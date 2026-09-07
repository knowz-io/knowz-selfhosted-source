import { describe, expect, it } from 'vitest'
import fixture from './fixtures/golden-encrypted-note-v2.json?raw'
import { runDecrypt } from '../lib/decrypt-flow'

const FIXED_PRIVATE_KEY_BASE64 =
  'YGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn8='
const FIXED_SNAPSHOT =
  '{"format":"knowz-note-snapshot-v1","title":"A future note","content":"Meet me beneath the old oak tree.","sealedAt":"2026-07-19T00:00:00Z"}'

describe('VERIFY-M5 local decrypt golden vector', () => {
  it('decrypts the portal golden note in memory and does not use localStorage', async () => {
    const result = await runDecrypt({
      messageText: fixture,
      keyText: FIXED_PRIVATE_KEY_BASE64,
    })
    expect(result.plaintext).toBe(FIXED_SNAPSHOT)
    expect(window.localStorage.length).toBe(0)
  })
})
