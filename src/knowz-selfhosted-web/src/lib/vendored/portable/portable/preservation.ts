import { PortableCryptoError, type PortablePayload, type PortablePayloadReference } from '../portable.js';
import type { OpenedPortableArchive } from './archive.js';

export interface ArchiveInventoryBinding {
  archiveId: string; manifestSha256: string; sourceSnapshot: string; sourceTrust: string;
  requiredPayloadCount: number; preservationState: string; omissions: unknown[];
}
export interface ArchiveBindingOptions { allowPartial?: boolean; validateOnly?: boolean }

function canonical(value: unknown): string {
  return JSON.stringify(value, (_key, child) => child && typeof child === 'object' && !Array.isArray(child)
    ? Object.fromEntries(Object.keys(child).sort().map(key => [key, child[key]])) : child);
}
function includesOmissions(actual: unknown[], required: unknown[]): boolean {
  const counts = new Map<string, number>();
  for (const omission of actual) { const key = canonical(omission); counts.set(key, (counts.get(key) ?? 0) + 1); }
  for (const omission of required) { const key = canonical(omission); const count = counts.get(key) ?? 0; if (!count) return false; counts.set(key, count - 1); }
  return true;
}

/** A server response cannot replace the inventory or trust established by local inspection. */
export function validateArchivePlanBinding(source: OpenedPortableArchive, plan: ArchiveInventoryBinding, options: ArchiveBindingOptions = {}): void {
  const inventory = source.manifest.inventory;
  if (plan.archiveId !== inventory.archiveId || plan.manifestSha256 !== source.manifestSha256
    || plan.sourceSnapshot !== inventory.snapshotBoundary || plan.sourceTrust !== source.sourceTrust
    || plan.requiredPayloadCount !== inventory.payloadCounts.required || !Array.isArray(plan.omissions)
    || !includesOmissions(plan.omissions, inventory.omissions)
    || !['complete', 'partial', 'blocked'].includes(plan.preservationState)
    || plan.preservationState === 'complete' && (source.manifest.preservationState !== 'complete' || plan.omissions.length !== 0)
    || plan.preservationState === 'partial' && !options.allowPartial && !options.validateOnly)
    throw new PortableCryptoError('plan-mismatch', 'The import plan does not match the inspected archive or its accepted preservation outcome.');
}

export interface ArchivePreservationEvidence {
  complete: true; encryptedPayloadCount: number; plainRecordCount: number; binaryCount: number;
  dependentRecordCount: number; privateKeysRequired: false; reusedDestinationVaults: readonly string[];
  /** Index readiness belongs to the destination and is not certified by archive preservation. */
  destinationIndexState: 'not-verified';
}
export interface ArchivePreservationOptions { signal?: AbortSignal; reusedVaultIds?: readonly string[] }
const storageReferences: Record<string, string> = {
  contextId: 'context', vaultId: 'vault', parentVaultId: 'vault', primaryVaultId: 'vault', topicId: 'topic',
  knowledgeId: 'knowledge', sourceKnowledgeId: 'knowledge', targetKnowledgeId: 'knowledge', parentCommentId: 'comment',
  commentId: 'comment', fileRecordId: 'file', rootFileRecordId: 'file', previousFileRecordId: 'file',
  baseVersionId: 'version', previousVersionId: 'version', inboxId: 'inbox', locationId: 'location', organizerId: 'person',
};
const storageArrays: Record<string, string> = { vaultIds: 'vault', tagIds: 'tag', personIds: 'person', locationIds: 'location', eventIds: 'event' };
// These change under authorized destination ownership/import lifecycle; encrypted identities below do not.
const destinationOwnership = new Set(['tenantId', 'revision', 'updatedAt', 'importedAt', 'createdByUserId', 'createdById']);
const familyState: Record<string, ReadonlySet<string>> = {
  context: new Set(['provenanceStatus']),
  catalog: new Set(['lifecycle', 'sourceLifecycle']),
  'vault-context-link': new Set(['isPrimaryWriteContext', 'primaryWriteFingerprint', 'primaryWriteGeneration']),
};
const auditInstants = new Set(['createdAt', 'updatedAt', 'attachedAt', 'exportedAt']);
const envelopeFields = new Set(['ciphertext', 'encryptedContent', 'encryptedDek', 'encryptedDekNonce', 'dekNonce',
  'ephemeralPublicKey', 'contentNonce', 'encryptedFieldsJson', 'envelopeVersion', 'encryptionEnvelopeVersion',
  'binaryEntryPath', 'binaryFilePath', 'binaryContentBase64', 'cryptoBindingId', 'cryptoBindingVaultId']);
const datasets: Record<string, string> = { knowledgeItems: 'knowledge', comments: 'comment', fileRecords: 'file', plainVersions: 'version',
  vaults: 'vault', topics: 'topic', tags: 'tag', persons: 'person', locations: 'location', events: 'event', inboxItems: 'inbox' };
const record = (value: unknown): value is Record<string, unknown> => !!value && typeof value === 'object' && !Array.isArray(value);
function mismatch(category: string): never { throw new PortableCryptoError('preservation-mismatch', `The destination did not preserve the original ${category}.`); }

/** Exact source preservation is independent of whether a private key can decrypt either archive. */
export async function verifyArchivePreservation(source: OpenedPortableArchive, destination: OpenedPortableArchive,
  mapping: Readonly<Record<string, string>>, options: ArchivePreservationOptions = {}): Promise<ArchivePreservationEvidence> {
  const check = () => options.signal?.throwIfAborted(); check();
  const verified = new Map<OpenedPortableArchive, Set<string>>();
  const verifyBytes = async (archive: OpenedPortableArchive, path: string) => {
    const paths = verified.get(archive) ?? new Set<string>(); if (paths.has(path)) return;
    const reader = archive.streamBinary(path, options.signal).getReader();
    try { while (true) { check(); const next = await reader.read(); if (next.done) break; } paths.add(path); verified.set(archive, paths); }
    catch (error) { await reader.cancel(error).catch(() => {}); throw error; }
    finally { reader.releaseLock(); }
  };
  const reusedVaults = new Set(options.reusedVaultIds ?? []);
  if ([...reusedVaults].some(id => !Object.entries(mapping).some(([key, value]) => key.startsWith('vault:') && value === id))) mismatch('reviewed destination vault selection');
  if (source.manifest.preservationState !== 'complete' || destination.manifest.preservationState !== 'complete') mismatch('complete archive outcome');
  const mapped = (kind: string, id: unknown): unknown => {
    if (id == null) return id;
    if (typeof id !== 'string' || !mapping[`${kind}:${id}`]) mismatch('typed destination mapping');
    return mapping[`${kind}:${id}`];
  };
  const normalize = (value: unknown, kind: string, remap: boolean, encrypted = false, field = '', typed = true, family = kind): unknown => {
    if (value == null) return null;
    // Arbitrary nested metadata is customer data, not another portable record.
    // Compare its full JSON and type without remapping names or dropping state fields.
    if (!typed) return record(value) || Array.isArray(value)
      ? { opaqueJsonType: Array.isArray(value) ? 'array' : 'object', opaqueJson: canonical(value) } : value;
    if (Array.isArray(value)) return value.map(child => normalize(child, kind, remap, encrypted, field));
    if (!record(value)) return field === 'type' && typeof value === 'string' ? value.toLowerCase() : value;
    const output: Record<string, unknown> = {};
    for (const [name, child] of Object.entries(value)) {
      const referenceKind = storageReferences[name]; const arrayKind = storageArrays[name];
      if (destinationOwnership.has(name) || familyState[family]?.has(name) || encrypted && envelopeFields.has(name)) continue;
      if (child == null) continue; // An omitted optional field and its explicit null have the same typed value.
      if (name === 'id') output[name] = remap ? mapped(kind, child) : child;
      else if (referenceKind) output[name] = remap ? mapped(referenceKind, child) : child;
      else if (arrayKind && Array.isArray(child)) output[name] = [...new Set(child.map(id => remap ? mapped(arrayKind, id) : id))].sort();
      else if (name.endsWith('Links') && Array.isArray(child) && ['personLinks', 'locationLinks', 'eventLinks'].includes(name)) {
        const entityKind = name.slice(0, -5); output[name] = child.map(link => record(link)
          ? normalize({ ...link, entityId: remap ? mapped(entityKind, link.entityId) : link.entityId }, kind, false, encrypted) : link);
      } else if (name === 'relationships' && Array.isArray(child)) output[name] = child.map(link => normalize(link, 'relationship', remap, encrypted));
      else if (name === 'attachments' && kind === 'file' && Array.isArray(child)) output[name] = child.map(link => {
        if (!record(link)) mismatch('typed file attachment link');
        const { knowledgeId, commentId, ...content } = link;
        return { knowledgeId: knowledgeId == null ? null : remap ? mapped('knowledge', knowledgeId) : knowledgeId,
          commentId: commentId == null ? null : remap ? mapped('comment', commentId) : commentId,
          content: normalize(content, kind, false, false, '', false) };
      });
      else if (name === 'versions' && Array.isArray(child)) output[name] = child.map(version => normalize(version, 'version', remap, encrypted));
      else if (name === 'sealedSnapshot') output[name] = normalize(child, kind, false, encrypted);
      else if (auditInstants.has(name) && typeof child === 'string' && Number.isFinite(Date.parse(child))) output[name] = new Date(child).toISOString();
      else output[name] = normalize(child, kind, remap, encrypted, name, false);
    }
    return output;
  };
  const contains = (expected: unknown, actual: unknown): boolean => {
    if (expected == null) return actual == null;
    if (Array.isArray(expected)) return Array.isArray(actual) && expected.length === actual.length && expected.every((v, i) => contains(v, actual[i]));
    if (record(expected)) return record(actual) && Object.entries(expected).every(([key, value]) => contains(value, actual[key]));
    return expected === actual;
  };
  const compareRecord = (left: Record<string, unknown>, right: Record<string, unknown>, kind: string, encrypted: boolean, localIndexState = false, family = kind) => {
    if (localIndexState) {
      const content = (row: Record<string, unknown>) => {
        if (Object.prototype.hasOwnProperty.call(row, 'isIndexed') && typeof row.isIndexed !== 'boolean'
          || row.indexedAt != null && (typeof row.indexedAt !== 'string' || !Number.isFinite(Date.parse(row.indexedAt))))
          mismatch('typed destination index state');
        // Only these two root PortableKnowledge properties describe a derived index.
        // Nested customer metadata and all other source fields remain comparable.
        const { isIndexed: _indexed, indexedAt: _indexedAt, ...retained } = row;
        return retained;
      };
      left = content(left); right = content(right);
    }
    if (!contains(normalize(left, kind, true, encrypted, '', true, family), normalize(right, kind, false, encrypted, '', true, family))) mismatch('record content or relationships');
  };
  for (const key of source.graph.catalog) {
    check(); const candidate = destination.graph.catalog.find(value => value.contextId === mapped('context', key.contextId)
      && value.generation === key.generation && value.fingerprint === key.fingerprint);
    if (!candidate || candidate.publicKey !== key.publicKey || candidate.originalBindingId !== key.originalBindingId
      || key.custody != null && candidate.custody !== key.custody) mismatch('public key identity');
  }
  const publicRows = (archive: OpenedPortableArchive, collection: string) => archive.packages.flatMap(pkg => record(pkg) && Array.isArray(pkg[collection]) ? pkg[collection] as Record<string, unknown>[] : []);
  for (const context of publicRows(source, 'cryptoContexts')) {
    const target = publicRows(destination, 'cryptoContexts').find(value => value.contextId === mapped('context', context.contextId));
    if (!target) mismatch('public context inventory'); compareRecord(context, target, 'context', false);
  }
  for (const key of publicRows(source, 'cryptoKeyCatalog')) {
    const target = publicRows(destination, 'cryptoKeyCatalog').find(value => value.contextId === mapped('context', key.contextId)
      && value.generation === key.generation && value.fingerprint === key.fingerprint);
    if (!target) mismatch('public catalog inventory'); compareRecord(key, target, 'context', false, false, 'catalog');
  }
  for (const link of publicRows(source, 'vaultCryptoContextLinks')) {
    const target = publicRows(destination, 'vaultCryptoContextLinks').find(value => value.vaultId === mapped('vault', link.vaultId)
      && value.contextId === mapped('context', link.contextId));
    if (!target) mismatch('vault-context membership'); compareRecord(link, target, 'vault', false, false, 'vault-context-link');
  }
  const subject = (archive: OpenedPortableArchive, ref: PortablePayloadReference, fallback?: unknown): unknown => {
    if (ref.originalItemId != null) return ref.originalItemId;
    const row = archive.graph.records.find(r => r.ownerKind === ref.ownerKind && r.id === ref.ownerId)?.record;
    const originalField = ({ knowledge: 'originalItemId', comment: 'originalCommentId', file: 'originalFileId', version: 'originalKnowledgeId' } as Record<string, string>)[ref.ownerKind];
    return (originalField ? row?.[originalField] : undefined) ?? fallback ?? (ref.ownerKind === 'version' ? row?.knowledgeId : ref.ownerId);
  };
  const generation = (archive: OpenedPortableArchive, ref: PortablePayloadReference) => ref.generation != null ? [ref.generation]
    : archive.graph.catalog.filter(key => key.contextId === ref.contextId && (ref.fingerprint == null || key.fingerprint === ref.fingerprint)).map(key => key.generation).sort((a, b) => a - b);
  const slot = (payload: PortablePayload, remap: boolean) => {
    const ref = payload.reference;
    // Older version-owned declarations omitted the redundant versionId. Its owner
    // already identifies that version; an explicit different value remains distinct.
    const versionId = ref.versionId ?? (ref.ownerKind === 'version' ? ref.ownerId : null);
    return canonical([ref.ownerKind, remap ? mapped(ref.ownerKind, ref.ownerId) : ref.ownerId, ref.role, ref.fieldName ?? null,
      versionId ? remap ? mapped('version', versionId) : versionId : null]);
  };
  for (const payload of source.graph.payloads) {
    check(); const matches = destination.graph.payloads.filter(candidate => slot(candidate, false) === slot(payload, true));
    if (matches.length !== 1) mismatch('encrypted payload inventory'); const candidate = matches[0]!;
    const left = payload.reference; const right = candidate.reference;
    if (right.contextId !== mapped('context', left.contextId) || left.originalBindingId !== right.originalBindingId
      || left.originalBindingKind !== right.originalBindingKind || left.fingerprint !== right.fingerprint
      || left.algorithmProfile !== right.algorithmProfile || left.keyType !== right.keyType || left.envelopeVersion !== right.envelopeVersion
      || canonical(generation(source, left)) !== canonical(generation(destination, right)) || subject(source, left) !== subject(destination, right, subject(source, left))
      || canonical(payload.envelope) !== canonical(candidate.envelope)) mismatch('encrypted envelope or immutable key reference');
    if (payload.binaryEntryPath) {
      if (!candidate.binaryEntryPath) mismatch('encrypted binary member');
      const a = source.manifest.inventory.parts.find(part => part.path === payload.binaryEntryPath);
      const b = destination.manifest.inventory.parts.find(part => part.path === candidate.binaryEntryPath);
      if (!a || !b || a.sha256 !== b.sha256 || a.length !== b.length) mismatch('encrypted binary bytes');
      await verifyBytes(source, a.path); await verifyBytes(destination, b.path);
    }
  }
  for (const row of source.graph.records) {
    check(); const target = destination.graph.records.find(value => value.ownerKind === row.ownerKind && value.id === mapped(row.ownerKind, row.id));
    if (!target) mismatch('encrypted record inventory');
    const ordinaryKnowledge = row.ownerKind === 'knowledge' && source.graph.plainData.knowledgeItems?.some(value => record(value) && value.id === row.id);
    compareRecord(row.record, target.record, row.ownerKind, true, ordinaryKnowledge);
  }
  let plainRecordCount = 0; let binaryCount = 0;
  for (const [name, rows] of Object.entries(source.graph.plainData)) {
    if (name === 'archives') {
      for (const collection of rows) {
        check(); if (!record(collection) || !Array.isArray(collection.records)) mismatch('opaque archive collection');
        const target = destination.graph.plainData.archives?.find(value => record(value) && value.collection === collection.collection);
        if (!record(target) || !Array.isArray(target.records) || !includesOmissions(target.records, collection.records)) mismatch('opaque archive records');
        plainRecordCount += collection.records.length;
      }
      continue;
    }
    const kind = datasets[name]; if (!kind) { if (rows.length) mismatch('supported plain data inventory'); continue; }
    for (const row of rows) {
      check(); if (!record(row)) mismatch('plain record');
      const target = destination.graph.plainData[name]?.find(value => record(value) && value.id === mapped(kind, row.id));
      if (!record(target)) mismatch('plain record inventory');
      if (kind !== 'vault' || !reusedVaults.has(String(target.id))) compareRecord(row, target, kind, name === 'fileRecords', name === 'knowledgeItems');
      plainRecordCount++;
      if (name === 'fileRecords') {
        const a = source.manifest.inventory.parts.find(part => part.path === row.binaryEntryPath);
        const b = destination.manifest.inventory.parts.find(part => part.path === target.binaryEntryPath);
        if (!a || !b || a.sha256 !== b.sha256 || a.length !== b.length) mismatch('ordinary binary bytes');
        await verifyBytes(source, a.path); await verifyBytes(destination, b.path); binaryCount++;
      }
    }
  }
  for (const dependent of source.graph.dependencyBoundItems) {
    check(); const target = destination.graph.dependencyBoundItems.find(value => value.id === mapped('knowledge', dependent.id));
    if (!target) mismatch('dependent body inventory'); compareRecord(dependent as unknown as Record<string, unknown>, target as unknown as Record<string, unknown>, 'knowledge', false);
  }
  for (const policy of source.graph.suspendedReleasePolicies) {
    const target = destination.graph.suspendedReleasePolicies.find(value => value.originalLockId === policy.originalLockId && value.knowledgeId === mapped('knowledge', policy.knowledgeId));
    if (!target) mismatch('suspended policy inventory'); compareRecord(policy as unknown as Record<string, unknown>, target as unknown as Record<string, unknown>, 'knowledge', false);
  }
  return { complete: true, encryptedPayloadCount: source.graph.payloads.length, plainRecordCount,
    binaryCount: binaryCount + source.graph.payloads.filter(value => value.binaryEntryPath).length,
    dependentRecordCount: source.graph.dependencyBoundItems.length, privateKeysRequired: false, reusedDestinationVaults: [...reusedVaults], destinationIndexState: 'not-verified' };
}
