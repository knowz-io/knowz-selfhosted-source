import { useEffect, useRef, useState } from 'react';
import { PortableCryptoError, fingerprintPublicKey, PortableKeyring, PORTABLE_LIMITS, extractPortableGraph, decryptPayload, recoverLegacyPayload, encryptPayload, proveRecoveredCustody, type LegacyAttribution, rewriteEncryptedRecord, createEncryptedFileRecord, createEncryptedKnowledgeRecord, type PortableGraphRecord, type PortableKeyIdentity, type PortablePayloadReference } from '../../lib/vendored/portable/portable';
import { saveLocalDownload } from '../../lib/archive-browser';
import type { EncryptedRecordTransport, EncryptedRecordReply, EncryptedCatalogReply, LocalAiPolicyReply } from '../../lib/encrypted-record-api';
import './archive-workbench.css';
interface Props { vaultId: string; knowledgeId: string; scopeKey: string; transport: EncryptedRecordTransport; canEdit?: boolean; custodyActor?: { actorId: string; tenantId: string }; onLock?: () => void; onKnowledgeChanged?: () => void; onKnowledgeKeyReady?: (key: Uint8Array) => void; onDecryptionStateChange?: (value: boolean) => void }
const collections: Record<string, string> = { knowledge: 'sealedItems', comment: 'sealedComments', file: 'sealedFiles', attachment: 'sealedAttachments', version: 'sealedVersions' };
function graphFrom(reply: EncryptedRecordReply) {
  const collection = collections[reply.ownerKind];
  if (!collection || reply.record?.id !== reply.ownerId || !reply.revision) throw new PortableCryptoError('record-identity', 'The encrypted record identity or revision is invalid.');
  const plainFile = reply.ownerKind === 'file' && !reply.payloadKeyReferences.some(ref => ref.role === 'binary');
  const plainKnowledge = reply.ownerKind === 'knowledge' && !reply.record.ciphertext && !reply.payloadKeyReferences.some(ref => ref.role === 'body');
  const plainVersion = reply.ownerKind === 'version' && Object.prototype.hasOwnProperty.call(reply.record, 'snapshotJson');
  if (plainVersion && (typeof reply.record.snapshotJson !== 'string' || reply.record.ciphertext != null || reply.record.envelopeMetadata != null || reply.payloadKeyReferences.some(ref => ref.role === 'body')))
    throw new PortableCryptoError('record-shape', 'The ordinary history snapshot has a contradictory encrypted body or invalid metadata.');
  const record = reply.ownerKind === 'file' && !plainFile && !reply.record.binaryContentBase64 ? { ...reply.record, binaryEntryPath: `files/${reply.ownerId}.bin` } : reply.record;
  const graph = extractPortableGraph({ schemaVersion: 5, requiredCapabilities: ['knowz.encrypted-graph/v1'], preservationState: reply.payloadKeyReferences.some(ref => ref.legacyUnattributed) ? 'partial' : 'complete', data: plainFile ? { fileRecords: [record] } : plainKnowledge ? { knowledgeItems: [record] } : {}, cryptoContexts: reply.cryptoContexts, cryptoKeyCatalog: reply.cryptoKeyCatalog, payloadKeyReferences: reply.payloadKeyReferences, ...(plainVersion ? { plainVersions: [record] } : !plainFile && !plainKnowledge ? { [collection]: [record] } : {}) }, { detachedRecord: true });
  if ((plainKnowledge || plainVersion) && !graph.records.some(value => value.ownerKind === reply.ownerKind && value.id === reply.ownerId)) graph.records.push({ ownerKind: reply.ownerKind, id: reply.ownerId, record, payloads: [] });
  return graph;
}
const keyId = (key: PortableKeyIdentity) => `${key.contextId}:${key.generation}:${key.fingerprint}`;
const originalKeyId = (key: PortableKeyIdentity) => `${key.contextId}:${key.fingerprint}`;
export function EncryptedRecordWorkspace({ vaultId, knowledgeId, scopeKey, transport, canEdit = true, custodyActor, onLock, onKnowledgeChanged, onKnowledgeKeyReady, onDecryptionStateChange }: Props) {
  const [catalog, setCatalog] = useState<EncryptedCatalogReply | null>(null); const [keys, setKeys] = useState<PortableKeyIdentity[]>([]);
  const [reply, setReply] = useState<EncryptedRecordReply | null>(null); const [row, setRow] = useState<PortableGraphRecord | null>(null);
  const [related, setRelated] = useState<Array<{ kind: string; id: string }>>([]); const [selection, setSelection] = useState(`knowledge:${knowledgeId}`);
  const [chosenKey, setChosenKey] = useState(''); const [unlocked, setUnlocked] = useState<string[]>([]);
  const [allowLegacy, setAllowLegacy] = useState(false); const [legacyAttributions, setLegacyAttributions] = useState<LegacyAttribution[]>([]);
  const [custodyConsent, setCustodyConsent] = useState(false);
  const [privateText, setPrivateText] = useState(''); const [fileLoaded, setFileLoaded] = useState(false); const [passphrase, setPassphrase] = useState(''); const [derivation, setDerivation] = useState('');
  const [aiPolicy, setAiPolicy] = useState<LocalAiPolicyReply | null>(null); const [aiInstruction, setAiInstruction] = useState(''); const [aiConsent, setAiConsent] = useState(false); const [aiDraft, setAiDraft] = useState<string | null>(null); const [aiEmptyConsent, setAiEmptyConsent] = useState(false);
  const clearLocalAi = () => { setAiPolicy(null); setAiInstruction(''); setAiConsent(false); setAiDraft(null); setAiEmptyConsent(false); };
  const [ordinaryDraft, setOrdinaryDraft] = useState<string | null>(null);
  const [body, setBody] = useState<string | null>(null); const [fields, setFields] = useState<Record<string, string>>({}); const original = useRef<{ body: string | null; fields: Record<string, string> }>({ body: null, fields: {} });
  const [busy, setBusy] = useState('Loading encryption context…'); const [error, setError] = useState(''); const [status, setStatus] = useState(''); const [deleteConfirm, setDeleteConfirm] = useState(false); const [comment, setComment] = useState('');
  const [attachment, setAttachment] = useState<File | null>(null); const [replacement, setReplacement] = useState<File | null>(null); const [newTitle, setNewTitle] = useState(''); const [newBody, setNewBody] = useState('');
  const ring = useRef<PortableKeyring | null>(null); const epoch = useRef(0); const controller = useRef<AbortController | null>(null); const currentRevision = useRef(''); const originalKnowledgeId = useRef(knowledgeId);
  const currentBodyShape = useRef<'ordinary' | 'encrypted' | null>(null);
  const keyInput = useRef<HTMLInputElement>(null);
  const clearPlaintext = () => { clearLocalAi(); setOrdinaryDraft(null); setLegacyAttributions([]); setBody(null); setFields({}); original.current = { body: null, fields: {} }; setComment(''); setAttachment(null); setReplacement(null); setNewTitle(''); setNewBody(''); onDecryptionStateChange?.(false); };
  const clearSecrets = () => { setPrivateText(''); setFileLoaded(false); setPassphrase(''); setDerivation(''); if (keyInput.current) keyInput.current.value = ''; };
  const lock = () => { ++epoch.current; controller.current?.abort(); controller.current = null; ring.current?.lock(); clearPlaintext(); clearSecrets(); setBusy(''); setStatus('Original keys and decrypted content cleared.'); };
  const load = async (kind: string, id: string, signal: AbortSignal, alive: () => boolean) => {
    const result = await transport.payload(vaultId, kind, id, signal);
    if (!alive()) return;
    if (result.vaultId !== vaultId || result.ownerId !== id || result.ownerKind !== kind) throw new PortableCryptoError('record-identity', 'The server returned a different encrypted record.');
    const graph = graphFrom(result);
    const fresh = await transport.contexts(vaultId, signal); if (!alive()) return;
    if (fresh.vaultId !== vaultId || !fresh.revision) throw new PortableCryptoError('context-identity', 'The server returned a different encryption context.');
    const freshGraph = extractPortableGraph({ schemaVersion: 5, requiredCapabilities: ['knowz.encrypted-graph/v1'], data: {}, cryptoContexts: fresh.cryptoContexts, cryptoKeyCatalog: fresh.cryptoKeyCatalog, payloadKeyReferences: [] });
    const physical = (values: PortableKeyIdentity[]) => JSON.stringify(values.map(value => [value.contextId, value.originalBindingId, value.generation, value.fingerprint, value.publicKey]).sort());
    if (ring.current && physical(ring.current.snapshot().catalog) !== physical(freshGraph.catalog)) {
      ring.current.dispose(); const session = new PortableKeyring({ userId: scopeKey, tenantId: vaultId, sessionId: crypto.randomUUID() }, freshGraph.catalog); ring.current = session;
      session.subscribe(() => { if (ring.current === session) { setUnlocked(session.snapshot().unlocked); clearPlaintext(); } }); setUnlocked([]); setChosenKey(freshGraph.catalog[0] ? keyId(freshGraph.catalog[0]) : '');
    }
    setCatalog(fresh); setKeys(freshGraph.catalog); setCustodyConsent(false);
    const required = graph.payloads.find(payload => payload.reference.role === 'body' || payload.reference.role === 'binary')?.reference ?? graph.payloads[0]?.reference;
    const preferred = required && freshGraph.catalog.find(key => key.contextId === required.contextId && key.fingerprint === required.fingerprint && (required.generation == null || key.generation === required.generation));
    if (preferred) setChosenKey(current => graph.payloads.some(payload => freshGraph.catalog.some(key => keyId(key) === current && key.contextId === payload.reference.contextId && key.fingerprint === payload.reference.fingerprint && (payload.reference.generation == null || key.generation === payload.reference.generation))) ? current : keyId(preferred));
    setReply(result); setRow(graph.records[0] ?? null); clearPlaintext(); setDeleteConfirm(false);
    if (kind === 'knowledge') {
      currentBodyShape.current = graph.payloads.some(payload => payload.reference.role === 'body') ? 'encrypted' : 'ordinary';
      currentRevision.current = result.revision; originalKnowledgeId.current = typeof result.record.originalItemId === 'string' ? result.record.originalItemId : knowledgeId;
      const relatives: Array<{ kind: string; id: string }> = [];
      for (const [name, relatedKind] of Object.entries({ comments: 'comment', files: 'file', attachments: 'attachment', versions: 'version' })) {
        const ids = result.related?.[name as keyof NonNullable<EncryptedRecordReply['related']>] ?? [];
        if (!Array.isArray(ids) || ids.length > PORTABLE_LIMITS.members || ids.some(value => typeof value !== 'string' || !/^[0-9a-f-]{36}$/i.test(value))) throw new PortableCryptoError('record-relations', 'The encrypted record relationship inventory is invalid.');
        relatives.push(...ids.map(value => ({ kind: relatedKind, id: value })));
      }
      setRelated(relatives);
    }
  };
  const run = async (label: string, work: (signal: AbortSignal, alive: () => boolean) => Promise<void>) => {
    if (controller.current) return;
    const abort = new AbortController(); controller.current = abort; const version = ++epoch.current; const alive = () => epoch.current === version && !abort.signal.aborted;
    setBusy(label); setError(''); setStatus('');
    try { await work(abort.signal, alive); }
    catch (failure) { if (alive()) { ring.current?.lock(); clearPlaintext(); setError(failure instanceof PortableCryptoError ? failure.message : 'Could not use this encrypted record. Check your key, access and connection.'); } }
    finally { if (alive()) { controller.current = null; setBusy(''); clearSecrets(); } }
  };
  useEffect(() => {
    lock(); ring.current?.dispose(); ring.current = null; currentBodyShape.current = null; setReply(null); setRow(null); setCatalog(null); setKeys([]); setAllowLegacy(false); setRelated([]); setSelection(`knowledge:${knowledgeId}`); setError('');
    void run('Loading original encryption context…', async (signal, alive) => {
      const value = await transport.contexts(vaultId, signal); if (!alive()) return;
      const graph = extractPortableGraph({ schemaVersion: 5, requiredCapabilities: ['knowz.encrypted-graph/v1'], data: {}, cryptoContexts: value.cryptoContexts, cryptoKeyCatalog: value.cryptoKeyCatalog, payloadKeyReferences: [] });
      const session = new PortableKeyring({ userId: scopeKey, tenantId: vaultId, sessionId: crypto.randomUUID() }, graph.catalog);
      ring.current = session; session.subscribe(() => { if (ring.current === session) { setUnlocked(session.snapshot().unlocked); clearPlaintext(); } });
      setCatalog(value); setKeys(graph.catalog); setChosenKey(graph.catalog[0] ? keyId(graph.catalog[0]) : '');
      await load('knowledge', knowledgeId, signal, alive);
    });
    return () => { ++epoch.current; controller.current?.abort(); controller.current = null; ring.current?.dispose(); ring.current = null; onDecryptionStateChange?.(false); };
  }, [scopeKey, vaultId, knowledgeId, transport]);
  const selectedIdentity = keys.find(key => keyId(key) === chosenKey);
  const custodyAccepted = !!selectedIdentity && !!catalog?.acceptedCustodyKeys?.some(key => key.contextId === selectedIdentity.contextId && key.generation === selectedIdentity.generation && key.fingerprint === selectedIdentity.fingerprint);
  const eligible = !!selectedIdentity && (selectedIdentity.sourceLifecycle ?? selectedIdentity.lifecycle) === 'active' && (selectedIdentity.custody === 'customer-held-public-only' || !selectedIdentity.custody && selectedIdentity.lifecycle === 'active' || custodyAccepted);
  const identity = selectedIdentity && eligible ? { ...selectedIdentity, lifecycle: 'active' } : selectedIdentity;
  const anchor = catalog?.importAnchors?.find(value => value.contextId === identity?.contextId);
  const canEditRow = canEdit && !(row?.ownerKind === 'file' && row.record.isCurrentVersion === false);
  const acceptCustody = () => identity && anchor && custodyActor && custodyConsent && run('Verifying possession of this original key…', async (signal, alive) => {
    const reference: PortablePayloadReference = { ownerKind: 'knowledge', ownerId: knowledgeId, role: 'body', contextId: identity.contextId, generation: identity.generation, fingerprint: identity.fingerprint, originalBindingKind: 'vault', originalBindingId: identity.originalBindingId, keyType: 'x25519-vault-public', algorithmProfile: 'x25519-vault', envelopeVersion: 2 };
    const challenge = await transport.createCustodyChallenge(vaultId, identity.contextId, { planId: anchor.planId, generation: identity.generation, fingerprint: identity.fingerprint }, signal);
    const proof = await ring.current!.withKey(reference, key => proveRecoveredCustody(challenge, { actorId: custodyActor.actorId, destinationTenantId: custodyActor.tenantId, destinationVaultId: vaultId, contextId: identity.contextId, originalBindingId: identity.originalBindingId, generation: identity.generation, fingerprint: identity.fingerprint, planId: anchor.planId }, key, { signal, isCurrent: alive }), signal);
    if (!alive()) return;
    const result = await transport.acceptRecoveredCustody(vaultId, identity.contextId, proof, signal);
    if (result.contextId !== identity.contextId || result.generation !== identity.generation || result.fingerprint !== identity.fingerprint || result.effectiveCustody !== 'customer-held-local' || result.keyPossession !== 'canary-verified' || result.archiveRecovery !== 'not-verified' || !['trusted', 'unverified'].includes(result.sourceTrust)) throw new PortableCryptoError('custody-response', 'The custody acceptance does not match this original key. Reload before continuing.');
    const next = await transport.contexts(vaultId, signal); if (!alive()) return;
    if (next.vaultId !== vaultId || !next.acceptedCustodyKeys?.some(key => key.contextId === identity.contextId && key.generation === identity.generation && key.fingerprint === identity.fingerprint)) throw new PortableCryptoError('custody-response', 'Fresh local custody consent is unavailable. Reload before continuing.');
    setCatalog(next); setKeys(extractPortableGraph({ schemaVersion: 5, requiredCapabilities: ['knowz.encrypted-graph/v1'], data: {}, cryptoContexts: next.cryptoContexts, cryptoKeyCatalog: next.cryptoKeyCatalog, payloadKeyReferences: [] }).catalog); setCustodyConsent(false);
    setStatus('Possession of this original generation verified. Full archive recovery is not verified. Select this generation for new writes to continue.');
  });
  const decrypt = () => row && run('Decrypting body and fields locally…', async (signal, alive) => {
    const values: Record<string, string> = {}; let content: string | null = null; const attributions: LegacyAttribution[] = [];
    for (const payload of row.payloads.filter(value => value.reference.role !== 'binary' && value.reference.role !== 'variant')) {
      let bytes: Uint8Array;
      if (payload.reference.legacyUnattributed) { const recovered = await recoverLegacyPayload(payload, ring.current!, { candidates: allowLegacy && identity ? [identity] : [], signal }); bytes = recovered.plaintext; attributions.push(recovered.attribution); }
      else bytes = await decryptPayload(payload, ring.current!, { signal });
      try { const value = new TextDecoder('utf-8', { fatal: true }).decode(bytes); if (payload.reference.role === 'body') content = value; else values[payload.reference.fieldName!] = value; } finally { bytes.fill(0); }
    }
    const bodyPayload = row.payloads.find(payload => payload.reference.role === 'body');
    const recoveredBody = attributions.find(value => value.role === 'body');
    const bodyReference = recoveredBody && bodyPayload ? { ...bodyPayload.reference, fingerprint: recoveredBody.fingerprint, generation: recoveredBody.generation ?? recoveredBody.possibleGenerations[0] } : bodyPayload?.reference;
    if (row.ownerKind === 'knowledge' && bodyReference && onKnowledgeKeyReady) await ring.current!.withKey(bodyReference, async key => { if (alive()) onKnowledgeKeyReady(key); }, signal);
    if (alive()) { setLegacyAttributions(attributions); setBody(content); setFields(values); original.current = { body: content, fields: { ...values } }; if (row.ownerKind === 'knowledge') onDecryptionStateChange?.(content !== null); }
  });
  const save = () => row && reply && identity && run('Encrypting and saving changes…', async (signal, alive) => {
    if (!unlocked.includes(originalKeyId(identity))) throw new PortableCryptoError('missing-key', 'Unlock the chosen original generation before editing.');
    const changedFields = Object.fromEntries(Object.entries(fields).filter(([name, value]) => value !== original.current.fields[name]));
    const rewritten = await rewriteEncryptedRecord(row, identity, { body: body !== original.current.body && body !== null ? body : undefined, fields: changedFields });
    if (row.ownerKind === 'knowledge' && typeof row.record.content === 'string' && !row.payloads.some(payload => payload.reference.role === 'body') && ordinaryDraft !== null) rewritten.record.content = ordinaryDraft;
    signal.throwIfAborted(); if (!alive()) return;
    await transport.write(row.ownerKind, row.id, { ...rewritten, vaultId }, reply.revision, signal);
    if (!alive()) return; if (row.ownerKind === 'knowledge') onKnowledgeChanged?.(); await load(row.ownerKind, row.id, signal, alive); if (alive()) setStatus('Saved with fresh encrypted envelopes. Decrypt the refreshed record to continue.');
  });
  const download = () => row && run('Decrypting file locally…', async (signal, alive) => {
    const payload = row.payloads.find(value => value.reference.role === 'binary' || value.reference.role === 'variant');
    const ciphertext = await transport.file(vaultId, row.id, signal); let bytes: Uint8Array | undefined;
    try { if (!payload) bytes = ciphertext.slice(); else if (payload.reference.legacyUnattributed) { const recovered = await recoverLegacyPayload(payload, ring.current!, { candidates: allowLegacy && identity ? [identity] : [], binary: ciphertext, signal }); bytes = recovered.plaintext; if (alive()) setLegacyAttributions([recovered.attribution]); } else bytes = await decryptPayload(payload, ring.current!, { binary: ciphertext, signal }); if (alive()) saveLocalDownload(new Blob([new Uint8Array(bytes)], { type: 'application/octet-stream' }), fields.fileName || `download-${row.id}.bin`); }
    finally { ciphertext.fill(0); bytes?.fill(0); }
  });
  const selectPrimary = () => identity && catalog && run('Selecting original write generation…', async (signal, alive) => {
    if (!unlocked.includes(originalKeyId(identity))) throw new PortableCryptoError('missing-key', 'Unlock this original generation before selecting it for writing.');
    await transport.selectPrimary(vaultId, identity.contextId, identity.fingerprint, catalog.revision, signal, identity.generation);
    const next = await transport.contexts(vaultId, signal); if (!alive()) return;
    const graph = extractPortableGraph({ schemaVersion: 5, requiredCapabilities: ['knowz.encrypted-graph/v1'], data: {}, cryptoContexts: next.cryptoContexts, cryptoKeyCatalog: next.cryptoKeyCatalog, payloadKeyReferences: [] });
    setCatalog(next); setKeys(graph.catalog); setStatus('Original write generation explicitly selected. Historical keys remain available for decryption.');
  });
  const addComment = () => identity && reply && catalog && run('Encrypting comment locally…', async (signal, alive) => {
    if (identity.lifecycle !== 'active' || !unlocked.includes(originalKeyId(identity))) throw new PortableCryptoError('write-key', 'Select and unlock an active original generation.');
    const id = crypto.randomUUID(); const reference: PortablePayloadReference = { ownerKind: 'comment', ownerId: id, originalItemId: id, role: 'body', contextId: identity.contextId, fingerprint: identity.fingerprint, generation: identity.generation, originalBindingKind: 'vault', originalBindingId: identity.originalBindingId, keyType: 'x25519-vault-public', algorithmProfile: 'x25519-vault', envelopeVersion: 2 };
    const bytes = new TextEncoder().encode(comment); let sealed;
    try { sealed = await encryptPayload(bytes, reference, identity); } finally { bytes.fill(0); }
    const envelope = sealed.envelope; signal.throwIfAborted(); if (!alive()) return;
    await transport.createComment(knowledgeId, { vaultId, record: { id, originalCommentId: id, knowledgeId, originalKnowledgeId: originalKnowledgeId.current, ciphertext: envelope.ciphertext, encryptedDek: envelope.encryptedDek, encryptedDekNonce: envelope.dekNonce, ephemeralPublicKey: envelope.ephemeralPublicKey, contentNonce: envelope.contentNonce, envelopeVersion: 2, cryptoBindingId: identity.originalBindingId }, payloadKeyReferences: [reference] }, signal, catalog.revision);
    if (alive()) { setComment(''); setSelection(`knowledge:${knowledgeId}`); await load('knowledge', knowledgeId, signal, alive); setStatus('Encrypted comment saved.'); }
  });
  const addFile = () => attachment && identity && catalog && run('Encrypting and attaching file…', async (signal, alive) => {
    if (!unlocked.includes(originalKeyId(identity))) throw new PortableCryptoError('missing-key', 'Unlock the original write generation first.');
    if (attachment.size > PORTABLE_LIMITS.encryptedFileBytes - 16) throw new PortableCryptoError('file-size', 'The file must fit within 64 MiB including its authentication tag.');
    const bytes = new Uint8Array(await attachment.arrayBuffer()); let encrypted;
    try { encrypted = await createEncryptedFileRecord({ id: crypto.randomUUID(), vaultId, knowledgeId, name: attachment.name, contentType: attachment.type || 'application/octet-stream', bytes, identity }); }
    finally { bytes.fill(0); }
    try {
      signal.throwIfAborted(); if (!alive()) return;
      await transport.stageFile(vaultId, { record: encrypted.record, payloadKeyReferences: encrypted.payloadKeyReferences }, encrypted.binary, catalog.revision, signal);
      if (alive()) { setSelection(`knowledge:${knowledgeId}`); await load('knowledge', knowledgeId, signal, alive); setStatus('Encrypted file attached. Its name and content type are encrypted too.'); }
    } finally { encrypted.binary.fill(0); }
  });
  const replaceFile = () => replacement && row?.ownerKind === 'file' && identity && reply && run('Encrypting replacement file…', async (signal, alive) => {
    if (!unlocked.includes(originalKeyId(identity))) throw new PortableCryptoError('missing-key', 'Unlock the original write generation first.');
    if (replacement.size > PORTABLE_LIMITS.encryptedFileBytes - (row.payloads.some(payload => payload.reference.role === 'binary') ? 16 : 0)) throw new PortableCryptoError('file-size', 'The replacement file must fit within the 64 MiB browser limit, including any authentication tag.');
    const bytes = new Uint8Array(await replacement.arrayBuffer()); let changed;
    try { changed = await rewriteEncryptedRecord(row, identity, { binary: bytes, fields: Object.fromEntries(row.payloads.filter(payload => payload.reference.role === 'field' && ['fileName', 'contentType'].includes(payload.reference.fieldName ?? '')).map(payload => [payload.reference.fieldName!, payload.reference.fieldName === 'fileName' ? replacement.name : replacement.type || 'application/octet-stream'])) }); }
    finally { bytes.fill(0); }
    try { signal.throwIfAborted(); if (!alive() || !changed.binary) return; const result = await transport.replaceFile(row.id, { ...changed, vaultId }, changed.binary, reply.revision, signal) as { ownerId?: string; record?: { id?: string } }; const nextId = result?.ownerId ?? result?.record?.id; if (!nextId || nextId === row.id || !/^[0-9a-f-]{36}$/i.test(nextId)) throw new PortableCryptoError('file-history', 'The replacement did not return a new file version. Reload before continuing.'); if (alive()) { await load('knowledge', knowledgeId, signal, alive); setSelection(`file:${nextId}`); await load('file', nextId, signal, alive); setStatus('File replaced as a new version. Earlier bytes remain available in history.'); } }
    finally { changed.binary?.fill(0); }
  });
  const addNote = () => identity && catalog && run('Encrypting new note…', async (signal, alive) => {
    if (!unlocked.includes(originalKeyId(identity))) throw new PortableCryptoError('missing-key', 'Unlock the original write generation first.');
    const created = await createEncryptedKnowledgeRecord({ id: crypto.randomUUID(), vaultId, title: newTitle, body: newBody, identity });
    signal.throwIfAborted(); if (!alive()) return; await transport.createItem(created, signal, catalog.revision); if (alive()) await load(row?.ownerKind ?? 'knowledge', row?.id ?? knowledgeId, signal, alive);
    if (alive()) { setNewTitle(''); setNewBody(''); setStatus(`Encrypted note created in this vault (${created.record.id}). Refresh the vault to open it.`); }
  });
  const aiFailure = () => new PortableCryptoError('local-ai-binding', 'The local AI response does not match this document, revision and consent. Reload before continuing.');
  const reviewLocalAi = () => reply && row?.ownerKind === 'knowledge' && run('Checking the configured local AI option…', async (signal, alive) => {
    clearLocalAi();
    const value = transport.localAiPolicy ? await transport.localAiPolicy(knowledgeId, vaultId, signal) : { supported: false, reason: 'edition-unsupported' };
    if (!alive()) return;
    const mode = row.payloads.some(payload => payload.reference.role === 'body') ? 'encrypted' : 'ordinary';
    if (!value || typeof value.supported !== 'boolean' || value.supported && (value.itemRevision !== reply.revision || value.bodyMode !== mode || !value.provider || !/^https?:\/\//.test(value.provider.displayOrigin) || !value.provider.model || !/^[a-f0-9]{64}$/.test(value.provider.policyRevision) || !value.limits || value.limits.inputBytes !== 65536 || value.limits.instructionBytes !== 4096 || value.limits.outputBytes !== 65536 || value.limits.timeoutSeconds !== 120)) throw aiFailure();
    setAiPolicy(value);
  });
  const generateLocalAi = () => reply && row?.ownerKind === 'knowledge' && identity && aiPolicy?.supported && aiPolicy.provider && aiConsent && run('Requesting the consented local draft…', async (signal, alive) => {
    const primary = catalog?.vaultCryptoContextLinks.some(link => link.vaultId === vaultId && link.contextId === identity.contextId && link.isPrimaryWriteContext === true && link.primaryWriteFingerprint === identity.fingerprint && link.primaryWriteGeneration === identity.generation);
    if (!primary || !eligible || !unlocked.includes(originalKeyId(identity)) || !transport.createLocalAiChallenge || !transport.runLocalAi) throw new PortableCryptoError('local-ai-key', 'Select and unlock the active original write generation before using local AI.');
    const mode = row.payloads.some(payload => payload.reference.role === 'body') ? 'encrypted' : 'ordinary';
    const protectedContent = row.payloads.some(payload => payload.reference.role === 'field' && payload.reference.fieldName === 'content');
    const content = mode === 'encrypted' ? body : protectedContent ? fields.content : ordinaryDraft ?? row.record.content;
    if (typeof content !== 'string' || !aiInstruction.trim()) throw aiFailure();
    const inputBytes = new TextEncoder().encode(content), instructionBytes = new TextEncoder().encode(aiInstruction);
    let inputSha256: string, instructionSha256: string;
    try { if (inputBytes.length > 65536 || instructionBytes.length > 4096) throw new PortableCryptoError('local-ai-size', 'Local AI supports a 64 KiB document and a 4 KiB instruction.'); inputSha256 = fingerprintPublicKey(inputBytes); instructionSha256 = fingerprintPublicKey(instructionBytes); }
    finally { inputBytes.fill(0); instructionBytes.fill(0); }
    const providerPolicyRevision = aiPolicy.provider!.policyRevision;
    setAiConsent(false); setAiDraft(null); setAiEmptyConsent(false);
    const challenge = await transport.createLocalAiChallenge(knowledgeId, { vaultId, operation: 'amend-body', contextId: identity.contextId, fingerprint: identity.fingerprint, generation: identity.generation, providerPolicyRevision, inputSha256, instructionSha256 }, reply.revision, signal);
    if (!alive()) return;
    const binding = challenge?.consentBinding, owner = binding?.owner;
    if (!/^[a-f0-9-]{36}$/i.test(challenge?.challengeId ?? '') || !(Date.parse(challenge.expiresAt) > Date.now()) || !binding || binding.operation !== 'amend-body' || binding.providerPolicyRevision !== providerPolicyRevision || binding.inputSha256 !== inputSha256 || binding.instructionSha256 !== instructionSha256 || !owner || owner.itemId !== knowledgeId || owner.vaultId !== vaultId || owner.itemRevision !== reply.revision || owner.bodyMode !== mode || owner.contextId !== identity.contextId || owner.fingerprint !== identity.fingerprint || owner.generation !== identity.generation || !/^[a-f0-9]{64}$/.test(owner.publicStateSha256) || custodyActor && (owner.actorId !== custodyActor.actorId || owner.tenantId !== custodyActor.tenantId)) throw aiFailure();
    const result = await transport.runLocalAi(knowledgeId, { vaultId, challengeId: challenge.challengeId, consent: true, content, instruction: aiInstruction }, reply.revision, signal);
    if (!alive()) return;
    if (!result || result.operationId !== challenge.challengeId || result.itemRevision !== reply.revision || result.providerPolicyRevision !== providerPolicyRevision || result.saved !== false || typeof result.content !== 'string') throw aiFailure();
    const bytes = new TextEncoder().encode(result.content); try { if (bytes.length > 65536) throw aiFailure(); } finally { bytes.fill(0); }
    setAiDraft(result.content); setAiInstruction(''); setStatus('Review the local draft. Nothing has been saved.');
  });
  const decrypted = body !== null || Object.keys(fields).length > 0;
  const plainHistory = row?.ownerKind === 'version' && typeof row.record.snapshotJson === 'string';
  const matchingRestoreShape = row?.ownerKind === 'version' && currentBodyShape.current === (plainHistory ? 'ordinary' : 'encrypted');
  const ordinaryBody = row && !row.payloads.some(payload => payload.reference.role === 'body' || payload.reference.role === 'field' && payload.reference.fieldName === 'content') && typeof row.record.content === 'string' ? row.record.content : null;
  return <section className="archive-workbench" aria-label="Encrypted record workspace">
    {busy && <p role="status">{busy}</p>}{error && <p role="alert" className="archive-error">{error}</p>}{status && <p role="status">{status}</p>}
    {(busy || reply) && <div className="archive-actions"><button type="button" onClick={() => { lock(); onLock?.(); }}>{busy ? 'Cancel and lock' : 'Lock original keys'}</button><button type="button" disabled={!!busy} onClick={() => void run('Refreshing encrypted record…', (signal, alive) => load(row?.ownerKind ?? 'knowledge', row?.id ?? knowledgeId, signal, alive))}>Reload encrypted record</button></div>}
    {reply && row && <><div className="archive-panel"><h3>Original encrypted content</h3><p>Original vault keys decrypt imported content locally. Destination permissions still apply. Keys and decrypted content clear when you lock or switch accounts; unused keys expire automatically.</p>
      <label>Record<select aria-label="Record" value={selection} disabled={!!busy} onChange={event => { const value = event.target.value; setSelection(value); const [kind, id] = value.split(':'); void run('Loading related encrypted record…', (signal, alive) => load(kind, id, signal, alive)); }}><option value={`knowledge:${knowledgeId}`}>Current knowledge</option>{related.map(value => <option key={`${value.kind}:${value.id}`} value={`${value.kind}:${value.id}`}>{value.kind} · {value.id}</option>)}</select></label>
      <label>Original key generation<select value={chosenKey} disabled={!!busy} onChange={e => { setChosenKey(e.target.value); setAllowLegacy(false); setCustodyConsent(false); }}>{keys.map(key => <option key={keyId(key)} value={keyId(key)}>Generation {key.generation} · {key.lifecycle} · {key.contextId} · {key.fingerprint.slice(0, 12)}</option>)}</select></label>
      {identity && <p><code>{identity.fingerprint}</code><br />{unlocked.includes(originalKeyId(identity)) ? 'Unlocked for this session' : 'Original private key required'}</p>}
      <label>Private key file<input ref={keyInput} type="file" disabled={!!busy} onChange={async e => { const file = e.target.files?.[0]; e.target.value = ''; if (!file) return; if (file.size > PORTABLE_LIMITS.keyBytes) { setError('Key files are limited to 64 KiB.'); return; } const version = epoch.current; const text = await file.text(); if (epoch.current === version) { setPrivateText(text); setFileLoaded(true); } }} /></label>
      {fileLoaded ? <p>Key file loaded (value hidden)</p> : <label>Private key or recovery phrase<textarea autoComplete="off" spellCheck={false} value={privateText} disabled={!!busy} onChange={e => setPrivateText(e.target.value)} /></label>}
      <label>Protected key file passphrase<input type="password" autoComplete="off" value={passphrase} disabled={!!busy} onChange={e => setPassphrase(e.target.value)} /></label>
      <details><summary>Optional recovery derivation passphrase</summary><label>Original derivation passphrase<input type="password" autoComplete="off" value={derivation} disabled={!!busy} onChange={e => setDerivation(e.target.value)} /></label></details>
      <button type="button" disabled={!!busy || !privateText || !identity} onClick={() => identity && void run('Unlocking original key…', async (signal, alive) => { await ring.current!.importKey(privateText, { contextId: identity.contextId, passphrase, derivationPassphrase: derivation, signal }); if (alive()) setStatus('Original key unlocked in this session.'); })}>Unlock original generation</button>
      {canEdit && <button type="button" disabled={!!busy || !identity || !unlocked.includes(originalKeyId(identity)) || !eligible} onClick={() => void selectPrimary()}>Use this original generation for new writes</button>}
      {canEdit && identity && anchor && custodyActor && identity.custody && identity.custody !== 'customer-held-public-only' && !custodyAccepted && <div className="archive-panel"><h4>Accept local custody of this original key</h4><p>The original source custody remains {identity.custody}. Proving this generation is in your possession records separate consent in this account. It does not verify the full archive or change source trust.</p><label className="archive-check"><input type="checkbox" checked={custodyConsent} disabled={!!busy} onChange={event => setCustodyConsent(event.target.checked)} />I hold this original private key and accept local custody of this generation</label><button type="button" disabled={!!busy || !custodyConsent || !unlocked.includes(originalKeyId(identity))} onClick={() => void acceptCustody()}>Verify key possession and accept local custody</button></div>}
      {row.payloads.some(payload => payload.reference.legacyUnattributed) && <div><p>This older record has incomplete key attribution. An explicit trial uses only the selected original key and its declared version 1 format; it does not try newer formats.</p><label className="archive-check"><input type="checkbox" checked={allowLegacy} disabled={!!busy} onChange={event => setAllowLegacy(event.target.checked)} />Use the selected original key for legacy recovery</label></div>}
    </div><div className="archive-panel"><h3>{row.ownerKind === 'version' ? 'Encrypted history snapshot' : 'Body and encrypted fields'}</h3>
      {row.ownerKind === 'file' && row.record.isCurrentVersion === false && <p role="status">Historical file version {String(row.record.fileVersion ?? 1)} is read-only. Its original bytes and encrypted metadata remain available.</p>}
      <button type="button" disabled={!!busy || !row.payloads.length} onClick={() => void decrypt()}>Decrypt this record</button>{row.ownerKind === 'file' && <button type="button" disabled={!!busy} onClick={() => void download()}>{row.payloads.some(p => p.reference.role === 'binary') ? 'Decrypt and download file' : 'Download plain file'}</button>}
      {legacyAttributions.length > 0 && <div role="status"><p>Legacy payloads authenticated: {legacyAttributions.length}. Ambiguous generations: {legacyAttributions.filter(value => value.generationAmbiguous).length}. Recovery does not rewrite the original metadata.</p><button type="button" onClick={() => saveLocalDownload(new Blob([JSON.stringify(legacyAttributions, null, 2)], { type: 'application/json' }), 'knowz-legacy-attribution.json')}>Download public attribution metadata</button></div>}
      {body !== null && <label>Encrypted body<textarea value={body} disabled={!!busy || !canEditRow || row.ownerKind === 'version'} onChange={e => { setBody(e.target.value); setAiConsent(false); setAiDraft(null); }} /></label>}
      {ordinaryBody !== null && <><p>This body is ordinary content. Its protected fields require the original key and decrypt separately.</p><label>Ordinary body<textarea value={ordinaryDraft ?? ordinaryBody} readOnly={!canEditRow || row.ownerKind !== 'knowledge' || !row.payloads.some(payload => payload.reference.role === 'field')} disabled={!!busy} onChange={event => { setOrdinaryDraft(event.target.value); setAiConsent(false); setAiDraft(null); }} /></label></>}
      {Object.entries(fields).map(([name, value]) => <label key={name}>Encrypted {name}<textarea value={value} disabled={!!busy || !canEditRow || row.ownerKind === 'version'} onChange={e => { setFields(current => ({ ...current, [name]: e.target.value })); if (name === 'content') { setAiConsent(false); setAiDraft(null); } }} /></label>)}
      {canEditRow && row.ownerKind === 'file' && <div><p>{row.payloads.some(p => p.reference.role === 'binary') ? 'Replacement file bytes and protected metadata are encrypted locally.' : 'The replacement keeps ordinary file bytes. Its protected metadata is encrypted locally.'}</p><label>{row.payloads.some(p => p.reference.role === 'binary') ? 'Replace encrypted file' : 'Replace plain file'}<input type="file" disabled={!!busy} onChange={e => { setReplacement(e.target.files?.[0] ?? null); e.target.value = ''; }} /></label><button type="button" disabled={!!busy || !replacement || identity?.lifecycle !== 'active'} onClick={() => void replaceFile()}>{row.payloads.some(p => p.reference.role === 'binary') ? 'Encrypt and replace file' : 'Replace file and encrypt metadata'}</button></div>}
      {decrypted && canEditRow && row.ownerKind === 'knowledge' && <div className="archive-panel">
        <button type="button" disabled={!!busy} onClick={() => void reviewLocalAi()}>Review local AI option</button>
        {aiPolicy && !aiPolicy.supported && <p role="status">Local AI is unavailable for this installation or document. No document text was sent.</p>}
        {aiPolicy?.supported && aiPolicy.provider && <><p>Configured local model: <strong>{aiPolicy.provider.model}</strong><br />{aiPolicy.provider.displayOrigin}</p><p>This sends this document body and your instruction to the operator-configured local model for one request. No keys are sent, and the response stays a draft until you review and save it.</p>
          {ordinaryBody !== null && <p>Saving this body keeps it ordinary plaintext. Its protected fields stay encrypted.</p>}
          <label>Local AI instruction<textarea value={aiInstruction} disabled={!!busy} onChange={event => { setAiInstruction(event.target.value); setAiConsent(false); setAiDraft(null); }} /></label>
          <label className="archive-check"><input type="checkbox" checked={aiConsent} disabled={!!busy} onChange={event => setAiConsent(event.target.checked)} />I consent to sending this document and instruction to this configured local model for this request</label>
          <button type="button" disabled={!!busy || !aiConsent || !aiInstruction.trim()} onClick={() => void generateLocalAi()}>Generate local draft</button>
          {aiDraft !== null && <><label>Local AI draft<textarea value={aiDraft} readOnly /></label>{aiDraft === '' && <label className="archive-check"><input type="checkbox" checked={aiEmptyConsent} onChange={event => setAiEmptyConsent(event.target.checked)} />I confirm replacing the body with an empty draft</label>}<button type="button" disabled={!!busy || aiDraft === '' && !aiEmptyConsent} onClick={() => { if (row.payloads.some(payload => payload.reference.role === 'field' && payload.reference.fieldName === 'content')) setFields(current => ({ ...current, content: aiDraft })); else if (ordinaryBody !== null) setOrdinaryDraft(aiDraft); else setBody(aiDraft); setAiDraft(null); setAiEmptyConsent(false); setStatus('Draft applied to the editor. Review it and save explicitly.'); }}>{aiDraft === '' ? 'Use empty draft in editor' : 'Use draft in editor'}</button><button type="button" onClick={() => { setAiDraft(null); setAiEmptyConsent(false); }}>Discard local draft</button></>}
        </>}
      </div>}
      {decrypted && canEditRow && row.ownerKind !== 'version' && <button type="button" disabled={!!busy || !identity || identity.lifecycle !== 'active'} onClick={() => void save()}>Save encrypted changes</button>}
      {canEdit && row.ownerKind === 'version' && matchingRestoreShape && <><p>{plainHistory ? 'Restoring preserves the ordinary body and exact protected fields as a new current revision.' : 'Restoring preserves this complete encrypted snapshot as a new current revision.'}</p><button type="button" disabled={!!busy || !decrypted} onClick={() => void run('Restoring protected history…', async (signal, alive) => { await transport.restore(knowledgeId, row.id, currentRevision.current, signal, vaultId); if (alive()) { onKnowledgeChanged?.(); setSelection(`knowledge:${knowledgeId}`); await load('knowledge', knowledgeId, signal, alive); setStatus(plainHistory ? 'Field-protected snapshot restored as a new revision.' : 'Encrypted snapshot restored as a new revision.'); } })}>{plainHistory ? 'Restore this field-protected version' : 'Restore this encrypted version'}</button></>}
      {canEdit && row.ownerKind === 'version' && !matchingRestoreShape && <p role="status">{plainHistory ? 'Restoring this ordinary-body snapshot would convert the current encrypted body. This conversion is not supported; the original snapshot remains readable.' : 'Restoring this encrypted-body snapshot would convert the current ordinary body. This conversion is not supported; the original snapshot remains readable.'}</p>}
      {canEditRow && ['comment', 'file', 'attachment'].includes(row.ownerKind) && <><button type="button" disabled={!!busy} onClick={() => setDeleteConfirm(!deleteConfirm)}>Delete this {row.ownerKind}</button>{deleteConfirm && <div role="alert"><p>This removes the selected {row.ownerKind} from the destination. Review any linked owners before deleting a shared file.</p><button type="button" disabled={!!busy} onClick={() => void run('Deleting encrypted record…', async (signal, alive) => { await transport.remove(row.ownerKind, row.id, reply.revision, signal, vaultId); if (alive()) { setSelection(`knowledge:${knowledgeId}`); await load('knowledge', knowledgeId, signal, alive); setStatus('Encrypted record deleted.'); } })}>Confirm deletion</button></div>}</>}
    </div>
    {canEdit && <details className="archive-panel"><summary>Add an encrypted comment</summary><label>Comment<textarea value={comment} disabled={!!busy} onChange={e => setComment(e.target.value)} /></label><button type="button" disabled={!!busy || !comment.trim() || !identity || identity.lifecycle !== 'active'} onClick={() => void addComment()}>Encrypt and add comment</button></details>}
    {canEdit && <details className="archive-panel"><summary>Add an encrypted attachment</summary><p>The browser supports encrypted files up to 64 MiB including the authentication tag.</p><label>New encrypted attachment<input type="file" disabled={!!busy} onChange={e => { setAttachment(e.target.files?.[0] ?? null); e.target.value = ''; }} /></label>{attachment && <p>Selected: {attachment.name}</p>}<button type="button" disabled={!!busy || !attachment || identity?.lifecycle !== 'active'} onClick={() => void addFile()}>Encrypt and attach file</button></details>}
    {canEdit && <details className="archive-panel"><summary>Add an encrypted note to this vault</summary><label>New encrypted note title<input value={newTitle} disabled={!!busy} onChange={e => setNewTitle(e.target.value)} /></label><label>New encrypted note body<textarea value={newBody} disabled={!!busy} onChange={e => setNewBody(e.target.value)} /></label><button type="button" disabled={!!busy || !newTitle.trim() || identity?.lifecycle !== 'active'} onClick={() => void addNote()}>Encrypt and create note</button></details>}
    </>}
  </section>;
}
