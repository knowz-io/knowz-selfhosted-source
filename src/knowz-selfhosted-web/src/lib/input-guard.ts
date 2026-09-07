/**
 * Untrusted-input hardening for the offline decrypt portal (spec R9, fail-closed).
 *
 * Both the portable-message parse and the key-file parse are HOSTILE-input
 * surfaces. This module enforces, BEFORE any crypto runs:
 *  - a hard size cap (portable message <= 5 MB, key file <= 64 KB),
 *  - a prototype-pollution guard (reject `__proto__` / `constructor` / `prototype`
 *    keys anywhere in the parsed JSON, via a throwing reviver),
 *  - strict schema validation (delegated to the vendored `parsePortableMessage`).
 *
 * Nothing here echoes the raw input, key material, or a stack trace — callers map
 * any throw to a single generic fail-closed message.
 *
 * WorkGroupID: kc-feat-vault-encryption-e2e-20260703-135844 (N5 FEAT_StandaloneDecryptPortal)
 */

import { parsePortableMessage, type PortableEncryptedMessage } from './vendored/portable-message'

// ============================================================================
// Size caps (spec R9)
// ============================================================================

/** Portable message hard cap: 5 MiB. */
export const MAX_MESSAGE_BYTES = 5 * 1024 * 1024
/** Key file / mnemonic hard cap: 64 KiB. */
export const MAX_KEY_BYTES = 64 * 1024

/** Forbidden object keys that could be used for prototype-pollution. */
export const FORBIDDEN_KEYS = ['__proto__', 'constructor', 'prototype'] as const

/** Thrown by the guard for oversize / forbidden-key / malformed input. Message is safe (no input echo). */
export class InputGuardError extends Error {
  constructor(message: string) {
    super(message)
    this.name = 'InputGuardError'
  }
}

/**
 * Reject an input whose UTF-8 byte length exceeds `maxBytes`, BEFORE parsing it.
 * Uses the encoded byte length (not `.length`, which counts UTF-16 code units).
 */
export function assertWithinSizeCap(text: string, maxBytes: number, label: string): void {
  // Fast pre-check on code-unit length (>= byte length is impossible to exceed
  // maxBytes if 4*length would still be under... so only measure bytes when close).
  const byteLength = new TextEncoder().encode(text).length
  if (byteLength > maxBytes) {
    throw new InputGuardError(`The ${label} is too large (max ${Math.floor(maxBytes / 1024)} KB).`)
  }
}

/**
 * `JSON.parse` with a reviver that THROWS on any prototype-pollution key. Returns
 * the parsed value on success. Throws {@link InputGuardError} on invalid JSON or a
 * forbidden key — never leaks the offending input.
 */
export function parseJsonGuarded(text: string): unknown {
  try {
    return JSON.parse(text, (key, value) => {
      if (key === '__proto__' || key === 'constructor' || key === 'prototype') {
        throw new InputGuardError('The input contains a forbidden property name.')
      }
      return value
    })
  } catch (err) {
    if (err instanceof InputGuardError) throw err
    throw new InputGuardError('The input is not valid JSON.')
  }
}

/**
 * Guarded strict parse of a portable "Knowz Encrypted Message":
 *   size cap -> prototype-pollution guard -> strict schema (vendored parser).
 *
 * The prototype-pollution guard runs the throwing reviver over the SAME text the
 * strict parser will read, so a forbidden key is rejected before schema checks.
 */
export function guardedParsePortableMessage(text: string): PortableEncryptedMessage {
  assertWithinSizeCap(text, MAX_MESSAGE_BYTES, 'encrypted message')
  // Reject prototype-pollution keys (and non-JSON) up front.
  parseJsonGuarded(text)
  // Strict schema validation (format/version/binding/envelope). Throws on any violation.
  return parsePortableMessage(text)
}

/** Guard a key file / mnemonic input by size only (schema handled by the key resolver). */
export function assertKeyWithinSizeCap(text: string): void {
  assertWithinSizeCap(text, MAX_KEY_BYTES, 'key file')
}
