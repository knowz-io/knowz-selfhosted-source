// VENDORED — byte-identical copy of src/knowz-web-client/src/lib/key-file.ts; do not edit here. Sync via scripts/check-vendored-crypto.

/**
 * Standardized key file format (JSON envelope) for vault encryption keys.
 *
 * Supports:
 * - Creating key file envelopes from raw key bytes (v1 plaintext, opt-in)
 * - Creating passphrase-protected envelopes (v2 AEAD — see key-file-crypto.ts)
 * - Triggering browser downloads of .knowzkey files
 * - Parsing v2 (encrypted), v1 (plaintext) JSON envelopes, AND legacy raw Base64
 *
 * NodeID: KeyFileExport / FEAT_PassphraseWrappedKeyFile (N4)
 */

import type { KnowzKeyFileV2 } from './key-file-crypto'

// Re-export the v2 passphrase-protected API + types through this module so
// consumers keep a single `../../lib/key-file` import surface. The portal (N5)
// vendors both files byte-identical.
export {
  createEncryptedKeyFile,
  unwrapKeyFile,
  buildKeyFileAad,
  deriveWrappingKey,
} from './key-file-crypto'
export type {
  KnowzKeyFileV2,
  KeyFileKdfParams,
  Argon2idKdfParams,
  Pbkdf2KdfParams,
  CreateEncryptedKeyFileOptions,
} from './key-file-crypto'

/** The standardized key file envelope */
export interface KnowzKeyFile {
  format: 'knowz-vault-key-v1'
  vaultId: string
  keyType: 'x25519-private' | 'x25519-public'
  key: string          // Base64-encoded key bytes
  fingerprint: string  // SHA256 hex of the public key
  createdAt: string    // ISO-8601
}

/**
 * Create a KnowzKeyFile envelope from raw key bytes.
 */
export function createKeyFile(
  vaultId: string,
  keyType: KnowzKeyFile['keyType'],
  keyBytes: Uint8Array,
  publicKeyFingerprint: string,
): KnowzKeyFile {
  // Convert bytes to Base64
  let binary = ''
  for (let i = 0; i < keyBytes.length; i++) {
    binary += String.fromCharCode(keyBytes[i])
  }
  const key = btoa(binary)

  return {
    format: 'knowz-vault-key-v1',
    vaultId,
    keyType,
    key,
    fingerprint: publicKeyFingerprint,
    createdAt: new Date().toISOString(),
  }
}

/**
 * Trigger a browser download of a .knowzkey file.
 *
 * Accepts both v1 plaintext (`KnowzKeyFile`) and v2 passphrase-protected
 * (`KnowzKeyFileV2`) envelopes — both carry `vaultId`/`keyType` so the filename
 * logic is identical.
 *
 * Filename: {sanitizedVaultName}-{private|public}.knowzkey
 */
export function downloadKeyFile(keyFile: KnowzKeyFile | KnowzKeyFileV2, vaultName: string): void {
  const sanitized = vaultName.replace(/[^a-zA-Z0-9]/g, '-')
  const suffix = keyFile.keyType === 'x25519-private' ? 'private' : 'public'
  const filename = `${sanitized}-${suffix}.knowzkey`

  const blob = new Blob([JSON.stringify(keyFile, null, 2)], { type: 'application/json' })
  const url = URL.createObjectURL(blob)

  const a = document.createElement('a')
  a.href = url
  a.download = filename
  document.body.appendChild(a)
  a.click()
  document.body.removeChild(a)
  URL.revokeObjectURL(url)
}

/**
 * Result of parsing a key file. `kind` discriminates the detected format.
 *
 * `key` is ALWAYS present (backward compatible with legacy consumers that read
 * `.key` directly): it holds the Base64 plaintext key for `raw` and `v1`, and
 * is an empty string for `v2` (the key is wrapped — call `unwrapKeyFile(file, ...)`
 * with the passphrase to recover the bytes).
 */
export interface ParsedKeyFileContent {
  kind: 'raw' | 'v1' | 'v2'
  key: string
  vaultId?: string
  keyType?: KnowzKeyFile['keyType']
  fingerprint?: string
  /** True when the file is an encrypted v2 envelope requiring a passphrase. */
  encrypted?: boolean
  /** The parsed v2 envelope — present only when `kind === 'v2'`. */
  file?: KnowzKeyFileV2
}

/**
 * Parse the contents of a key file: v2 (encrypted JSON), v1 (plaintext JSON),
 * or a legacy raw Base64 string.
 *
 * Detection order: JSON-like inputs are parsed and schema-checked; non-JSON input
 * remains eligible for the legacy raw-Base64 path. Unknown or malformed JSON fails
 * closed instead of being reinterpreted as key bytes.
 */
export function parseKeyFileContent(text: string): ParsedKeyFileContent {
  const trimmed = text.trim()
  let parsed: unknown

  try {
    parsed = JSON.parse(trimmed)
  } catch {
    if (trimmed.startsWith('{') || trimmed.startsWith('[')) {
      throw new Error('Invalid key file JSON.')
    }
    return { kind: 'raw', key: trimmed }
  }

  if (!isRecord(parsed) || typeof parsed.format !== 'string') {
    throw new Error('Unsupported key file format.')
  }

  if (parsed.format === 'knowz-vault-key-v1') {
    if (!isValidV1KeyFile(parsed)) {
      throw new Error('Invalid v1 key file.')
    }
    const file = parsed as unknown as KnowzKeyFile
    return {
      kind: 'v1',
      key: file.key,
      vaultId: file.vaultId,
      keyType: file.keyType,
      fingerprint: file.fingerprint,
    }
  }

  if (parsed.format === 'knowz-vault-key-v2') {
    if (!isValidV2KeyFile(parsed)) {
      throw new Error('Invalid v2 key file.')
    }
    const file = parsed as unknown as KnowzKeyFileV2
    return {
      kind: 'v2',
      key: '',
      encrypted: true,
      file,
      vaultId: file.vaultId,
      keyType: file.keyType,
      fingerprint: file.fingerprint,
    }
  }

  throw new Error('Unsupported key file format.')
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function isNonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0
}

function isKeyType(value: unknown): value is KnowzKeyFile['keyType'] {
  return value === 'x25519-private' || value === 'x25519-public'
}

function isPositiveInteger(value: unknown): value is number {
  return Number.isInteger(value) && (value as number) > 0
}

function isValidV1KeyFile(value: Record<string, unknown>): boolean {
  return value.format === 'knowz-vault-key-v1'
    && isNonEmptyString(value.vaultId)
    && isKeyType(value.keyType)
    && isNonEmptyString(value.key)
    && isNonEmptyString(value.fingerprint)
    && isNonEmptyString(value.createdAt)
}

function isValidV2KeyFile(value: Record<string, unknown>): boolean {
  if (
    value.format !== 'knowz-vault-key-v2'
    || !isNonEmptyString(value.vaultId)
    || !isKeyType(value.keyType)
    || !isNonEmptyString(value.wrappedKey)
    || !isNonEmptyString(value.fingerprint)
    || !isNonEmptyString(value.createdAt)
    || !isRecord(value.kdf)
    || !isRecord(value.cipher)
  ) {
    return false
  }

  const cipherValid = value.cipher.name === 'AES-256-GCM'
    && isNonEmptyString(value.cipher.nonce)
  if (!cipherValid || !isNonEmptyString(value.kdf.salt) || !isPositiveInteger(value.kdf.dkLen)) {
    return false
  }

  if (value.kdf.name === 'argon2id') {
    return isPositiveInteger(value.kdf.m)
      && isPositiveInteger(value.kdf.t)
      && isPositiveInteger(value.kdf.p)
  }

  return value.kdf.name === 'PBKDF2'
    && value.kdf.hash === 'SHA-256'
    && isPositiveInteger(value.kdf.iterations)
}
