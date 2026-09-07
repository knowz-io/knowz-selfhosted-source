import { describe, expect, it } from 'vitest'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

// Regression guard for the self-hosted web client's CSP. Self-hosted sets the
// CSP via a <meta http-equiv="Content-Security-Policy"> tag in index.html
// rather than an SWA HTTP header. Same blob: requirement as the SaaS web
// client because AttachmentViewer + 5 other call sites use createObjectURL.

function parseCsp(csp: string): Record<string, string[]> {
  const directives: Record<string, string[]> = {}
  for (const part of csp.split(';')) {
    const trimmed = part.trim()
    if (!trimmed) continue
    const [name, ...values] = trimmed.split(/\s+/)
    directives[name.toLowerCase()] = values
  }
  return directives
}

describe('knowz-selfhosted-web meta CSP', () => {
  const indexPath = resolve(__dirname, '../../index.html')
  const html = readFileSync(indexPath, 'utf8')
  // The meta tag uses double-quoted attributes and the CSP value contains
  // single-quoted tokens like 'self' — only stop the capture at the closing
  // double quote, not at any quote.
  const match = html.match(
    /<meta\s+http-equiv="Content-Security-Policy"\s+content="([^"]+)"/i,
  )
  const csp = match?.[1] ?? ''
  const directives = parseCsp(csp)

  it('defines a Content-Security-Policy meta tag', () => {
    expect(csp).not.toBe('')
  })

  it('enforces frame-ancestors via the production HTTP header instead of an ignored meta directive', () => {
    expect(directives['frame-ancestors']).toBeUndefined()
    const dockerfile = readFileSync(resolve(__dirname, '../../Dockerfile'), 'utf8')
    expect(dockerfile).toContain('add_header Content-Security-Policy "frame-ancestors \'none\'" always;')
  })

  it("img-src allows 'self', data:, and blob:", () => {
    const imgSrc = directives['img-src'] ?? []
    expect(imgSrc).toContain("'self'")
    expect(imgSrc).toContain('data:')
    expect(imgSrc).toContain('blob:')
  })

  it("media-src allows 'self' and blob:", () => {
    const mediaSrc = directives['media-src'] ?? []
    expect(mediaSrc).toContain("'self'")
    expect(mediaSrc).toContain('blob:')
  })

  it("object-src allows 'self' and blob:", () => {
    const objectSrc = directives['object-src'] ?? []
    expect(objectSrc).toContain("'self'")
    expect(objectSrc).toContain('blob:')
  })
})
