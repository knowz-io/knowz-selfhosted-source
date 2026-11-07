import { PortableCryptoError, encryptPayload, fingerprintPublicKey, parsePortableJson, PORTABLE_LIMITS, type PortableGraphRecord, type PortableKeyIdentity, type PortablePayloadReference } from '../portable.js';
export interface PortableRecordEdits { body?: string; fields?: Record<string, string>; binary?: Uint8Array }
/** Re-encrypts only edited fields, preserving all relationships and immutable original identities. */
export async function rewriteEncryptedRecord(source: PortableGraphRecord, identity: PortableKeyIdentity, edits: PortableRecordEdits) {
  if (source.ownerKind === 'version' || source.ownerKind === 'file' && source.record.isCurrentVersion === false) throw new PortableCryptoError('history-readonly', 'Historical snapshots are immutable. Use the authorized restore action.');
  if (identity.lifecycle !== 'active') throw new PortableCryptoError('key-lifecycle', 'Select an active original key generation before editing.');
  const record = parsePortableJson(JSON.stringify(source.record), PORTABLE_LIMITS.packageBytes) as Record<string, unknown>;
  const references = source.payloads.map(payload => ({ ...payload.reference }));
  let fields: { schema: string; fields: Record<string, unknown> } | undefined;
  if (record.encryptedFieldsJson) fields = parsePortableJson(String(record.encryptedFieldsJson)) as typeof fields;
  let binary: Uint8Array | undefined;
  for (const payload of source.payloads) {
    const ref = payload.reference;
    const value = ref.role === 'field' ? edits.fields?.[ref.fieldName!] : ref.role === 'body' ? edits.body : ref.role === 'binary' ? edits.binary : undefined;
    if (value === undefined) continue;
    if (ref.contextId !== identity.contextId || ref.originalBindingId.toLowerCase() !== identity.originalBindingId.toLowerCase()) throw new PortableCryptoError('context-mismatch', 'Editing must retain the immutable original encryption context.');
    const reference: PortablePayloadReference = { ...ref, fingerprint: identity.fingerprint, generation: identity.generation, envelopeVersion: ref.role === 'field' ? 1 : 2, legacyUnattributed: false };
    const bytes = typeof value === 'string' ? new TextEncoder().encode(value) : value;
    let encrypted;
    try { encrypted = await encryptPayload(bytes, reference, identity); } finally { if (typeof value === 'string') bytes.fill(0); }
    references[source.payloads.indexOf(payload)] = reference;
    if (ref.role === 'field') {
      if (!fields || fields.schema !== 'knowz-encrypted-fields-v1') throw new PortableCryptoError('field-format', 'The encrypted field set is invalid.');
      fields.fields[ref.fieldName!] = encrypted.envelope;
    } else {
      const { ciphertext, encryptedDek, dekNonce, ephemeralPublicKey, contentNonce } = encrypted.envelope;
      Object.assign(record, { encryptedDek, encryptedDekNonce: dekNonce, ephemeralPublicKey, contentNonce, envelopeVersion: 2, cryptoBindingId: ref.originalBindingId });
      const originalKeyRef = typeof record.encryptionKeyRef === 'string' ? record.encryptionKeyRef.split(':') : [];
      if (originalKeyRef.length === 6 && originalKeyRef[0] === 'vault' && originalKeyRef[2]?.trim() && originalKeyRef[4] === 'sha256') record.encryptionKeyRef = `vault:${identity.originalBindingId.toLowerCase()}:${originalKeyRef[2]}:v${identity.generation}:sha256:${identity.fingerprint}`;
      else delete record.encryptionKeyRef; // Exact payload descriptors remain authoritative for noncanonical legacy rows.
      if (ref.role === 'body') record.ciphertext = ciphertext;
      else {
        binary = Uint8Array.from(atob(ciphertext!), char => char.charCodeAt(0));
        record.sizeBytes = binary.length; record.originalSizeBytes = bytes.length;
        record.binarySha256 = fingerprintPublicKey(binary); delete record.binaryEntryPath; delete record.binaryContentBase64;
      }
    }
  }
  if (edits.body !== undefined && !source.payloads.some(payload => payload.reference.role === 'body')) throw new PortableCryptoError('missing-body', 'This record stores its content in encrypted fields. Edit its content field.');
  for (const field of Object.keys(edits.fields ?? {})) if (!source.payloads.some(payload => payload.reference.role === 'field' && payload.reference.fieldName === field)) throw new PortableCryptoError('unknown-field', 'Cannot edit a field outside the authenticated encrypted field set.');
  if (edits.binary !== undefined && source.ownerKind === 'file' && !source.payloads.some(payload => payload.reference.role === 'binary')) {
    if (edits.binary.length > PORTABLE_LIMITS.encryptedFileBytes) throw new PortableCryptoError('file-size', 'The ordinary replacement file exceeds the 64 MiB browser limit.');
    binary = edits.binary.slice(); record.sizeBytes = binary.length; record.binarySha256 = fingerprintPublicKey(binary);
    delete record.binaryEntryPath; delete record.binaryFilePath; delete record.binaryContentBase64;
  }
  if (fields) record.encryptedFieldsJson = JSON.stringify(fields);
  return { record, payloadKeyReferences: references, binary };
}

export interface CreateEncryptedFileInput {
  id: string; vaultId: string; knowledgeId: string; name: string; contentType: string;
  bytes: Uint8Array; identity: PortableKeyIdentity; originalBindingKind?: string;
}
/** New files use standard existing whole-file AES-GCM and separate v1 field envelopes. */
export async function createEncryptedFileRecord(input: CreateEncryptedFileInput) {
  if (input.bytes.length > PORTABLE_LIMITS.encryptedFileBytes - 16) throw new PortableCryptoError('file-size', 'Encrypted file ciphertext must fit within 64 MiB, including its authentication tag.');
  const reference: PortablePayloadReference = {
    ownerKind: 'file', ownerId: input.id, role: 'binary', contextId: input.identity.contextId,
    fingerprint: input.identity.fingerprint, generation: input.identity.generation,
    originalBindingKind: input.originalBindingKind ?? 'vault', originalBindingId: input.identity.originalBindingId,
    keyType: 'x25519-vault-public', algorithmProfile: 'x25519-vault', envelopeVersion: 2,
  };
  const encrypted = await encryptPayload(input.bytes, reference, input.identity);
  const fieldValues: Record<string, unknown> = {}; const payloadKeyReferences = [reference];
  for (const [fieldName, value] of Object.entries({ fileName: input.name, contentType: input.contentType })) {
    const fieldReference = { ...reference, role: 'field', fieldName, envelopeVersion: 1 };
    const bytes = new TextEncoder().encode(value);
    try { fieldValues[fieldName] = (await encryptPayload(bytes, fieldReference, input.identity)).envelope; payloadKeyReferences.push(fieldReference); }
    finally { bytes.fill(0); }
  }
  const binary = Uint8Array.from(atob(encrypted.envelope.ciphertext!), char => char.charCodeAt(0));
  const record: Record<string, unknown> = {
    id: input.id, originalFileId: input.id, knowledgeId: input.knowledgeId, contentType: 'application/octet-stream',
    binarySha256: fingerprintPublicKey(binary), sizeBytes: binary.length, originalSizeBytes: input.bytes.length, cryptoBindingId: input.identity.originalBindingId,
    envelopeVersion: 2, encryptedDek: encrypted.envelope.encryptedDek, encryptedDekNonce: encrypted.envelope.dekNonce,
    ephemeralPublicKey: encrypted.envelope.ephemeralPublicKey, contentNonce: encrypted.envelope.contentNonce,
    encryptedFieldsJson: JSON.stringify({ schema: 'knowz-encrypted-fields-v1', fields: fieldValues }),
    createdAt: new Date().toISOString(), updatedAt: new Date().toISOString(),
  };
  return { vaultId: input.vaultId, record, payloadKeyReferences, binary };
}

export async function createEncryptedKnowledgeRecord(input: { id: string; vaultId: string; title: string; body: string; identity: PortableKeyIdentity }) {
  const reference: PortablePayloadReference = { ownerKind: 'knowledge', ownerId: input.id, role: 'body', contextId: input.identity.contextId, fingerprint: input.identity.fingerprint, generation: input.identity.generation, originalBindingKind: 'vault', originalBindingId: input.identity.originalBindingId, originalItemId: input.id, keyType: 'x25519-vault-public', algorithmProfile: 'x25519-vault', envelopeVersion: 2 };
  const body = new TextEncoder().encode(input.body); const title = new TextEncoder().encode(input.title);
  if (body.length + title.length > PORTABLE_LIMITS.packageBytes / 2) { body.fill(0); title.fill(0); throw new PortableCryptoError('note-size', 'Encrypted note content exceeds the supported metadata limit.'); }
  try {
    const content = await encryptPayload(body, reference, input.identity);
    const titleRef = { ...reference, role: 'field', fieldName: 'title', envelopeVersion: 1 };
    const titleEnvelope = await encryptPayload(title, titleRef, input.identity);
    return { vaultId: input.vaultId, record: { id: input.id, originalItemId: input.id, vaultIds: [input.vaultId], primaryVaultId: input.vaultId, type: 'note', ciphertext: content.envelope.ciphertext, encryptedDek: content.envelope.encryptedDek, encryptedDekNonce: content.envelope.dekNonce, ephemeralPublicKey: content.envelope.ephemeralPublicKey, contentNonce: content.envelope.contentNonce, envelopeVersion: 2, cryptoBindingId: input.identity.originalBindingId, encryptedFieldsJson: JSON.stringify({ schema: 'knowz-encrypted-fields-v1', fields: { title: titleEnvelope.envelope } }), createdAt: new Date().toISOString(), updatedAt: new Date().toISOString() }, payloadKeyReferences: [reference, titleRef] };
  } finally { body.fill(0); title.fill(0); }
}
