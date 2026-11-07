import { x25519 } from '@noble/curves/ed25519.js';
import { PortableCryptoError, encodeBase64, decodeBase64, fingerprintPublicKey, parsePortableJson } from '../portable.js';
import { decryptEnvelopeToBytes } from './vault-crypto.js';
import { createEncryptedKeyFile } from './key-file-crypto.js';
export interface RecoveryOriginalKey { originalBindingId: string; generation: number; fingerprint: string; publicKey: string }
export interface ManagedRecoveryOptions { sourceTenantId: string; sourceVaultId: string; actorId: string; destinationTenantId: string; expectedSignerFingerprint: string; expectedKeys: readonly RecoveryOriginalKey[] }
export interface ManagedRecoveryRequest { recipientPublicKey: string; destinationTenantId: string; nonce: string; expiresAt: string; purpose: 'managed-to-customer-recovery' }
export interface ManagedRecoveryResponse {
  transferId: string;
  envelope: { ciphertext: string; contentNonce: string; wrappedDek: string; dekNonce: string; ephemeralPublicKey: string; version: number };
  receiptRawBase64: string;
  signature: { algorithm: string; publicKey: string; publicKeyFingerprint: string; signatureP1363: string };
}
const encoder = new TextEncoder(); const limit = 1024 * 1024;
const invalid = (): never => { throw new PortableCryptoError('recovery-transfer', 'The recovery transfer could not be verified. Check the source identity and request a fresh authorized transfer.'); };
const guid = (value: string) => { if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(value)) invalid(); return value; };
const digest = (value: unknown) => fingerprintPublicKey(encoder.encode(JSON.stringify(value)));
const object = (value: unknown): value is Record<string, any> => !!value && typeof value === 'object' && !Array.isArray(value);
const keyIdentity = (key: RecoveryOriginalKey) => `${key.originalBindingId}:${key.generation}`;
function validateKeys(keys: unknown): RecoveryOriginalKey[] {
  if (!Array.isArray(keys) || keys.length < 1 || keys.length > 256) return invalid();
  const seen = new Set<string>();
  return keys.map(key => {
    if (!object(key) || !Number.isSafeInteger(key.generation) || key.generation < 1) return invalid();
    const value = { originalBindingId: guid(key.originalBindingId), generation: key.generation, fingerprint: key.fingerprint, publicKey: key.publicKey };
    if (fingerprintPublicKey(decodeBase64(value.publicKey, 32)) !== value.fingerprint || seen.has(keyIdentity(value))) return invalid();
    seen.add(keyIdentity(value)); return value;
  });
}
function sameInventory(actual: RecoveryOriginalKey[], expected: readonly RecoveryOriginalKey[]) {
  return actual.length === expected.length && actual.every(key => expected.some(other => keyIdentity(key) === keyIdentity(other) && key.publicKey === other.publicKey && key.fingerprint === other.fingerprint));
}
/** Holds original keys only in private memory; callers can explicitly export compatible protected files. */
export class RecoveredOriginalKeys {
  #keys: Array<{ identity: RecoveryOriginalKey; privateKey: Uint8Array }>;
  #epoch = 0;
  #timer: ReturnType<typeof setTimeout>;
  constructor(keys: Array<{ identity: RecoveryOriginalKey; privateKey: Uint8Array }>) {
    this.#keys = keys;
    this.#timer = setTimeout(() => this.dispose(), 5 * 60 * 1000);
    (this.#timer as unknown as { unref?: () => void }).unref?.();
  }
  snapshot(): RecoveryOriginalKey[] { return this.#keys.map(key => ({ ...key.identity })); }
  dispose() { ++this.#epoch; clearTimeout(this.#timer); this.#keys.forEach(key => key.privateKey.fill(0)); this.#keys = []; }
  async exportProtected(index: number, passphrase: string): Promise<{ fileName: string; text: string }> {
    const entry = this.#keys[index]; const epoch = this.#epoch;
    if (!entry) throw new PortableCryptoError('recovery-cleared', 'Recovered key material was cleared. Request a fresh authorized recovery.');
    if (!passphrase) throw new PortableCryptoError('recovery-password', 'Choose a password for the protected key file.');
    const bytes = entry.privateKey.slice();
    try {
      const file = await createEncryptedKeyFile(entry.identity.originalBindingId, 'x25519-private', bytes, entry.identity.fingerprint, passphrase, { kdf: 'pbkdf2' });
      if (this.#epoch !== epoch) throw new PortableCryptoError('recovery-cleared', 'Recovered key material was cleared. Request a fresh authorized recovery.');
      return { fileName: `knowz-${entry.identity.originalBindingId}-generation-${entry.identity.generation}-${entry.identity.fingerprint.slice(0, 12)}.knowzkey`, text: JSON.stringify(file, null, 2) };
    } finally { bytes.fill(0); }
  }
}
/** Generates a one-time local recipient. The request contains public material only; completion is single-use. */
export function createManagedRecoverySession(options: ManagedRecoveryOptions) {
  const expectedKeys = validateKeys(options.expectedKeys);
  options = Object.freeze({ ...options, expectedKeys });
  for (const id of [options.sourceTenantId, options.sourceVaultId, options.actorId, options.destinationTenantId]) guid(id);
  if (!/^[a-f0-9]{64}$/.test(options.expectedSignerFingerprint)) invalid();
  const privateKey = crypto.getRandomValues(new Uint8Array(32));
  const request: Readonly<ManagedRecoveryRequest> = Object.freeze({ recipientPublicKey: encodeBase64(x25519.getPublicKey(privateKey)), destinationTenantId: options.destinationTenantId, nonce: encodeBase64(crypto.getRandomValues(new Uint8Array(32))), expiresAt: new Date(Date.now() + 5 * 60 * 1000).toISOString(), purpose: 'managed-to-customer-recovery' });
  let closed = false; let epoch = 0; const close = () => { closed = true; ++epoch; privateKey.fill(0); clearTimeout(timer); };
  const timer = setTimeout(close, 5 * 60 * 1000); (timer as unknown as { unref?: () => void }).unref?.();
  return {
    request, close,
    async complete(response: ManagedRecoveryResponse, signal?: AbortSignal): Promise<RecoveredOriginalKeys> {
      if (closed) throw new PortableCryptoError('recovery-used', 'This recovery recipient is closed or was already used.');
      closed = true; const version = epoch;
      let plaintext: Uint8Array | undefined; const recovered: Array<{ identity: RecoveryOriginalKey; privateKey: Uint8Array }> = [];
      try {
        signal?.throwIfAborted(); if (Date.parse(request.expiresAt) <= Date.now() || !object(response) || !object(response.signature)) invalid();
        guid(response.transferId);
        const signature = response.signature;
        if (signature.algorithm !== 'ECDSA-P256-SHA256-P1363') invalid();
        const spki = decodeBase64(signature.publicKey, 80, 256); const fp = fingerprintPublicKey(spki);
        if (fp !== options.expectedSignerFingerprint || fp !== signature.publicKeyFingerprint) invalid();
        const receiptBytes = decodeBase64(response.receiptRawBase64, 1, limit);
        const signingKey = await crypto.subtle.importKey('spki', new Uint8Array(spki), { name: 'ECDSA', namedCurve: 'P-256' }, false, ['verify']);
        if (!await crypto.subtle.verify({ name: 'ECDSA', hash: 'SHA-256' }, signingKey, new Uint8Array(decodeBase64(signature.signatureP1363, 64)), new Uint8Array(receiptBytes))) invalid();
        const receipt = parsePortableJson(new TextDecoder('utf-8', { fatal: true }).decode(receiptBytes), limit);
        if (!object(receipt) || receipt.formatVersion !== 1) return invalid();
        const expected = { transferId: response.transferId, sourceTenantId: options.sourceTenantId, sourceVaultId: options.sourceVaultId, actorId: options.actorId, destinationTenantId: request.destinationTenantId, purpose: request.purpose, expiresAt: request.expiresAt, requestSha256: digest(request) };
        for (const [key, value] of Object.entries(expected)) if (receipt[key] !== value) invalid();
        if (receipt.nonceSha256 !== fingerprintPublicKey(decodeBase64(request.nonce, 32)) || receipt.recipientPublicKeySha256 !== fingerprintPublicKey(decodeBase64(request.recipientPublicKey, 32))) invalid();
        const inventory = validateKeys(receipt.keys); if (!sameInventory(inventory, expectedKeys)) invalid();
        const env = response.envelope;
        if (!object(env) || env.version !== 2) invalid();
        const canonical = { ciphertext: env.ciphertext, contentNonce: env.contentNonce, wrappedDek: env.wrappedDek, dekNonce: env.dekNonce, ephemeralPublicKey: env.ephemeralPublicKey, version: env.version };
        if (receipt.envelopeSha256 !== digest(canonical) || receipt.ciphertextSha256 !== fingerprintPublicKey(decodeBase64(env.ciphertext, 16, limit))) invalid();
        decodeBase64(env.contentNonce, 12); decodeBase64(env.dekNonce, 12); decodeBase64(env.wrappedDek, 48); decodeBase64(env.ephemeralPublicKey, 32);
        signal?.throwIfAborted();
        plaintext = await decryptEnvelopeToBytes(privateKey, { ciphertext: env.ciphertext, contentNonce: env.contentNonce, encryptedDek: env.wrappedDek, dekNonce: env.dekNonce, ephemeralPublicKey: env.ephemeralPublicKey }, encoder.encode(`knowz-msg-v2|${response.transferId}|x25519-vault-public|2`));
        const payload = parsePortableJson(new TextDecoder('utf-8', { fatal: true }).decode(plaintext), limit);
        if (!object(payload) || payload.nonce !== request.nonce) return invalid();
        for (const [key, value] of Object.entries(expected)) if (payload[key] !== value) invalid();
        if (!sameInventory(validateKeys(payload.keys), inventory)) invalid();
        for (const entry of payload.keys) {
          const bytes = decodeBase64(entry.privateKey, 32);
          const identity = inventory.find(key => keyIdentity(key) === keyIdentity(entry))!;
          recovered.push({ identity, privateKey: bytes });
          if (fingerprintPublicKey(x25519.getPublicKey(bytes)) !== identity.fingerprint) invalid();
          entry.privateKey = ''; // Drop immutable decoded strings promptly; retained key buffers are explicitly zeroed.
        }
        signal?.throwIfAborted(); if (version !== epoch || Date.parse(request.expiresAt) <= Date.now()) invalid();
        return new RecoveredOriginalKeys(recovered);
      } catch { recovered.forEach(key => key.privateKey.fill(0)); return invalid(); }
      finally { clearTimeout(timer); privateKey.fill(0); plaintext?.fill(0); }
    },
  };
}
