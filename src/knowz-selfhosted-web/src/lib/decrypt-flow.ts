/**
 * Decrypt-flow orchestration for the offline portal (spec "Decrypt flow" + R7/R9/R10).
 *
 *   1. Guard + parse inputs (input-guard.ts).
 *   2. Resolve the private key: `.knowzkey` v1 (raw) / v2 (passphrase-unwrapped) /
 *      raw Base64 / BIP-39 mnemonic (deriveKeyPairFromMnemonic).
 *   3. Derive the public-key fingerprint (anti-phishing provenance, R10).
 *   4. decryptPortableMessage(privateKey, msg) — deterministic on `contentAadBound`.
 *   5. `finally`: zero all key material (R7 — memory-only, never persisted).
 *
 * Every failure path throws a {@link SafeDecryptError} whose `message` is a curated,
 * non-leaking string. The UI renders `error.message` directly and can trust it never
 * contains a stack, the raw input, key material, or partial plaintext.
 *
 * WorkGroupID: kc-feat-vault-encryption-e2e-20260703-135844 (N5 FEAT_StandaloneDecryptPortal)
 */

import { x25519 } from '@noble/curves/ed25519.js'
import {
  base64ToBytes,
  computeFingerprint,
  deriveKeyPairFromMnemonic,
  validateMnemonicPhrase,
} from './vendored/vault-crypto'
import { parseKeyFileContent, unwrapKeyFile } from './vendored/key-file'
import { decryptPortableMessage } from './vendored/portable-message'
import { guardedParsePortableMessage, assertKeyWithinSizeCap } from './input-guard'

// ============================================================================
// Errors (curated, non-leaking — spec R9 fail-closed)
// ============================================================================

export type DecryptErrorKind = 'passphrase-required' | 'passphrase' | 'generic'

export const GENERIC_DECRYPT_ERROR =
  'Could not decrypt. Check that your key and message match, then try again.'
export const PASSPHRASE_REQUIRED_MESSAGE =
  'This key file is passphrase-protected. Enter its passphrase to unlock it.'
export const PASSPHRASE_WRONG_MESSAGE =
  'Incorrect passphrase, or the key file is corrupted or has been tampered with.'
export const MISSING_KEY_MESSAGE = 'Paste or upload a key file, or enter your recovery phrase.'
export const MISSING_MESSAGE_MESSAGE = 'Paste or upload a Knowz encrypted message.'
export const INVALID_KEY_MESSAGE =
  "That doesn't look like a valid vault key, key file, or recovery phrase."
export const PUBLIC_KEY_MESSAGE =
  'This is the public key. Choose the matching private .knowzkey file to decrypt.'
export const KEY_VAULT_MISMATCH_MESSAGE =
  'This private key belongs to a different vault than this encrypted message.'
export const KEY_FINGERPRINT_MISMATCH_MESSAGE =
  'This private key does not match this encrypted message.'
export const CIPHERTEXT_ONLY_MESSAGE =
  'This looks like ciphertext only. From the encrypted item’s More menu, export the encrypted message (.knowzmsg), then upload that file here.'

export class SafeDecryptError extends Error {
  readonly kind: DecryptErrorKind
  constructor(kind: DecryptErrorKind, message: string) {
    super(message)
    this.name = 'SafeDecryptError'
    this.kind = kind
  }
  static generic(message: string = GENERIC_DECRYPT_ERROR): SafeDecryptError {
    return new SafeDecryptError('generic', message)
  }
  static passphraseRequired(): SafeDecryptError {
    return new SafeDecryptError('passphrase-required', PASSPHRASE_REQUIRED_MESSAGE)
  }
  static passphrase(message: string = PASSPHRASE_WRONG_MESSAGE): SafeDecryptError {
    return new SafeDecryptError('passphrase', message)
  }
}

// ============================================================================
// Result
// ============================================================================

export interface DecryptResult {
  /** The recovered plaintext (the user's content — intentionally displayed). */
  plaintext: string
  /** SHA-256 hex fingerprint derived from the supplied private key (anti-phishing, R10). */
  fingerprint: string
  /** The vaultId the message declares it was encrypted for. */
  vaultId: string
  /** True when the content layer was AAD-bound (v2); false for legacy v1 exports. */
  contentAadBound: boolean
}

export interface DecryptInputs {
  /** Raw pasted/uploaded portable-message JSON text. */
  messageText: string
  /** Raw pasted/uploaded key text: `.knowzkey` JSON, raw Base64 key, or a mnemonic. */
  keyText: string
  /** Passphrase for a v2 (passphrase-wrapped) key file; ignored otherwise. */
  passphrase?: string
}

// ============================================================================
// Key resolution
// ============================================================================

const X25519_KEY_BYTES = 32

/** A candidate mnemonic: multiple whitespace-separated tokens (BIP-39 checksum verified separately). */
function looksLikeMnemonic(text: string): boolean {
  const tokens = text.trim().split(/\s+/)
  return tokens.length >= 12 && tokens.every((t) => /^[a-zA-Z]+$/.test(t))
}

function normalizeMnemonic(text: string): string {
  return text.trim().replace(/\s+/g, ' ').toLowerCase()
}

function looksLikeCiphertextOnly(text: string): boolean {
  return text.length >= 24
    && text.length % 4 === 0
    && /^[A-Za-z0-9+/]+={0,2}$/.test(text)
}

/** Decode a Base64 private key to exactly 32 bytes, else throw a safe error. */
function decodeRawKey(base64: string): Uint8Array {
  let bytes: Uint8Array
  try {
    bytes = base64ToBytes(base64.trim())
  } catch {
    throw SafeDecryptError.generic(INVALID_KEY_MESSAGE)
  }
  if (bytes.length !== X25519_KEY_BYTES) {
    throw SafeDecryptError.generic(INVALID_KEY_MESSAGE)
  }
  return bytes
}

/**
 * Non-throwing key inspection for the UI: does this key input require a passphrase
 * (i.e. is it a v2 passphrase-wrapped `.knowzkey`)?
 */
export function inspectKeyInput(keyText: string): { requiresPassphrase: boolean } {
  const trimmed = keyText.trim()
  if (!trimmed || looksLikeMnemonic(trimmed)) return { requiresPassphrase: false }
  try {
    const parsed = parseKeyFileContent(trimmed)
    return { requiresPassphrase: parsed.kind === 'v2' }
  } catch {
    return { requiresPassphrase: false }
  }
}

/**
 * Resolve a 32-byte X25519 private key from the supplied key text + optional passphrase.
 * Order: BIP-39 mnemonic -> key file (v2 unwrap / v1 / raw Base64).
 */
async function resolvePrivateKey(
  keyText: string,
  passphrase: string | undefined,
  messageIdentity: { vaultId: string; fingerprint: string },
): Promise<Uint8Array> {
  const trimmed = keyText.trim()
  if (!trimmed) throw SafeDecryptError.generic(MISSING_KEY_MESSAGE)

  // 1. BIP-39 mnemonic (checksum-validated).
  if (looksLikeMnemonic(trimmed)) {
    const normalized = normalizeMnemonic(trimmed)
    if (validateMnemonicPhrase(normalized)) {
      return deriveKeyPairFromMnemonic(normalized).privateKey
    }
    // Looked like a phrase but failed the checksum — fail closed (do not try Base64).
    throw SafeDecryptError.generic(INVALID_KEY_MESSAGE)
  }

  // 2. Key file: v2 (passphrase-wrapped) / v1 (plaintext) / legacy raw Base64.
  const parsed = parseKeyFileContent(trimmed)
  if (parsed.keyType === 'x25519-public') {
    throw SafeDecryptError.generic(PUBLIC_KEY_MESSAGE)
  }
  if (parsed.vaultId
      && parsed.vaultId.toLowerCase() !== messageIdentity.vaultId.toLowerCase()) {
    throw SafeDecryptError.generic(KEY_VAULT_MISMATCH_MESSAGE)
  }
  if (parsed.fingerprint
      && parsed.fingerprint.toLowerCase() !== messageIdentity.fingerprint.toLowerCase()) {
    throw SafeDecryptError.generic(KEY_FINGERPRINT_MISMATCH_MESSAGE)
  }
  if (parsed.kind === 'v2') {
    if (!parsed.file) throw SafeDecryptError.generic(INVALID_KEY_MESSAGE)
    if (!passphrase) throw SafeDecryptError.passphraseRequired()
    try {
      return await unwrapKeyFile(parsed.file, passphrase)
    } catch {
      // Wrong passphrase, AAD/header tamper, or corrupt ciphertext — all fail closed.
      throw SafeDecryptError.passphrase()
    }
  }

  // v1 or raw → Base64-decoded private key.
  return decodeRawKey(parsed.key)
}

// ============================================================================
// Orchestration
// ============================================================================

/**
 * Run the full offline decrypt. Never throws anything other than {@link SafeDecryptError};
 * key material is zeroed in the `finally` regardless of success or failure (R7).
 */
export async function runDecrypt(inputs: DecryptInputs): Promise<DecryptResult> {
  const messageText = inputs.messageText ?? ''
  const keyText = inputs.keyText ?? ''

  if (!messageText.trim()) throw SafeDecryptError.generic(MISSING_MESSAGE_MESSAGE)
  if (looksLikeCiphertextOnly(messageText.trim())) {
    throw SafeDecryptError.generic(CIPHERTEXT_ONLY_MESSAGE)
  }

  let privateKey: Uint8Array | undefined
  try {
    // Size caps BEFORE any parse/crypto (spec R9). Inside the try so an oversize
    // input maps to a curated SafeDecryptError rather than leaking InputGuardError.
    assertKeyWithinSizeCap(keyText)

    // Strict, guarded parse of the (hostile) message input.
    const message = guardedParsePortableMessage(messageText)

    // Resolve the key (may prompt for a passphrase via a typed error).
    privateKey = await resolvePrivateKey(keyText, inputs.passphrase, {
      vaultId: message.binding.vaultId,
      fingerprint: message.fingerprint,
    })

    // Anti-phishing provenance: fingerprint of the derived PUBLIC key (R10).
    const publicKey = x25519.getPublicKey(privateKey)
    const fingerprint = computeFingerprint(publicKey)
    if (fingerprint.toLowerCase() !== message.fingerprint.toLowerCase()) {
      throw SafeDecryptError.generic(KEY_FINGERPRINT_MISMATCH_MESSAGE)
    }

    // Deterministic decrypt (contentAadBound drives AAD). A tampered binding fails the GCM tag.
    const plaintext = await decryptPortableMessage(privateKey, message)

    return {
      plaintext,
      fingerprint,
      vaultId: message.binding.vaultId,
      contentAadBound: message.contentAadBound,
    }
  } catch (err) {
    // Curated errors pass through; everything else collapses to the generic fail-closed message.
    if (err instanceof SafeDecryptError) throw err
    throw SafeDecryptError.generic()
  } finally {
    // R7: zero the private key material whether we succeeded or failed.
    if (privateKey) privateKey.fill(0)
  }
}
