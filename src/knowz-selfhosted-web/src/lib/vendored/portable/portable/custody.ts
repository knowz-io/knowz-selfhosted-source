import { x25519 } from '@noble/curves/ed25519.js';
import { hmac } from '@noble/hashes/hmac.js';
import { sha256 } from '@noble/hashes/sha2.js';
import { PortableCryptoError, decodeBase64, encodeBase64, fingerprintPublicKey, parsePortableJson } from '../portable.js';
import { decryptEnvelopeToBytes } from './vault-crypto.js';
export interface CustodyChallenge {
  challengeId: string; bindingRawBase64: string;
  envelope: { ciphertext: string; contentNonce: string; wrappedDek: string; dekNonce: string; ephemeralPublicKey: string; version: number };
}
export interface CustodyProofExpected {
  actorId: string; destinationTenantId: string; destinationVaultId: string; contextId: string;
  originalBindingId: string; generation: number; fingerprint: string; planId: string; planSha256?: string;
}
export interface RecoveredCustodyAcceptance {
  acceptanceId: string; contextId: string; generation: number; fingerprint: string;
  effectiveCustody: 'customer-held-local'; keyPossession: 'canary-verified'; archiveRecovery: 'not-verified'; sourceTrust: 'trusted' | 'unverified';
}
const invalid = (): never => { throw new PortableCryptoError('custody-proof', 'The custody challenge could not be verified. Reload the current account and request a fresh challenge.'); };
const object = (value: unknown): value is Record<string, unknown> => !!value && typeof value === 'object' && !Array.isArray(value);
const exact = (value: unknown, names: string[]): value is Record<string, unknown> => object(value) && Object.keys(value).length === names.length && names.every(name => Object.prototype.hasOwnProperty.call(value, name));
const guid = (value: unknown) => typeof value === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(value) && !/^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(value);
const digest = (value: unknown) => typeof value === 'string' && /^[a-f0-9]{64}$/.test(value);
/** Possession of one original key only. No private material or archive recovery claim is returned. */
export async function proveRecoveredCustody(response: CustodyChallenge, expected: CustodyProofExpected, localPrivateKey: Uint8Array,
  options: { signal?: AbortSignal; isCurrent?: () => boolean; now?: () => number } = {}): Promise<{ challengeId: string; proof: string }> {
  let key: Uint8Array | undefined; let raw: Uint8Array | undefined; let plaintext: Uint8Array | undefined; let secret: Uint8Array | undefined; let proof: Uint8Array | undefined; let nonce: Uint8Array | undefined;
  const current = () => { options.signal?.throwIfAborted(); if (options.isCurrent && !options.isCurrent()) invalid(); };
  try {
    current(); const now = options.now ?? Date.now;
    const identityFields = ['actorId', 'destinationTenantId', 'destinationVaultId', 'contextId', 'originalBindingId', 'planId'] as const;
    if (identityFields.some(name => !guid(expected[name])) || !Number.isSafeInteger(expected.generation) || expected.generation < 1 || !digest(expected.fingerprint)
      || expected.planSha256 !== undefined && !digest(expected.planSha256) || localPrivateKey.length !== 32
      || !exact(response, ['challengeId', 'bindingRawBase64', 'envelope']) || !guid(response.challengeId)) invalid();
    key = localPrivateKey.slice(); if (fingerprintPublicKey(x25519.getPublicKey(key)) !== expected.fingerprint) invalid();
    raw = decodeBase64(response.bindingRawBase64, 1, 8192);
    const binding = parsePortableJson(new TextDecoder('utf-8', { fatal: true }).decode(raw), 8192) as Record<string, unknown>;
    if (!exact(binding, ['formatVersion', 'challengeId', ...identityFields, 'generation', 'fingerprint', 'planSha256', 'purpose', 'operation', 'nonce', 'expiresAt'])
      || binding.formatVersion !== 1 || binding.challengeId !== response.challengeId || binding.generation !== expected.generation || binding.fingerprint !== expected.fingerprint
      || !digest(binding.planSha256) || expected.planSha256 !== undefined && binding.planSha256 !== expected.planSha256
      || binding.purpose !== 'customer-held-custody-acceptance' || binding.operation !== 'accept-custody'
      || identityFields.some(name => binding[name] !== expected[name])) invalid();
    nonce = decodeBase64(binding.nonce, 32);
    const expiry = typeof binding.expiresAt === 'string' ? Date.parse(binding.expiresAt) : NaN;
    if (!Number.isFinite(expiry) || new Date(expiry).toISOString() !== binding.expiresAt || expiry <= now() || expiry > now() + 300_000) invalid();
    const env = response.envelope;
    if (!exact(env, ['ciphertext', 'contentNonce', 'wrappedDek', 'dekNonce', 'ephemeralPublicKey', 'version']) || env.version !== 2) invalid();
    decodeBase64(env.ciphertext, 16, 8192); decodeBase64(env.contentNonce, 12); decodeBase64(env.wrappedDek, 48); decodeBase64(env.dekNonce, 12); decodeBase64(env.ephemeralPublicKey, 32);
    current();
    plaintext = await decryptEnvelopeToBytes(key, { ciphertext: env.ciphertext, contentNonce: env.contentNonce, encryptedDek: env.wrappedDek, dekNonce: env.dekNonce, ephemeralPublicKey: env.ephemeralPublicKey }, new TextEncoder().encode(`knowz-msg-v2|${response.challengeId}|x25519-vault-public|2`));
    const canary = parsePortableJson(new TextDecoder('utf-8', { fatal: true }).decode(plaintext), 8192) as Record<string, unknown>;
    if (!exact(canary, ['formatVersion', 'bindingSha256', 'secret']) || canary.formatVersion !== 1 || canary.bindingSha256 !== fingerprintPublicKey(raw)) invalid();
    secret = decodeBase64(canary.secret, 32); canary.secret = '';
    proof = hmac(sha256, secret, raw); current(); if (expiry <= now()) invalid();
    return { challengeId: response.challengeId, proof: encodeBase64(proof) };
  } catch { return invalid(); }
  finally { key?.fill(0); raw?.fill(0); plaintext?.fill(0); secret?.fill(0); proof?.fill(0); nonce?.fill(0); }
}
