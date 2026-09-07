// VENDORED — byte-identical copy of src/knowz-web-client/src/lib/vault-crypto.ts; do not edit here. Sync via scripts/check-vendored-crypto.

/**
 * Client-side vault encryption utilities using BIP-39 mnemonic key recovery.
 *
 * Key derivation chain:
 *   256-bit entropy (CSPRNG) → 24 BIP-39 words
 *   → PBKDF2-HMAC-SHA512(2048 rounds, salt="mnemonic"+passphrase) → 512-bit seed
 *   → HKDF-SHA256(info="knowz-vault-encryption-v1") → 32-byte master
 *   → X25519 key pair (encryption) + Ed25519 key pair (signing)
 *
 * Envelope decryption (mirrors VaultCryptoService.cs server-side):
 *   1. shared_secret = X25519(vault_private_key, ephemeral_public_key)
 *   2. wrapping_key = HKDF-SHA256(ikm=shared_secret, salt=ephemeral_pub, info="knowz-dek-wrap-v1")
 *   3. DEK = AES-256-GCM-Decrypt(wrapping_key, dek_nonce, wrapped_dek)
 *   4. plaintext = AES-256-GCM-Decrypt(DEK, content_nonce, ciphertext)
 *
 * WorkGroupID: kc-feat-mnemonic-key-recovery-20260316-120000
 */

import { generateMnemonic as bip39Generate, validateMnemonic as bip39Validate, mnemonicToSeedSync } from '@scure/bip39'
import { wordlist } from '@scure/bip39/wordlists/english.js'
import { x25519 } from '@noble/curves/ed25519.js'
import { ed25519 } from '@noble/curves/ed25519.js'
import { hkdf } from '@noble/hashes/hkdf.js'
import { sha256 } from '@noble/hashes/sha2.js'

// ============================================================================
// Constants (must match server-side VaultCryptoService.cs)
// ============================================================================

const HKDF_MASTER_INFO = new TextEncoder().encode('knowz-vault-encryption-v1')
const HKDF_DEK_WRAP_INFO = new TextEncoder().encode('knowz-dek-wrap-v1')
const AES_KEY_BYTES = 32
const NONCE_BYTES = 12
const TAG_BYTES = 16

// ============================================================================
// Types
// ============================================================================

export interface VaultKeyPair {
  /** X25519 private key (32 bytes) — NEVER send to server */
  privateKey: Uint8Array
  /** X25519 public key (32 bytes) — sent to server during setup */
  publicKey: Uint8Array
  /** Ed25519 signing private key (32 bytes) — NEVER send to server */
  signingPrivateKey: Uint8Array
  /** Ed25519 signing public key (32 bytes) — sent to server for ownership verification */
  signingPublicKey: Uint8Array
}

export interface EncryptedEnvelope {
  /** Base64-encoded AES-256-GCM encrypted content (ciphertext || auth_tag) */
  encryptedContent?: string
  /** Base64-encoded AES-256-GCM encrypted content used by field envelopes */
  ciphertext?: string
  /** Raw AES-256-GCM encrypted content bytes used by encrypted attachment blobs */
  ciphertextBytes?: Uint8Array
  /** Base64-encoded 12-byte nonce for content decryption */
  contentNonce: string
  /** Base64-encoded wrapped DEK (encrypted_dek || auth_tag = 48 bytes) */
  encryptedDek: string
  /** Base64-encoded 12-byte nonce for DEK unwrapping */
  dekNonce: string
  /** Base64-encoded ephemeral X25519 public key (32 bytes) */
  ephemeralPublicKey: string
}

export interface EncryptedFieldSet {
  schema?: string | null
  fields?: Record<string, EncryptedEnvelope | null> | null
}

// ============================================================================
// Mnemonic generation and validation
// ============================================================================

/**
 * Generate a BIP-39 mnemonic phrase.
 * @param wordCount 12 (128-bit) or 24 (256-bit) words
 */
export function generateMnemonicPhrase(wordCount: 12 | 24 = 24): string {
  const strength = wordCount === 12 ? 128 : 256
  return bip39Generate(wordlist, strength)
}

/**
 * Validate a BIP-39 mnemonic phrase (checksum verification).
 */
export function validateMnemonicPhrase(mnemonic: string): boolean {
  return bip39Validate(mnemonic, wordlist)
}

// ============================================================================
// Key derivation
// ============================================================================

/**
 * Derive a 512-bit seed from a mnemonic phrase.
 * Uses PBKDF2-HMAC-SHA512 with 2048 rounds and salt="mnemonic"+passphrase.
 * This matches the BIP-39 standard used by @scure/bip39.
 */
export function mnemonicToSeed(mnemonic: string, passphrase?: string): Uint8Array {
  return mnemonicToSeedSync(mnemonic, passphrase || '')
}

/**
 * Derive X25519 + Ed25519 key pairs from a BIP-39 seed.
 *
 * Chain: seed → HKDF-SHA256(info="knowz-vault-encryption-v1") → 32-byte master
 *        master → X25519 key pair (for encryption)
 *        master → Ed25519 key pair (for signing/ownership proof)
 */
export function deriveKeyPair(seed: Uint8Array): VaultKeyPair {
  // HKDF-SHA256: extract+expand from 512-bit seed to 32-byte master key
  const masterKey = hkdf(sha256, seed, undefined, HKDF_MASTER_INFO, AES_KEY_BYTES)

  // X25519 key pair from master key (clamping is handled internally by @noble/curves)
  const x25519PrivateKey = new Uint8Array(masterKey)
  const x25519PublicKey = x25519.getPublicKey(x25519PrivateKey)

  // Ed25519 key pair from master key
  const ed25519PrivateKey = new Uint8Array(masterKey)
  const ed25519PublicKey = ed25519.getPublicKey(ed25519PrivateKey)

  return {
    privateKey: x25519PrivateKey,
    publicKey: x25519PublicKey,
    signingPrivateKey: ed25519PrivateKey,
    signingPublicKey: ed25519PublicKey,
  }
}

/**
 * Full derivation: mnemonic → seed → key pairs.
 */
export function deriveKeyPairFromMnemonic(mnemonic: string, passphrase?: string): VaultKeyPair {
  const seed = mnemonicToSeed(mnemonic, passphrase)
  return deriveKeyPair(seed)
}

// ============================================================================
// Fingerprint
// ============================================================================

/**
 * Compute SHA-256 fingerprint of a public key (lowercase hex).
 * Must match server-side VaultCryptoService.ComputeKeyFingerprint.
 */
export function computeFingerprint(publicKey: Uint8Array): string {
  const hash = sha256(publicKey)
  return Array.from(hash).map(b => b.toString(16).padStart(2, '0')).join('')
}

// ============================================================================
// Ed25519 signing (for ownership verification)
// ============================================================================

/**
 * Sign a challenge with Ed25519 private key for ownership proof.
 * The challenge and signature are Base64-encoded (matching server-side VerifySignature).
 */
export function signChallenge(signingPrivateKey: Uint8Array, challengeBase64: string): string {
  const challengeBytes = base64ToBytes(challengeBase64)
  const signature = ed25519.sign(challengeBytes, signingPrivateKey)
  return bytesToBase64(signature)
}

// ============================================================================
// Envelope decryption (client-side only)
// ============================================================================

/**
 * Decrypt an encrypted envelope using the vault's X25519 private key.
 *
 * Steps (mirrors VaultCryptoService.EncryptEnvelope in reverse):
 * 1. X25519 ECDH: shared_secret = ECDH(vault_private_key, ephemeral_public_key)
 * 2. HKDF: wrapping_key = HKDF-SHA256(ikm=shared_secret, salt=ephemeral_pub, info="knowz-dek-wrap-v1")
 * 3. Unwrap DEK: DEK = AES-256-GCM-Decrypt(wrapping_key, dek_nonce, wrapped_dek)
 * 4. Decrypt content: plaintext = AES-256-GCM-Decrypt(DEK, content_nonce, ciphertext)
 */
export async function decryptEnvelope(
  privateKey: Uint8Array,
  envelope: EncryptedEnvelope,
  aad?: Uint8Array
): Promise<string> {
  const plaintext = await decryptEnvelopeToBytes(privateKey, envelope, aad)
  return new TextDecoder().decode(plaintext)
}

/**
 * @param aad Optional associated data bound to the AES-256-GCM CONTENT layer only (v2 envelopes,
 *   see portable-message.ts buildContentAad). The DEK-wrap layer is never AAD-bound. When omitted,
 *   decryption is byte-identical to the legacy v1 (no-AAD) path so stored v1 envelopes still decrypt.
 */
export async function decryptEnvelopeToBytes(
  privateKey: Uint8Array,
  envelope: EncryptedEnvelope,
  aad?: Uint8Array
): Promise<Uint8Array> {
  // Decode all Base64 values
  const ephemeralPub = base64ToBytes(envelope.ephemeralPublicKey)
  const wrappedDekWithTag = base64ToBytes(envelope.encryptedDek)
  const dekNonce = base64ToBytes(envelope.dekNonce)
  const encryptedPayload = envelope.ciphertextBytes
    ?? base64ToBytes(envelope.encryptedContent ?? envelope.ciphertext ?? '')
  const contentNonce = base64ToBytes(envelope.contentNonce)

  // Step 1: X25519 ECDH → shared secret
  const sharedSecret = x25519.getSharedSecret(privateKey, ephemeralPub)

  // Step 2: HKDF-SHA256 → wrapping key
  // salt = ephemeral public key, info = "knowz-dek-wrap-v1" (matches server DeriveWrappingKey)
  const wrappingKey = hkdf(
    sha256,
    sharedSecret,
    ephemeralPub,  // salt
    HKDF_DEK_WRAP_INFO,
    AES_KEY_BYTES
  )

  // Step 3: Unwrap DEK using AES-256-GCM (DEK-wrap layer is NEVER AAD-bound — spec R4)
  // wrapped_dek format: encrypted_dek(32 bytes) || auth_tag(16 bytes) = 48 bytes total
  const dek = await aesGcmDecrypt(wrappingKey, dekNonce, wrappedDekWithTag)

  // Step 4: Decrypt content using AES-256-GCM, binding the optional content-layer AAD (v2)
  // ciphertext format: encrypted_content || auth_tag(16 bytes)
  return aesGcmDecrypt(dek, contentNonce, encryptedPayload, aad)
}

export async function decryptEncryptedFieldSet(
  privateKey: Uint8Array,
  fieldSet?: EncryptedFieldSet | null
): Promise<Record<string, string>> {
  const fields = fieldSet?.fields
  if (!fields) return {}

  const entries = await Promise.all(
    Object.entries(fields).map(async ([name, envelope]) => {
      if (!envelope) return null
      const value = await decryptEnvelope(privateKey, envelope)
      return [name, value] as const
    })
  )

  return entries.reduce<Record<string, string>>((acc, entry) => {
    if (entry) acc[entry[0]] = entry[1]
    return acc
  }, {})
}

// ============================================================================
// WebCrypto AES-256-GCM helpers
// ============================================================================

/**
 * AES-256-GCM decrypt using WebCrypto API.
 * Input format: ciphertext || auth_tag (16 bytes appended).
 * WebCrypto expects the tag appended to the ciphertext, which is our format.
 */
async function aesGcmDecrypt(
  keyBytes: Uint8Array,
  nonce: Uint8Array,
  ciphertextWithTag: Uint8Array,
  additionalData?: Uint8Array
): Promise<Uint8Array> {
  const key = await crypto.subtle.importKey(
    'raw',
    new Uint8Array(keyBytes),
    { name: 'AES-GCM' },
    false,
    ['decrypt']
  )

  const params: AesGcmParams = {
    name: 'AES-GCM',
    iv: new Uint8Array(nonce),
    tagLength: TAG_BYTES * 8, // bits
  }
  // Empty AAD is equivalent to no AAD in GCM; only attach when provided to keep v1 byte-identical.
  if (additionalData && additionalData.length > 0) {
    params.additionalData = new Uint8Array(additionalData)
  }

  // WebCrypto AES-GCM expects ciphertext || tag (which is our format)
  const plaintext = await crypto.subtle.decrypt(params, key, new Uint8Array(ciphertextWithTag))

  return new Uint8Array(plaintext)
}

// ============================================================================
// Base64 helpers
// ============================================================================

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
