import { describe, expect, it } from 'vitest'
import { looksLikeEncryptedEnvelope } from '../lib/encrypted-envelope'

describe('looksLikeEncryptedEnvelope (VERIFY-M4)', () => {
  it('does not flag ordinary markdown', () => {
    expect(looksLikeEncryptedEnvelope('# Hello\n\nThis is a note.')).toBe(false)
    expect(looksLikeEncryptedEnvelope('')).toBe(false)
    expect(looksLikeEncryptedEnvelope(null)).toBe(false)
  })

  it('flags knowz-encrypted-message-v2 envelopes', () => {
    expect(looksLikeEncryptedEnvelope('knowz-encrypted-message-v2:abc')).toBe(true)
    expect(
      looksLikeEncryptedEnvelope(
        '{"typ":"knowz-encrypted-message-v2","alg":"A256GCM","ciphertext":"abc"}',
      ),
    ).toBe(true)
  })
})
