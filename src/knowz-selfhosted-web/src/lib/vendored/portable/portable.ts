import { recoverLegacyPayload, type LegacyKeyCandidate, type LegacyAttribution } from './portable/legacy.js';
/** Original-key portability. Browser-safe: no network, DOM, storage or Node-only imports. */
import { x25519 } from '@noble/curves/ed25519.js';
import { sha256 } from '@noble/hashes/sha2.js';
import { hkdf } from '@noble/hashes/hkdf.js';
import { mnemonicToSeedSync, validateMnemonic } from '@scure/bip39';
import { wordlist } from '@scure/bip39/wordlists/english.js';
import { decryptEnvelopeToBytes, encryptEnvelope } from './portable/vault-crypto.js';
import { unwrapKeyFile, type KnowzKeyFileV2 } from './portable/key-file-crypto.js';
import { plainVersionFields, validatePortableGraphClosure } from './portable/graph-validation.js';
import { validateDependencyBoundItem, validateSuspendedReleasePolicy, type PortableDependencyBoundKnowledge, type PortableSuspendedReleasePolicy } from './portable/suspended-release.js';
export { rewriteEncryptedRecord, createEncryptedKnowledgeRecord, createEncryptedFileRecord, type PortableRecordEdits, type CreateEncryptedFileInput } from './portable/records.js';
export { protectArchiveStream, unprotectArchiveStream, readBoundedStream } from './portable/password.js';
export { openPortableArchive, type OpenedPortableArchive, type PortableArchiveManifest, type PortableArchiveEntry, type OpenPortableArchiveOptions } from './portable/archive.js';

export const PORTABLE_LIMITS = Object.freeze({
  keyBytes: 64 * 1024, manifestBytes: 4 * 1024 * 1024, packageBytes: 8 * 1024 * 1024,
  metadataBytes: 64 * 1024 ** 2, packages: 128, members: 10_000, archiveBytes: 32 * 1024 ** 3, ordinaryFileBytes: 32 * 1024 ** 3, encryptedFileBytes: 64 * 1024 ** 2,
  protectedBrowserBytes: 128 * 1024 ** 2, expansionRatio: 200, scryptLogN: 18,
  legacyCandidates: 16, keyIdleMs: 30 * 60 * 1000,
});
const textEncoder = new TextEncoder();
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const fingerprint = /^[0-9a-f]{64}$/;
const safeFailure = 'Could not decrypt. The key does not match, or the encrypted data is corrupt or has been changed.';

export class PortableCryptoError extends Error {
  constructor(readonly code: string, message: string) { super(message); this.name = 'PortableCryptoError'; }
}
function fail(code: string, message: string): never { throw new PortableCryptoError(code, message); }
function object(value: unknown): value is Record<string, unknown> {
  return !!value && typeof value === 'object' && !Array.isArray(value);
}
function guid(value: unknown): asserts value is string {
  if (typeof value !== 'string' || !uuid.test(value) || /^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(value)) fail('invalid-binding', 'A valid original encryption binding is required.');
}
export function encodeBase64(bytes: Uint8Array): string {
  let encoded = '';
  for (let i = 0; i < bytes.length; i += 0x8000) encoded += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(encoded);
}
export function decodeBase64(value: unknown, min = 0, max = min): Uint8Array {
  if (typeof value !== 'string' || value.length > Math.ceil(max / 3) * 4 || !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(value)) fail('invalid-bytes', 'Invalid encrypted byte encoding.');
  let bytes: Uint8Array;
  try { bytes = Uint8Array.from(atob(value), char => char.charCodeAt(0)); } catch { return fail('invalid-bytes', 'Invalid encrypted byte encoding.'); }
  if (bytes.length < min || bytes.length > max || encodeBase64(bytes) !== value) fail('invalid-bytes', 'Invalid encrypted byte length.');
  return bytes;
}
export function fingerprintPublicKey(bytes: Uint8Array): string {
  return Array.from(sha256(bytes), b => b.toString(16).padStart(2, '0')).join('');
}
export function parsePortableJson(text: string, maximumBytes = PORTABLE_LIMITS.packageBytes): unknown {
  if (text.length > maximumBytes || textEncoder.encode(text).length > maximumBytes) fail('size-limit', 'The input is too large.');
  try {
    // Native JSON.parse discards duplicate properties. Inspect string tokens before parsing so
    // different consumers cannot authenticate the same bytes but interpret different key refs.
    const frames: Array<{ object: boolean; key: boolean; names: Set<string> }> = [];
    for (let i = 0; i < text.length; i++) {
      const char = text[i];
      if (char === '"') {
        const start = i++;
        for (; i < text.length; i++) { if (text[i] === '\\') i++; else if (text[i] === '"') break; }
        const frame = frames[frames.length - 1];
        if (frame?.object && frame.key) {
          const name = JSON.parse(text.slice(start, i + 1)) as string;
          if (frame.names.has(name)) fail('duplicate-json', 'Duplicate JSON property.');
          frame.names.add(name); frame.key = false;
        }
      } else if (char === '{' || char === '[') {
        frames.push({ object: char === '{', key: char === '{', names: new Set() });
        if (frames.length > 64) fail('json-depth', 'JSON nesting exceeds the supported limit.');
      } else if (char === '}' || char === ']') frames.pop();
      else if (char === ',') { const frame = frames[frames.length - 1]; if (frame?.object) frame.key = true; }
    }
    return JSON.parse(text, (key, value: unknown) => {
      if (['__proto__', 'constructor', 'prototype'].includes(key)) fail('invalid-json', 'Unsafe JSON property.');
      return value;
    });
  } catch (error) {
    if (error instanceof PortableCryptoError) throw error;
    return fail('invalid-json', 'Invalid JSON input.');
  }
}

export interface PortableKeyIdentity {
  contextId: string; originalBindingId: string; publicKey: string; fingerprint: string;
  generation: number; lifecycle: string;
  sourceLifecycle?: string; custody?: string;
}
export interface PortablePayloadReference {
  generation?: number | null;
  ownerKind: string; ownerId: string; role: string; fieldName?: string | null; versionId?: string | null;
  contextId: string; fingerprint?: string | null; legacyUnattributed?: boolean;
  originalBindingKind: string; originalBindingId: string; originalItemId?: string | null;
  keyType: string; algorithmProfile: string; envelopeVersion: number;
}
export interface PortableEnvelope {
  ciphertext?: string; encryptedContent?: string; encryptedDek: string; dekNonce: string;
  ephemeralPublicKey: string; contentNonce: string; keyFingerprint?: string;
}
export interface PortablePayload {
  reference: PortablePayloadReference; envelope: PortableEnvelope; binaryEntryPath?: string;
}
export interface PortableSessionScope { userId: string; sessionId: string; tenantId: string }
export interface PortableKeyImportOptions { contextId: string; passphrase?: string; derivationPassphrase?: string; signal?: AbortSignal }
export interface InspectedKey {
  kind: 'raw' | 'v1' | 'v2' | 'mnemonic'; requiresPassphrase: boolean;
  originalBindingId?: string; fingerprint?: string;
}

export function inspectKeyFile(text: string): InspectedKey {
  if (text.length > PORTABLE_LIMITS.keyBytes || textEncoder.encode(text).length > PORTABLE_LIMITS.keyBytes) fail('size-limit', 'The key input is too large.');
  const trimmed = text.trim();
  if (trimmed.startsWith('{') || trimmed.startsWith('[')) {
    const parsed = parsePortableJson(trimmed, PORTABLE_LIMITS.keyBytes);
    if (!object(parsed) || typeof parsed.format !== 'string' || !['knowz-vault-key-v1', 'knowz-vault-key-v2'].includes(parsed.format)) fail('key-format', 'Unsupported key file format.');
    if (parsed.keyType !== 'x25519-private') fail('key-type', 'Choose the matching private key file.');
    guid(parsed.vaultId);
    if (typeof parsed.fingerprint !== 'string' || !fingerprint.test(parsed.fingerprint)) fail('key-fingerprint', 'A full key fingerprint is required.');
    if (parsed.format === 'knowz-vault-key-v1') decodeBase64(parsed.key, 32);
    else {
      if (!object(parsed.cipher) || parsed.cipher.name !== 'AES-256-GCM' || !object(parsed.kdf)) fail('key-algorithm', 'Unsupported key protection.');
      decodeBase64(parsed.cipher.nonce, 12); decodeBase64(parsed.wrappedKey, 48); decodeBase64(parsed.kdf.salt, 16);
      const kdf = parsed.kdf;
      if (kdf.dkLen !== 32) fail('key-kdf', 'Unsupported key derivation parameters.');
      const positive = (value: unknown, max: number) => typeof value === 'number' && Number.isSafeInteger(value) && value > 0 && value <= max;
      if (kdf.name === 'argon2id') {
        if (!positive(kdf.m, 262144) || !positive(kdf.t, 6) || !positive(kdf.p, 4) || (kdf.m as number) < 8 * (kdf.p as number)) fail('key-kdf', 'Key derivation cost is unsupported or too high.');
      } else if (kdf.name !== 'PBKDF2' || kdf.hash !== 'SHA-256' || !positive(kdf.iterations, 2_000_000)) fail('key-kdf', 'Key derivation cost is unsupported or too high.');
    }
    return { kind: parsed.format.endsWith('v2') ? 'v2' : 'v1', requiresPassphrase: parsed.format.endsWith('v2'), originalBindingId: parsed.vaultId.toLowerCase(), fingerprint: parsed.fingerprint };
  }
  if (trimmed.split(/\s+/).length >= 12) {
    if (!validateMnemonic(trimmed.toLowerCase().replace(/\s+/g, ' '), wordlist)) fail('key-format', 'Invalid recovery phrase.');
    return { kind: 'mnemonic', requiresPassphrase: false };
  }
  decodeBase64(trimmed, 32);
  return { kind: 'raw', requiresPassphrase: false };
}

/** Private buffers never leave this session store; a lock epoch invalidates pending work. */
export class PortableKeyring {
  private readonly keys = new Map<string, Uint8Array>();
  private readonly catalog: PortableKeyIdentity[];
  private revision = 0;
  private lastUsed = Date.now();
  private readonly listeners = new Set<() => void>();
  private timer: ReturnType<typeof setTimeout> | undefined;
  readonly scope: Readonly<PortableSessionScope>;
  constructor(scope: PortableSessionScope, catalog: readonly PortableKeyIdentity[], private readonly idleMs = PORTABLE_LIMITS.keyIdleMs) {
    if (!scope.userId || !scope.sessionId || !scope.tenantId) fail('session', 'An isolated recovery session is required.');
    this.scope = Object.freeze({ ...scope });
    const seen = new Set<string>();
    this.catalog = catalog.map(identity => {
      guid(identity.contextId); guid(identity.originalBindingId);
      if (!fingerprint.test(identity.fingerprint) || fingerprintPublicKey(decodeBase64(identity.publicKey, 32)) !== identity.fingerprint || !Number.isSafeInteger(identity.generation) || identity.generation < 1) fail('key-catalog', 'Invalid public key catalog.');
      const id = `${identity.contextId}:${identity.generation}`;
      if (seen.has(id)) fail('key-catalog', 'Duplicate key identity.');
      seen.add(id);
      return Object.freeze({ ...identity });
    });
  }
  get epoch(): number { return this.revision; }
  subscribe(listener: () => void): () => void { this.listeners.add(listener); return () => this.listeners.delete(listener); }
  private notify() { for (const listener of this.listeners) listener(); }
  private touch() {
    this.lastUsed = Date.now(); clearTimeout(this.timer);
    this.timer = setTimeout(() => this.lock(), this.idleMs);
    // Do not keep Node processes alive solely for a browser-style session timer.
    (this.timer as unknown as { unref?: () => void }).unref?.();
  }
  snapshot() {
    if (Date.now() - this.lastUsed >= this.idleMs && this.keys.size) this.lock();
    return { scope: this.scope, epoch: this.revision, unlocked: [...this.keys.keys()], catalog: this.catalog.map(value => ({ ...value })) };
  }
  lock() {
    for (const key of this.keys.values()) key.fill(0);
    this.keys.clear(); this.revision++; clearTimeout(this.timer); this.timer = undefined; this.notify();
  }
  dispose() { this.lock(); this.listeners.clear(); }
  async importKey(text: string, options: PortableKeyImportOptions): Promise<string> {
    const epoch = this.revision;
    let bytes: Uint8Array | undefined;
    try {
      const inspected = inspectKeyFile(text);
      if (inspected.kind === 'raw') bytes = decodeBase64(text.trim(), 32);
      else if (inspected.kind === 'mnemonic') {
        const seed = mnemonicToSeedSync(text.trim().toLowerCase().replace(/\s+/g, ' '), options.derivationPassphrase ?? '');
        try { bytes = hkdf(sha256, seed, undefined, textEncoder.encode('knowz-vault-encryption-v1'), 32); } finally { seed.fill(0); }
      } else {
        const parsed = parsePortableJson(text, PORTABLE_LIMITS.keyBytes) as Record<string, unknown>;
        if (inspected.kind === 'v2') {
          if (!options.passphrase) fail('passphrase-required', 'Enter the passphrase protecting this key file.');
          try { bytes = await unwrapKeyFile(parsed as unknown as KnowzKeyFileV2, options.passphrase); }
          catch { fail('key-passphrase', 'Incorrect passphrase, or the key file is corrupt or has been changed.'); }
        } else bytes = decodeBase64(parsed.key, 32);
      }
      options.signal?.throwIfAborted();
      if (epoch !== this.revision) fail('session-changed', 'The recovery session was locked. Unlock again to continue.');
      const actual = fingerprintPublicKey(x25519.getPublicKey(bytes!));
      const match = this.catalog.find(value => value.contextId === options.contextId && value.fingerprint === actual);
      if (!match || (inspected.fingerprint && inspected.fingerprint !== actual) || (inspected.originalBindingId && inspected.originalBindingId !== match.originalBindingId.toLowerCase())) fail('key-mismatch', 'The private key does not match this original context and generation.');
      const keyId = `${match.contextId}:${match.fingerprint}`;
      this.keys.get(keyId)?.fill(0); this.keys.set(keyId, bytes!.slice()); this.touch(); this.notify();
      return actual;
    } catch (error) { if (epoch === this.revision) this.lock(); throw error; }
    finally { bytes?.fill(0); }
  }
  async withKey<T>(reference: PortablePayloadReference, operation: (key: Uint8Array) => Promise<T>, signal?: AbortSignal): Promise<T> {
    this.snapshot(); signal?.throwIfAborted();
    const identity = resolveContext(reference, this.catalog);
    const key = this.keys.get(`${identity.contextId}:${identity.fingerprint}`);
    if (!key) fail('missing-key', 'This payload needs its original private key generation.');
    const epoch = this.revision;
    const copy = key.slice(); this.touch();
    try {
      const result = await operation(copy);
      if (signal?.aborted || epoch !== this.revision) {
        if (result instanceof Uint8Array) result.fill(0);
        fail('session-changed', 'The recovery session changed. Unlock again to continue.');
      }
      return result;
    } finally { copy.fill(0); }
  }
}

export function resolveContext(reference: PortablePayloadReference, catalog: readonly PortableKeyIdentity[]): PortableKeyIdentity {
  if (reference.generation == null && new Set(catalog.filter(value => value.contextId === reference.contextId && value.fingerprint === reference.fingerprint).map(value => value.generation)).size > 1) fail('ambiguous-generation', 'The payload key generation is ambiguous.');
  const match = catalog.find(key => key.contextId === reference.contextId && key.fingerprint === reference.fingerprint && (reference.generation == null || key.generation === reference.generation));
  if (!match || (reference.generation != null && match.generation !== reference.generation) || match.originalBindingId.toLowerCase() !== reference.originalBindingId.toLowerCase()) fail('missing-key', 'The required original context and key generation are unavailable.');
  return match;
}
function aad(reference: PortablePayloadReference): Uint8Array | undefined {
  guid(reference.originalBindingId); guid(reference.contextId);
  if (!['knowledge', 'comment', 'file', 'attachment', 'version'].includes(reference.ownerKind)
    || !reference.ownerId || !['body', 'field', 'binary', 'variant'].includes(reference.role)
    || (reference.role === 'field' && !reference.fieldName)
    || !['vault', 'item', 'release-lock'].includes(reference.originalBindingKind)
    || !fingerprint.test(reference.fingerprint ?? '') || !['x25519-vault', 'x25519-item'].includes(reference.algorithmProfile)) fail('payload-reference', 'Unsupported or incomplete encrypted payload reference.');
  if (reference.keyType === 'x25519-vault-public' && reference.algorithmProfile !== 'x25519-vault'
    || reference.keyType === 'x25519-item-public' && reference.algorithmProfile !== 'x25519-item') fail('algorithm-profile', 'The encrypted key type and algorithm profile disagree.');
  if (reference.envelopeVersion === 1 && reference.keyType === 'x25519-vault-public') return undefined;
  if (reference.envelopeVersion !== 2) fail('envelope-version', 'Unsupported encrypted payload version.');
  let suffix = '';
  if (reference.keyType === 'x25519-item-public') { guid(reference.originalItemId); suffix = `|${reference.originalItemId.toLowerCase()}`; }
  else if (reference.keyType !== 'x25519-vault-public') fail('key-type', 'Unsupported encrypted payload key type.');
  return textEncoder.encode(`knowz-msg-v2|${reference.originalBindingId.toLowerCase()}|${reference.keyType}|2${suffix}`);
}
function validateEnvelope(envelope: PortableEnvelope, binary?: Uint8Array) {
  decodeBase64(envelope.ephemeralPublicKey, 32); decodeBase64(envelope.encryptedDek, 48);
  decodeBase64(envelope.dekNonce, 12); decodeBase64(envelope.contentNonce, 12);
  if (binary) {
    if (binary.length < 16 || binary.length > PORTABLE_LIMITS.encryptedFileBytes) fail('size-limit', 'Encrypted file exceeds the supported 64 MiB limit.');
  } else decodeBase64(envelope.ciphertext ?? envelope.encryptedContent, 16, PORTABLE_LIMITS.encryptedFileBytes);
}
export async function decryptPayload(payload: PortablePayload, keyring: PortableKeyring, options: { binary?: Uint8Array; signal?: AbortSignal } = {}): Promise<Uint8Array> {
  const associatedData = aad(payload.reference); validateEnvelope(payload.envelope, options.binary);
  if (payload.envelope.keyFingerprint && payload.envelope.keyFingerprint !== payload.reference.fingerprint) fail('key-fingerprint', 'Encrypted field key identity disagrees with the archive reference.');
  return keyring.withKey(payload.reference, async key => {
    try { return await decryptEnvelopeToBytes(key, { ...payload.envelope, ciphertextBytes: options.binary }, associatedData); }
    catch { return fail('authentication', safeFailure); }
  }, options.signal);
}
export async function encryptPayload(plaintext: Uint8Array, reference: PortablePayloadReference, identity: PortableKeyIdentity): Promise<PortablePayload> {
  if (identity.lifecycle !== 'active') fail('write-key', 'Select an eligible active original key for writing.');
  resolveContext(reference, [identity]);
  if (reference.envelopeVersion !== 2 && !(reference.role === 'field' && reference.envelopeVersion === 1)) fail('write-version', 'New body and file payloads require authenticated version 2.');
  if (plaintext.length > PORTABLE_LIMITS.encryptedFileBytes - 16) fail('size-limit', 'The encrypted payload would exceed 64 MiB.');
  const envelope = await encryptEnvelope(decodeBase64(identity.publicKey, 32), plaintext, aad(reference)!);
  if (reference.role === 'field') (envelope as PortableEnvelope).keyFingerprint = identity.fingerprint;
  return { reference: { ...reference }, envelope };
}
export const decryptBinary = (payload: PortablePayload, bytes: Uint8Array, keys: PortableKeyring, signal?: AbortSignal) => decryptPayload(payload, keys, { binary: bytes, signal });
export const encryptBinary = encryptPayload;
export function payloadIdentity(reference: PortablePayloadReference): string {
  return JSON.stringify([reference.ownerKind, reference.ownerId, reference.role, reference.fieldName ?? null, reference.versionId ?? null]);
}
export interface PortableVerificationResult {
  requiredPayloadCount: number; verifiedPayloadCount: number; complete: boolean;
  roleCounts: Record<string, { required: number; verified: number }>;
  missingKeys: string[]; errors: Array<{ payloadId: string; code: string }>; legacyAttributions: LegacyAttribution[];
}
export async function verifyAllArchivePayloads(payloads: readonly PortablePayload[], keys: PortableKeyring, options: {
  readBinary?: (path: string, signal?: AbortSignal) => Promise<Uint8Array>;
  legacyCandidates?: readonly LegacyKeyCandidate[];
  signal?: AbortSignal; onProgress?: (verified: number, processed: number, total: number) => void;
} = {}): Promise<PortableVerificationResult> {
  const seen = new Set<string>();
  for (const payload of payloads) {
    const id = payloadIdentity(payload.reference);
    if (seen.has(id)) fail('duplicate-payload', 'Duplicate encrypted payload identity.');
    seen.add(id);
  }
  const result: PortableVerificationResult = { requiredPayloadCount: payloads.length, verifiedPayloadCount: 0, complete: false, roleCounts: {}, missingKeys: [], errors: [], legacyAttributions: [] };
  const epoch = keys.epoch;
  for (const [index, payload] of payloads.entries()) {
    options.signal?.throwIfAborted();
    if (keys.epoch !== epoch) fail('session-changed', 'Recovery was cancelled because the key session changed.');
    const role = payload.reference.role;
    const count = result.roleCounts[role] ??= { required: 0, verified: 0 }; count.required++;
    try {
      const binary = payload.binaryEntryPath ? await options.readBinary?.(payload.binaryEntryPath, options.signal) : undefined;
      if (payload.binaryEntryPath && !binary) fail('missing-binary', 'Required encrypted file bytes are missing.');
      let plaintext: Uint8Array | undefined;
      try {
        if (payload.reference.legacyUnattributed) {
          const recovered = await recoverLegacyPayload(payload, keys, { candidates: (options.legacyCandidates ?? []).filter(key => key.contextId === payload.reference.contextId), binary, signal: options.signal });
          plaintext = recovered.plaintext; result.legacyAttributions.push(recovered.attribution);
        } else plaintext = await decryptPayload(payload, keys, { binary, signal: options.signal });
        count.verified++; result.verifiedPayloadCount++;
      } finally { plaintext?.fill(0); binary?.fill(0); }
    } catch (error) {
      if (options.signal?.aborted || keys.epoch !== epoch) throw error;
      const code = error instanceof PortableCryptoError ? error.code : 'verification-failed';
      const id = payloadIdentity(payload.reference);
      if (code === 'missing-key') result.missingKeys.push(id);
      else result.errors.push({ payloadId: id, code });
    }
    options.onProgress?.(result.verifiedPayloadCount, index + 1, payloads.length);
  }
  result.complete = result.verifiedPayloadCount === result.requiredPayloadCount;
  return result;
}

export interface PortableGraphRecord {
  ownerKind: string; id: string; record: Record<string, unknown>; payloads: PortablePayload[];
}
export interface PortableGraph {
  catalog: PortableKeyIdentity[]; payloads: PortablePayload[]; records: PortableGraphRecord[];
  plainData: Record<string, unknown[]>; omissions: unknown[]; preservationState: string;
  dependencyBoundItems: PortableDependencyBoundKnowledge[]; suspendedReleasePolicies: PortableSuspendedReleasePolicy[]; dependentPayloadCount: number;
}
/** Enumerates actual stored payloads and requires a one-to-one reference closure. */
export function extractPortableGraph(input: unknown, options: { detachedRecord?: boolean } = {}): PortableGraph {
  if (!object(input) || input.schemaVersion !== 5 || !Array.isArray(input.requiredCapabilities)) fail('graph-version', 'This is not a supported complete encrypted graph. Legacy transports cannot certify full recovery.');
  const rows = (name: string): Record<string, unknown>[] => {
    const value = input[name];
    if (value === null || value === undefined) return [];
    if (!Array.isArray(value) || value.length > PORTABLE_LIMITS.members || value.some(row => !object(row))) fail('graph-shape', 'Invalid graph collection.');
    return value as Record<string, unknown>[];
  };
  const contexts = new Map<string, Record<string, unknown>>();
  const dependencyBoundItems = rows('dependencyBoundItems').map(validateDependencyBoundItem);
  const suspendedReleasePolicies = rows('suspendedReleasePolicies').map(validateSuspendedReleasePolicy);
  const requiredCapabilities = ['knowz.encrypted-graph/v1', ...(dependencyBoundItems.length || suspendedReleasePolicies.length ? ['knowz.suspended-release/v1'] : [])];
  const capabilities = input.requiredCapabilities as unknown[];
  if (capabilities.length !== requiredCapabilities.length || requiredCapabilities.some(cap => !capabilities.includes(cap)) || new Set(capabilities).size !== requiredCapabilities.length) fail('graph-version', 'Unsupported or missing encrypted graph capability.');
  for (const value of rows('cryptoContexts')) {
    if (Object.keys(value).some(name => !['contextId', 'bindingKind', 'originalBindingId', 'algorithmProfile', 'provenanceStatus', 'sourceTenantId'].includes(name))) fail('context-metadata', 'Unknown fields in public crypto context metadata.');
    guid(value.contextId); guid(value.originalBindingId);
    if (!['vault', 'item', 'release-lock'].includes(String(value.bindingKind)) || !['x25519-vault', 'x25519-item'].includes(String(value.algorithmProfile))) fail('context-profile', 'Unsupported original crypto context declaration.');
    if (contexts.has(value.contextId)) fail('graph-context', 'Duplicate original context.');
    contexts.set(value.contextId, value);
  }
  const catalog = rows('cryptoKeyCatalog').map(value => {
    if (Object.keys(value).some(name => !['contextId', 'fingerprint', 'publicKey', 'keyType', 'generation', 'lifecycle', 'custody', 'sourceLifecycle'].includes(name))) fail('key-metadata', 'Unknown fields in public crypto key catalog metadata.');
    const context = contexts.get(String(value.contextId));
    if (!context) fail('graph-context', 'Public key has no original context.');
    if (!['x25519', 'x25519-vault-public', 'x25519-item-public'].includes(String(value.keyType))) fail('key-type', 'Unsupported public key catalog type.');
    return { ...value, originalBindingId: context.originalBindingId } as unknown as PortableKeyIdentity;
  });
  // Share exact catalog validation with interactive key import without retaining secrets.
  new PortableKeyring({ userId: 'validator', sessionId: 'validator', tenantId: 'validator' }, catalog).dispose();
  const references = rows('payloadKeyReferences') as unknown as PortablePayloadReference[];
  const remaining = new Set(references);
  const identities = new Set<string>();
  for (const reference of references) {
    const id = payloadIdentity(reference);
    if (identities.has(id)) fail('duplicate-payload', 'Duplicate encrypted payload reference.');
    identities.add(id);
  }
  const result: PortableGraph = { catalog, payloads: [], records: [], plainData: {}, omissions: [], preservationState: String(input.preservationState ?? 'complete'), dependencyBoundItems, suspendedReleasePolicies, dependentPayloadCount: dependencyBoundItems.reduce((count, row) => count + 1 + row.versions.length, 0) };
  if (!object(input.data)) fail('graph-shape', 'The graph data collections are missing.');
  for (const [name, value] of Object.entries(input.data)) {
    if (name === 'archives' && object(value)) {
      result.plainData.archives = Object.entries(value).map(([collection, records]) => {
        if (!Array.isArray(records)) fail('graph-shape', 'Invalid archived record collection.');
        return { collection, records };
      });
    } else if (Array.isArray(value)) result.plainData[name] = value;
    else if (value !== null && value !== undefined) fail('graph-shape', 'Invalid plain graph collection.');
  }
  result.plainData.plainVersions = rows('plainVersions');
  if (input.omissions !== undefined && input.omissions !== null) {
    if (!Array.isArray(input.omissions)) fail('graph-shape', 'Invalid omission inventory.');
    result.omissions = input.omissions;
  }
  if (result.preservationState === 'complete' && result.omissions.length) fail('graph-completeness', 'A complete graph cannot contain omissions.');
  const recordIds = new Set<string>();
  const newRecord = (ownerKind: string, record: Record<string, unknown>) => {
    guid(record.id);
    const id = `${ownerKind}:${record.id}`;
    if (recordIds.has(id)) fail('duplicate-record', 'Duplicate encrypted record.');
    recordIds.add(id);
    const value = { ownerKind, id: record.id, record, payloads: [] as PortablePayload[] };
    result.records.push(value); return value;
  };
  const add = (record: PortableGraphRecord, role: string, envelope: PortableEnvelope, fieldName?: string, binaryEntryPath?: string, storedVersion?: unknown, binding?: unknown, storedKeyReference?: unknown) => {
    const matches = references.filter(value => value.ownerKind === record.ownerKind && value.ownerId === record.id && value.role === role && (value.fieldName ?? undefined) === fieldName);
    if (matches.length !== 1) fail('graph-reference', 'Every encrypted payload requires one exact key reference.');
    const reference = matches[0]!;
    if (!['x25519-vault-public', 'x25519-item-public'].includes(reference.keyType) || ![1, 2].includes(reference.envelopeVersion)
      || reference.keyType === 'x25519-vault-public' && reference.algorithmProfile !== 'x25519-vault'
      || reference.keyType === 'x25519-item-public' && (reference.algorithmProfile !== 'x25519-item' || reference.envelopeVersion !== 2 || typeof reference.originalItemId !== 'string' || !uuid.test(reference.originalItemId))) fail('algorithm-profile', 'Unsupported encrypted key type, algorithm profile or envelope version.');
    const context = contexts.get(reference.contextId);
    if (!context || context.originalBindingId !== reference.originalBindingId || context.bindingKind !== reference.originalBindingKind || context.algorithmProfile !== reference.algorithmProfile
      || (binding && binding !== reference.originalBindingId) || (storedVersion !== undefined && (storedVersion ?? 1) !== reference.envelopeVersion)) fail('graph-binding', 'Payload binding or version disagrees with its original context.');
    if (reference.keyType === 'x25519-item-public' && reference.role === 'body' && ['knowledge', 'version'].includes(record.ownerKind)) {
      const subject = context.bindingKind === 'release-lock' ? context.originalBindingId : record.ownerKind === 'knowledge' ? record.record.originalItemId : record.record.originalKnowledgeId;
      if (reference.originalItemId !== subject) fail('item-subject', 'The item envelope subject disagrees with its immutable original owner identity.');
    }
    if (typeof storedKeyReference === 'string') {
      const parts = storedKeyReference.split(':');
      if (parts.length === 6 && parts[0] === 'vault' && parts[4] === 'sha256') {
        const generation = /^v[1-9][0-9]*$/.test(parts[3]!) ? Number(parts[3]!.slice(1)) : NaN;
        const key = reference.legacyUnattributed ? undefined : resolveContext(reference, catalog);
        if (!parts[2]?.trim() || parts[1]!.toLowerCase() !== reference.originalBindingId.toLowerCase() || !Number.isSafeInteger(generation) || !fingerprint.test(parts[5]!) || parts[5] !== reference.fingerprint || (reference.generation != null && generation !== reference.generation) || key && generation !== key.generation) fail('key-reference', 'The canonical stored key reference disagrees with its payload identity or inferred generation.');
      }
    }
    if (reference.legacyUnattributed) {
      if (result.preservationState === 'complete') fail('graph-attribution', 'Complete graphs require exact historical key attribution.');
    } else { aad(reference); resolveContext(reference, catalog); }
    if (envelope.keyFingerprint && envelope.keyFingerprint !== reference.fingerprint) fail('key-fingerprint', 'Encrypted field key identity disagrees with the archive reference.');
    if (binaryEntryPath) {
      if (!/^files\/[a-zA-Z0-9._/-]+$/.test(binaryEntryPath) || binaryEntryPath.includes('..') || binaryEntryPath.includes('//')) fail('binary-path', 'Unsafe encrypted file path.');
      decodeBase64(envelope.ephemeralPublicKey, 32); decodeBase64(envelope.encryptedDek, 48); decodeBase64(envelope.dekNonce, 12); decodeBase64(envelope.contentNonce, 12);
    } else validateEnvelope(envelope);
    const payload = { reference: { ...reference }, envelope, ...(binaryEntryPath ? { binaryEntryPath } : {}) };
    remaining.delete(reference); record.payloads.push(payload); result.payloads.push(payload);
  };
  const envelope = (row: Record<string, unknown>, ciphertext?: unknown): PortableEnvelope => ({
    ciphertext: (ciphertext ?? row.ciphertext) as string | undefined,
    encryptedDek: row.encryptedDek as string, dekNonce: (row.encryptedDekNonce ?? row.dekNonce) as string,
    ephemeralPublicKey: row.ephemeralPublicKey as string, contentNonce: row.contentNonce as string,
    ...(row.keyFingerprint ? { keyFingerprint: row.keyFingerprint as string } : {}),
  });
  const fields = (record: PortableGraphRecord, json: unknown) => {
    if (json === undefined || json === null || json === '') return;
    if (typeof json !== 'string') fail('field-schema', 'Encrypted field sidecar must be serialized JSON.');
    const sidecar = parsePortableJson(json);
    if (!object(sidecar) || sidecar.schema !== 'knowz-encrypted-fields-v1' || !object(sidecar.fields)) fail('field-schema', 'Unsupported encrypted field schema.');
    for (const [name, value] of Object.entries(sidecar.fields)) {
      if (!object(value)) fail('field-envelope', 'An encrypted field envelope is missing.');
      add(record, 'field', envelope(value), name, undefined, 1);
    }
  };
  for (const row of rows('sealedItems')) {
    const record = newRecord('knowledge', row);
    add(record, 'body', envelope(row), undefined, undefined, row.envelopeVersion ?? 1, row.cryptoBindingId, row.encryptionKeyRef);
    fields(record, row.encryptedFieldsJson);
  }
  for (const row of rows('sealedComments')) {
    const record = newRecord('comment', row);
    if (row.ciphertext) add(record, 'body', envelope(row), undefined, undefined, row.envelopeVersion ?? 1, row.cryptoBindingId, row.encryptionKeyRef);
    fields(record, row.encryptedFieldsJson);
    if (!record.payloads.some(value => value.reference.role === 'body' || value.reference.fieldName === 'content')) fail('comment-content', 'The encrypted comment content is missing.');
  }
  for (const row of rows('sealedFiles')) {
    const record = newRecord('file', row);
    if (row.binaryEntryPath && row.binaryContentBase64) fail('binary-source', 'A file cannot contain both inline and external bytes.');
    add(record, 'binary', envelope(row, row.binaryContentBase64), undefined, row.binaryEntryPath as string | undefined, row.envelopeVersion ?? 1, row.cryptoBindingId, row.encryptionKeyRef);
    fields(record, row.encryptedFieldsJson);
  }
  for (const row of result.plainData.knowledgeItems ?? []) {
    if (!object(row)) fail('graph-shape', 'Invalid ordinary knowledge record.');
    if (row.encryptedFieldsJson) fields(newRecord('knowledge', row), row.encryptedFieldsJson);
  }
  for (const row of result.plainData.fileRecords ?? []) {
    if (!object(row)) fail('graph-shape', 'Invalid plain file record.');
    if (row.encryptedFieldsJson) fields(newRecord('file', row), row.encryptedFieldsJson);
  }
  for (const row of rows('sealedAttachments')) fields(newRecord('attachment', row), row.encryptedFieldsJson);
  for (const row of rows('sealedVersions')) {
    const record = newRecord('version', row);
    if (typeof row.envelopeMetadata !== 'string') fail('version-snapshot', 'The encrypted version snapshot is missing.');
    const snapshot = parsePortableJson(row.envelopeMetadata);
    if (!object(snapshot) || snapshot.sealedSnapshot !== true || snapshot.isEncrypted !== undefined && snapshot.isEncrypted !== true) fail('version-snapshot', 'The encrypted version snapshot is incomplete.');
    if (snapshot.encryptionEnvelopeVersion != null && snapshot.envelopeVersion != null && snapshot.encryptionEnvelopeVersion !== snapshot.envelopeVersion) fail('version-snapshot', 'The encrypted version snapshot declares conflicting envelope versions.');
    add(record, 'body', envelope(snapshot, row.ciphertext), undefined, undefined, snapshot.encryptionEnvelopeVersion ?? snapshot.envelopeVersion ?? 1, snapshot.cryptoBindingId, snapshot.encryptionKeyRef);
    fields(record, snapshot.encryptedFieldsJson);
  }
  for (const row of rows('plainVersions')) {
    const sidecar = plainVersionFields(row.snapshotJson);
    if (sidecar) fields(newRecord('version', row), sidecar);
  }
  for (const row of dependencyBoundItems) {
    fields(newRecord('knowledge', row as unknown as Record<string, unknown>), row.encryptedFieldsJson);
    for (const version of row.versions) fields(newRecord('version', version as unknown as Record<string, unknown>), version.encryptedFieldsJson);
  }
  for (const policy of suspendedReleasePolicies) {
    const knowledge = [...rows('sealedItems'), ...dependencyBoundItems, ...result.plainData.knowledgeItems ?? []].find(row => object(row) && row.id === policy.knowledgeId) as Record<string, unknown> | undefined;
    if (!options.detachedRecord && (!knowledge || knowledge.originalItemId && knowledge.originalItemId !== policy.originalKnowledgeId)) fail('suspended-owner', 'Suspended policy has an absent or contradictory knowledge owner.');
  }
  if (remaining.size) fail('unused-reference', 'The graph declares encrypted payloads that are absent or unsupported.');
  validatePortableGraphClosure(input, options.detachedRecord);
  return result;
}

export * from './portable/recovery.js';
export * from './portable/custody.js';
export * from './portable/suspended-release.js';

export * from './portable/legacy.js';
export * from './portable/preservation.js';
