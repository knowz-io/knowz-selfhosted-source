// VENDORED — byte-identical copy of src/knowz-web-client/src/lib/portable-message.ts; do not edit here. Sync via scripts/check-vendored-crypto.

/**
 * Portable "Knowz Encrypted Message" — a self-describing, versioned export that the standalone decrypt
 * portal (N5) consumes with no server call. Mirrors the C# DTO in
 * `src/Knowz.Shared/DTOs/PortableEncryptedMessageDto.cs` and the crypto in `VaultCryptoService.cs`.
 *
 * The content-layer AAD produced by {@link buildContentAad} MUST be byte-identical to
 * `IVaultCryptoService.BuildContentAad` in C# (golden vectors — N6):
 *   AAD = UTF-8("knowz-msg-v2|" + vaultId + "|" + keyType + "|" + version)
 * where `vaultId` is the lowercase 36-char hyphenated form (verbatim string — NEVER a byte layout,
 * which would break C#↔TS parity).
 *
 * WorkGroupID: kc-feat-vault-encryption-e2e-20260703-135844 (N3 DATA_PortableEncryptedMessageEnvelope)
 */

import { EncryptedEnvelope, decryptEnvelopeToBytes } from './vault-crypto'

// ============================================================================
// Constants (must match server-side + the pinned wire format)
// ============================================================================

export const PORTABLE_MESSAGE_FORMAT = 'knowz-encrypted-message-v2'
export const PORTABLE_MESSAGE_VERSION = 2
export const PORTABLE_KEY_TYPE = 'x25519-vault-public'

const PORTABLE_ALG = {
  kdf: 'hkdf-sha256',
  keyAgreement: 'x25519',
  dekWrap: 'aes-256-gcm',
  content: 'aes-256-gcm',
} as const

// ============================================================================
// Types
// ============================================================================

export interface PortableEncryptedMessage {
  format: 'knowz-encrypted-message-v2'
  version: 2
  alg: { kdf: 'hkdf-sha256'; keyAgreement: 'x25519'; dekWrap: 'aes-256-gcm'; content: 'aes-256-gcm' }
  binding: { vaultId: string; keyType: 'x25519-vault-public' }
  contentAadBound: boolean
  envelope: {
    ephemeralPublicKey: string
    encryptedDek: string
    dekNonce: string
    contentNonce: string
    ciphertext: string
  } // all base64
  fingerprint: string // sha256hex of vault public key
  createdAt: string // ISO-8601
}

// ============================================================================
// Canonical content-layer AAD (byte-identical to C# BuildContentAad)
// ============================================================================

/**
 * Build the canonical content-layer AAD. Byte-for-byte identical to C#
 * `IVaultCryptoService.BuildContentAad`. `vaultId` is used verbatim (lowercase hyphenated GUID string).
 */
export function buildContentAad(vaultId: string, keyType: string, version: number): Uint8Array {
  return new TextEncoder().encode(`knowz-msg-v2|${vaultId}|${keyType}|${version}`)
}

// ============================================================================
// Build (client-side export packaging)
// ============================================================================

/**
 * Repackage an encrypted envelope (the fields the SPA already fetches from `EncryptedContentResponse`)
 * into a self-describing portable message.
 *
 * @param env The encrypted envelope (base64 fields). `ciphertext` may be supplied via `encryptedContent`
 *   or `ciphertext` on the envelope object.
 * @param opts.vaultId Lowercase hyphenated vault GUID (folded into the AAD binding).
 * @param opts.fingerprint SHA-256 hex fingerprint of the vault public key.
 * @param opts.envelopeVersion Stored `EncryptionEnvelopeVersion` (1 ⇒ legacy no-AAD; 2 ⇒ AAD-bound).
 */
export function buildPortableMessage(
  env: EncryptedEnvelope,
  opts: { vaultId: string; fingerprint: string; envelopeVersion: number; createdAt?: Date }
): PortableEncryptedMessage {
  const ciphertext = env.encryptedContent ?? env.ciphertext
  if (!ciphertext) {
    throw new Error('buildPortableMessage: envelope is missing ciphertext (encryptedContent/ciphertext)')
  }

  return {
    format: PORTABLE_MESSAGE_FORMAT,
    version: PORTABLE_MESSAGE_VERSION,
    alg: { ...PORTABLE_ALG },
    binding: { vaultId: opts.vaultId, keyType: PORTABLE_KEY_TYPE },
    contentAadBound: opts.envelopeVersion === 2,
    envelope: {
      ephemeralPublicKey: env.ephemeralPublicKey,
      encryptedDek: env.encryptedDek,
      dekNonce: env.dekNonce,
      contentNonce: env.contentNonce,
      ciphertext,
    },
    fingerprint: opts.fingerprint,
    createdAt: (opts.createdAt ?? new Date()).toISOString(),
  }
}

// ============================================================================
// Parse (strict schema — throws on invalid, never leaks plaintext)
// ============================================================================

function fail(reason: string): never {
  throw new Error(`Invalid Knowz encrypted message: ${reason}`)
}

function requireNonEmptyString(obj: Record<string, unknown>, key: string): void {
  if (typeof obj[key] !== 'string' || (obj[key] as string).length === 0) {
    fail(`missing or invalid "${key}"`)
  }
}

/**
 * Strictly parse a portable message from text. Throws on any schema violation (missing
 * format/version/binding/envelope, wrong format id, or version !== 2). The version discriminator is the
 * ONLY authority for whether AAD binding applies — never inferred from field presence.
 */
export function parsePortableMessage(text: string): PortableEncryptedMessage {
  let parsed: unknown
  try {
    parsed = JSON.parse(text)
  } catch {
    fail('not valid JSON')
  }
  if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
    fail('not an object')
  }
  const obj = parsed as Record<string, unknown>

  if (obj.format !== PORTABLE_MESSAGE_FORMAT) fail(`unexpected format "${String(obj.format)}"`)
  if (obj.version !== PORTABLE_MESSAGE_VERSION) fail(`unsupported version ${String(obj.version)}`)

  if (obj.binding === null || typeof obj.binding !== 'object') fail('missing "binding"')
  const binding = obj.binding as Record<string, unknown>
  requireNonEmptyString(binding, 'vaultId')
  if (binding.keyType !== PORTABLE_KEY_TYPE) fail(`unexpected binding.keyType "${String(binding.keyType)}"`)

  if (typeof obj.contentAadBound !== 'boolean') fail('missing "contentAadBound"')

  if (obj.alg === null || typeof obj.alg !== 'object') fail('missing "alg"')

  if (obj.envelope === null || typeof obj.envelope !== 'object') fail('missing "envelope"')
  const envelope = obj.envelope as Record<string, unknown>
  for (const field of ['ephemeralPublicKey', 'encryptedDek', 'dekNonce', 'contentNonce', 'ciphertext']) {
    requireNonEmptyString(envelope, field)
  }

  return obj as unknown as PortableEncryptedMessage
}

// ============================================================================
// Decrypt (deterministic from the declared version + contentAadBound flag)
// ============================================================================

/**
 * Decrypt a self-describing v2 portable message with the vault's X25519 private key.
 * Decryption is deterministic: `contentAadBound === true` ⇒ bind the canonical AAD; otherwise (legacy
 * v1 exports) ⇒ no AAD. AES-GCM authenticity makes a tampered binding fail (no plaintext).
 */
export async function decryptPortableMessage(
  privateKey: Uint8Array,
  msg: PortableEncryptedMessage
): Promise<string> {
  const envelope: EncryptedEnvelope = {
    ephemeralPublicKey: msg.envelope.ephemeralPublicKey,
    encryptedDek: msg.envelope.encryptedDek,
    dekNonce: msg.envelope.dekNonce,
    contentNonce: msg.envelope.contentNonce,
    ciphertext: msg.envelope.ciphertext,
  }

  const aad = msg.contentAadBound
    ? buildContentAad(msg.binding.vaultId, msg.binding.keyType, msg.version)
    : undefined

  const bytes = await decryptEnvelopeToBytes(privateKey, envelope, aad)
  return new TextDecoder().decode(bytes)
}
