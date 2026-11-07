import { PortableCryptoError, decryptPayload, decodeBase64, fingerprintPublicKey, type PortableKeyIdentity, type PortableKeyring, type PortablePayload } from '../portable.js';
export interface LegacyKeyCandidate { contextId: string; fingerprint: string; generation: number }
export interface LegacyAttribution {
  formatId: 'knowz.legacy-attribution'; formatVersion: 1; ownerKind: string; ownerId: string; role: string; fieldName: string | null; versionId: string | null;
  contextId: string; originalBindingId: string; fingerprint: string; generation: number | null; generationAmbiguous: boolean; possibleGenerations: number[];
  envelopeVersion: 1; ciphertextSha256: string; envelopeSha256: string;
}
/** Explicit v1-only AEAD trials. Never changes an archive or interprets key proof as authorization. */
export async function recoverLegacyPayload(payload: PortablePayload, ring: PortableKeyring, options: { candidates: readonly LegacyKeyCandidate[]; binary?: Uint8Array; signal?: AbortSignal }): Promise<{ plaintext: Uint8Array; attribution: LegacyAttribution }> {
  const fail = (code: string, message: string): never => { throw new PortableCryptoError(code, message); };
  const ref = payload.reference;
  if (!ref.legacyUnattributed || ref.envelopeVersion !== 1 || ref.algorithmProfile !== 'x25519-vault' || ref.keyType !== 'x25519-vault-public') fail('legacy-version', 'Explicit legacy recovery supports only the declared original version 1 vault algorithm.');
  if (!options.candidates.length) fail('missing-key', 'Select and unlock original candidate keys for this legacy payload.');
  if (options.candidates.length > 16) fail('legacy-limit', 'Select no more than 16 candidate key generations.');
  const catalog = ring.snapshot().catalog; const epoch = ring.epoch; const candidates = new Map<string, PortableKeyIdentity>();
  for (const candidate of options.candidates) {
    const key = catalog.find(value => value.contextId === candidate.contextId && value.fingerprint === candidate.fingerprint && value.generation === candidate.generation);
    if (!key || key.contextId !== ref.contextId || key.originalBindingId !== ref.originalBindingId) return fail('legacy-context', 'Candidate keys must belong to this authorized original context and binding.');
    if (ref.fingerprint && ref.fingerprint !== key.fingerprint) fail('legacy-context', 'The candidate conflicts with the declared original key fingerprint.');
    candidates.set(key!.fingerprint, key!);
  }
  const successes: Array<{ key: PortableKeyIdentity; plaintext: Uint8Array }> = [];
  try {
    for (const key of candidates.values()) {
      options.signal?.throwIfAborted(); if (epoch !== ring.epoch) fail('session-changed', 'The recovery session changed. Select and unlock the keys again.');
      try {
        const plaintext = await decryptPayload({ ...payload, reference: { ...ref, fingerprint: key.fingerprint, generation: key.generation, legacyUnattributed: false } }, ring, { binary: options.binary, signal: options.signal });
        successes.push({ key, plaintext });
      } catch (error) {
        if (options.signal?.aborted || epoch !== ring.epoch || !(error instanceof PortableCryptoError) || !['authentication', 'missing-key'].includes(error.code)) throw error;
      }
    }
    options.signal?.throwIfAborted(); if (epoch !== ring.epoch) fail('session-changed', 'The recovery session changed. Select and unlock the keys again.');
    if (successes.length !== 1) fail('legacy-authentication', 'The selected keys did not uniquely authenticate this legacy payload. Check the original keys and ciphertext.');
    const success = successes[0]!;
    const generations = [...new Set(catalog.filter(value => value.contextId === ref.contextId && value.originalBindingId === ref.originalBindingId && value.fingerprint === success.key.fingerprint).map(value => value.generation))].sort((a, b) => a - b);
    const ciphertext = options.binary ?? decodeBase64(payload.envelope.encryptedContent ?? payload.envelope.ciphertext, 16, 64 * 1024 ** 2);
    const envelope = { ciphertextSha256: fingerprintPublicKey(ciphertext), encryptedDek: payload.envelope.encryptedDek, dekNonce: payload.envelope.dekNonce, ephemeralPublicKey: payload.envelope.ephemeralPublicKey, contentNonce: payload.envelope.contentNonce, envelopeVersion: 1 };
    return { plaintext: success.plaintext, attribution: { formatId: 'knowz.legacy-attribution', formatVersion: 1, ownerKind: ref.ownerKind, ownerId: ref.ownerId, role: ref.role, fieldName: ref.fieldName ?? null, versionId: ref.versionId ?? null, contextId: ref.contextId, originalBindingId: ref.originalBindingId, fingerprint: success.key.fingerprint, generation: generations.length === 1 ? generations[0]! : null, generationAmbiguous: generations.length !== 1, possibleGenerations: generations, envelopeVersion: 1, ciphertextSha256: envelope.ciphertextSha256, envelopeSha256: fingerprintPublicKey(new TextEncoder().encode(JSON.stringify(envelope))) } };
  } catch (error) { successes.forEach(value => value.plaintext.fill(0)); throw error; }
}
