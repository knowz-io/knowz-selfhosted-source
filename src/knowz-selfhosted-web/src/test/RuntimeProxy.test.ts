import { describe, expect, it } from 'vitest'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

describe('self-hosted nginx runtime proxy', () => {
  it('uses Docker DNS with a variable-backed API upstream (VERIFY-K11)', () => {
    const dockerfile = readFileSync(resolve(__dirname, '../../Dockerfile'), 'utf8')
    expect(dockerfile).toContain('resolver 127.0.0.11')
    expect(dockerfile).toContain('set $api_upstream')
    expect(dockerfile).toContain('proxy_pass ${API_PROTOCOL}://$api_upstream')
    expect(dockerfile).not.toContain('proxy_pass ${API_PROTOCOL}://${API_UPSTREAM}')
  })
})
