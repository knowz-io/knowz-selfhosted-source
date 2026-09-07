// VENDORED — byte-identical copy of src/knowz-web-client/src/lib/key-file-crypto.ts; do not edit here. Sync via scripts/check-vendored-crypto.

/**
 * Passphrase-protected key-file wrapping (`knowz-vault-key-v2`).
 *
 * Self-contained AEAD envelope for OPTIONALLY protecting an exported vault key
 * with a passphrase. This is DISTINCT from vault-crypto.ts envelope crypto:
 * here we do KDF(passphrase) -> AES-256-GCM key-wrap, NOT X25519 ECDH content
 * encryption. The two operations must not be conflated.
 *
 * Portability constraint (the standalone decrypt portal vendors this file
 * byte-identical): depends ONLY on WebCrypto (`crypto.subtle`) and
 * `@noble/hashes/argon2`. It MUST NOT import from vault-crypto.ts or any other
 * app module.
 *
 * KDF choice (spec R2):
 *  - Default: Argon2id (m = 65536 KiB / 64 MiB, t = 3, p = 1, 32-byte output)
 *    via `@noble/hashes` (pure JS, bundles offline, no WASM/CDN fetch).
 *    GPU-brute-force resistant.
 *  - Fallback: PBKDF2-HMAC-SHA256, 600,000 iterations (OWASP 2023 floor) via
 *    native WebCrypto — zero-dependency, always available.
 * The envelope is self-describing: it declares which KDF + params were used so
 * unwrap is deterministic and future-proof.
 *
 * AAD (spec R4): AES-256-GCM binds the file context so a tampered `vaultId` or
 * `keyType` header fails the auth tag:
 *   AAD = UTF-8("knowz-vault-key-v2|" + vaultId + "|" + keyType)
 *
 * NodeID: FEAT_PassphraseWrappedKeyFile (N4)
 * WorkGroupID: kc-feat-vault-encryption-e2e-20260703-135844
 */

import { argon2idAsync } from '@noble/hashes/argon2.js'

// ============================================================================
// Types
// ============================================================================

export type KeyFileKeyType = 'x25519-private' | 'x25519-public'

export interface Argon2idKdfParams {
  name: 'argon2id'
  /** Memory cost in KiB. */
  m: number
  /** Time cost (passes/iterations). */
  t: number
  /** Parallelism. */
  p: number
  /** Base64-encoded random salt (16 bytes). */
  salt: string
  /** Derived-key length in bytes (32). */
  dkLen: number
}

export interface Pbkdf2KdfParams {
  name: 'PBKDF2'
  hash: 'SHA-256'
  iterations: number
  /** Base64-encoded random salt (16 bytes). */
  salt: string
  /** Derived-key length in bytes (32). */
  dkLen: number
}

export type KeyFileKdfParams = Argon2idKdfParams | Pbkdf2KdfParams

/** The passphrase-protected key file envelope (opt-in v2). */
export interface KnowzKeyFileV2 {
  format: 'knowz-vault-key-v2'
  vaultId: string
  keyType: KeyFileKeyType
  kdf: KeyFileKdfParams
  cipher: { name: 'AES-256-GCM'; nonce: string }
  /** Base64-encoded AES-256-GCM output: ciphertext(key) || 16-byte tag. */
  wrappedKey: string
  /** SHA-256 hex of the public key (plaintext metadata, mirrors v1). */
  fingerprint: string
  createdAt: string
}

export interface CreateEncryptedKeyFileOptions {
  /** Which KDF to use. Default 'argon2id' (falls back to PBKDF2 if unavailable). */
  kdf?: 'argon2id' | 'pbkdf2'
  /**
   * Override Argon2id cost params. Production omits these -> spec defaults
   * (m=65536, t=3, p=1). Primarily for tests / low-end-device tuning; the
   * chosen values are recorded in the envelope so unwrap stays deterministic.
   */
  argon2?: { m?: number; t?: number; p?: number }
}

// ============================================================================
// Constants (spec R2 defaults)
// ============================================================================

export const ARGON2ID_MEMORY_KIB = 65536
export const ARGON2ID_TIME_COST = 3
export const ARGON2ID_PARALLELISM = 1
export const ARGON2ID_MIN_MEMORY_KIB = 512
export const ARGON2ID_MAX_MEMORY_KIB = 262144
export const ARGON2ID_MAX_TIME_COST = 6
export const ARGON2ID_MAX_PARALLELISM = 4
export const PBKDF2_MIN_ITERATIONS = 600_000
export const PBKDF2_MAX_ITERATIONS = 2_000_000
export const KDF_OUTPUT_BYTES = 32
export const SALT_BYTES = 16
export const NONCE_BYTES = 12
export const TAG_BYTES = 16
export const V2_FORMAT = 'knowz-vault-key-v2' as const

/**
 * Friendly, non-leaking error surfaced for any AES-GCM auth failure — wrong
 * passphrase, tampered `vaultId`/`keyType` header (AAD mismatch), or corrupted
 * ciphertext. Never echoes the key material or a stack trace.
 */
export const UNWRAP_FAILURE_MESSAGE =
  'Incorrect passphrase, or the key file is corrupted or has been tampered with.'

/**
 * Surfaced when an (attacker-controlled) v2 envelope declares a KDF cost above our
 * resource-exhaustion ceiling. We REJECT rather than clamp — see
 * {@link assertKdfWithinDosLimits}. Non-leaking: never echoes key material.
 */
export const KDF_COST_REJECTED_MESSAGE =
  'Key file requests an excessive KDF cost and was rejected.'

// ============================================================================
// AAD binding
// ============================================================================

/** AAD = UTF-8("knowz-vault-key-v2|" + vaultId + "|" + keyType). */
export function buildKeyFileAad(vaultId: string, keyType: KeyFileKeyType): Uint8Array {
  return new TextEncoder().encode(`${V2_FORMAT}|${vaultId}|${keyType}`)
}

// ============================================================================
// KDF
// ============================================================================

/**
 * DoS guard for an (attacker-controlled) v2 envelope's declared KDF params.
 *
 * REJECTS out-of-range cost — it deliberately does NOT clamp. KDF params live
 * OUTSIDE the AES-GCM wrap AAD, so the wrapping key is a pure function of the
 * RECORDED params (m/t/p or iterations, salt, dkLen). A legitimately-created file
 * must therefore be unwrapped with its ACTUAL recorded params; silently clamping
 * them to a different value derives a DIFFERENT key and fails the GCM tag on the
 * CORRECT passphrase (the F1 regression that clamping caused).
 *
 * There is deliberately NO lower bound. The file was already created with whatever
 * cost it recorded; rejecting weak params on unwrap cannot retroactively strengthen
 * it and would only lock out valid files (low-end-device / test exports below the
 * recommended {@link ARGON2ID_MIN_MEMORY_KIB} / {@link PBKDF2_MIN_ITERATIONS} floor).
 * We keep only an UPPER ceiling to bound resource exhaustion on import, and reject
 * (throw) rather than silently degrade.
 *
 * @throws {Error} {@link KDF_COST_REJECTED_MESSAGE} when the declared cost exceeds
 * the resource-exhaustion ceiling.
 */
export function assertKdfWithinDosLimits(params: KeyFileKdfParams): void {
  if (params.name === 'argon2id') {
    if (
      params.m > ARGON2ID_MAX_MEMORY_KIB ||
      params.t > ARGON2ID_MAX_TIME_COST ||
      params.p > ARGON2ID_MAX_PARALLELISM ||
      params.dkLen > KDF_OUTPUT_BYTES
    ) {
      throw new Error(KDF_COST_REJECTED_MESSAGE)
    }
    return
  }

  if (params.iterations > PBKDF2_MAX_ITERATIONS || params.dkLen > KDF_OUTPUT_BYTES) {
    throw new Error(KDF_COST_REJECTED_MESSAGE)
  }
}

/**
 * Derive a 32-byte wrapping key from a passphrase using the declared KDF params.
 * Deterministic: identical (passphrase, params incl. salt) -> identical output.
 */
export async function deriveWrappingKey(
  passphrase: string,
  params: KeyFileKdfParams,
): Promise<Uint8Array> {
  const passphraseBytes = new TextEncoder().encode(passphrase)
  const salt = base64ToBytes(params.salt)

  if (params.name === 'argon2id') {
    return argon2idAsync(passphraseBytes, salt, {
      t: params.t,
      m: params.m,
      p: params.p,
      dkLen: params.dkLen,
    })
  }

  // PBKDF2-HMAC-SHA256 via native WebCrypto.
  const baseKey = await crypto.subtle.importKey('raw', passphraseBytes, 'PBKDF2', false, [
    'deriveBits',
  ])
  const bits = await crypto.subtle.deriveBits(
    { name: 'PBKDF2', salt: new Uint8Array(salt), iterations: params.iterations, hash: params.hash },
    baseKey,
    params.dkLen * 8,
  )
  return new Uint8Array(bits)
}

function buildPbkdf2Params(saltB64: string): Pbkdf2KdfParams {
  return {
    name: 'PBKDF2',
    hash: 'SHA-256',
    iterations: PBKDF2_MIN_ITERATIONS,
    salt: saltB64,
    dkLen: KDF_OUTPUT_BYTES,
  }
}

function buildArgon2idParams(saltB64: string, opts?: CreateEncryptedKeyFileOptions): Argon2idKdfParams {
  return {
    name: 'argon2id',
    m: opts?.argon2?.m ?? ARGON2ID_MEMORY_KIB,
    t: opts?.argon2?.t ?? ARGON2ID_TIME_COST,
    p: opts?.argon2?.p ?? ARGON2ID_PARALLELISM,
    salt: saltB64,
    dkLen: KDF_OUTPUT_BYTES,
  }
}

// ============================================================================
// Wrap / unwrap
// ============================================================================

/**
 * Wrap raw key bytes into a passphrase-protected v2 envelope.
 * @throws if `passphrase` is empty (use the plaintext v1 path explicitly instead).
 */
export async function createEncryptedKeyFile(
  vaultId: string,
  keyType: KeyFileKeyType,
  keyBytes: Uint8Array,
  fingerprint: string,
  passphrase: string,
  opts?: CreateEncryptedKeyFileOptions,
): Promise<KnowzKeyFileV2> {
  if (!passphrase) {
    throw new Error('A passphrase is required to create an encrypted key file.')
  }

  const salt = randomBytes(SALT_BYTES)
  const nonce = randomBytes(NONCE_BYTES)
  const saltB64 = bytesToBase64(salt)
  const requested = opts?.kdf ?? 'argon2id'

  let kdfParams: KeyFileKdfParams
  let wrappingKey: Uint8Array

  if (requested === 'argon2id') {
    const argonParams = buildArgon2idParams(saltB64, opts)
    try {
      wrappingKey = await deriveWrappingKey(passphrase, argonParams)
      kdfParams = argonParams
    } catch {
      // Argon2id unavailable at runtime -> deterministic PBKDF2 fallback.
      kdfParams = buildPbkdf2Params(saltB64)
      wrappingKey = await deriveWrappingKey(passphrase, kdfParams)
    }
  } else {
    kdfParams = buildPbkdf2Params(saltB64)
    wrappingKey = await deriveWrappingKey(passphrase, kdfParams)
  }

  const aad = buildKeyFileAad(vaultId, keyType)
  let wrapped: Uint8Array
  try {
    wrapped = await aesGcmEncrypt(wrappingKey, nonce, keyBytes, aad)
  } finally {
    wrappingKey.fill(0) // best-effort zeroization of derived key material
  }

  return {
    format: V2_FORMAT,
    vaultId,
    keyType,
    kdf: kdfParams,
    cipher: { name: 'AES-256-GCM', nonce: bytesToBase64(nonce) },
    wrappedKey: bytesToBase64(wrapped),
    fingerprint,
    createdAt: new Date().toISOString(),
  }
}

/**
 * Unwrap a v2 envelope back to the raw key bytes.
 * A wrong passphrase, tampered header (AAD), or corrupted ciphertext all fail
 * the GCM auth tag and surface as {@link UNWRAP_FAILURE_MESSAGE} — never a
 * silent wrong-key result, stack trace, or key/plaintext echo.
 */
export async function unwrapKeyFile(file: KnowzKeyFileV2, passphrase: string): Promise<Uint8Array> {
  if (!file || file.format !== V2_FORMAT) {
    throw new Error('Not a knowz-vault-key-v2 encrypted key file.')
  }

  // DoS guard: reject (never clamp) an out-of-range KDF cost BEFORE deriving.
  // We then derive with the file's ACTUAL recorded params so the wrapping key
  // matches what wrap used — clamping would derive a different key and break a
  // legitimately-created file on the correct passphrase.
  assertKdfWithinDosLimits(file.kdf)

  const wrappingKey = await deriveWrappingKey(passphrase, file.kdf)
  const aad = buildKeyFileAad(file.vaultId, file.keyType)
  const nonce = base64ToBytes(file.cipher.nonce)
  const wrapped = base64ToBytes(file.wrappedKey)

  try {
    return await aesGcmDecrypt(wrappingKey, nonce, wrapped, aad)
  } catch {
    // GCM auth failure: wrong passphrase, AAD/header tamper, or corrupt ciphertext.
    throw new Error(UNWRAP_FAILURE_MESSAGE)
  } finally {
    wrappingKey.fill(0)
  }
}

// ============================================================================
// WebCrypto AES-256-GCM helpers (tag appended, matches vault-crypto.ts layout)
// ============================================================================

async function aesGcmEncrypt(
  keyBytes: Uint8Array,
  nonce: Uint8Array,
  plaintext: Uint8Array,
  aad: Uint8Array,
): Promise<Uint8Array> {
  const key = await crypto.subtle.importKey('raw', new Uint8Array(keyBytes), { name: 'AES-GCM' }, false, ['encrypt'])
  const ciphertext = await crypto.subtle.encrypt(
    { name: 'AES-GCM', iv: new Uint8Array(nonce), additionalData: new Uint8Array(aad), tagLength: TAG_BYTES * 8 },
    key,
    new Uint8Array(plaintext),
  )
  return new Uint8Array(ciphertext)
}

async function aesGcmDecrypt(
  keyBytes: Uint8Array,
  nonce: Uint8Array,
  ciphertextWithTag: Uint8Array,
  aad: Uint8Array,
): Promise<Uint8Array> {
  const key = await crypto.subtle.importKey('raw', new Uint8Array(keyBytes), { name: 'AES-GCM' }, false, ['decrypt'])
  const plaintext = await crypto.subtle.decrypt(
    { name: 'AES-GCM', iv: new Uint8Array(nonce), additionalData: new Uint8Array(aad), tagLength: TAG_BYTES * 8 },
    key,
    new Uint8Array(ciphertextWithTag),
  )
  return new Uint8Array(plaintext)
}

// ============================================================================
// Local helpers (self-contained — no imports from vault-crypto.ts)
// ============================================================================

function randomBytes(n: number): Uint8Array {
  return crypto.getRandomValues(new Uint8Array(n))
}

export function bytesToBase64(bytes: Uint8Array): string {
  let binary = ''
  for (let i = 0; i < bytes.length; i++) {
    binary += String.fromCharCode(bytes[i])
  }
  return btoa(binary)
}

export function base64ToBytes(base64: string): Uint8Array {
  const binary = atob(base64)
  const bytes = new Uint8Array(binary.length)
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i)
  }
  return bytes
}
