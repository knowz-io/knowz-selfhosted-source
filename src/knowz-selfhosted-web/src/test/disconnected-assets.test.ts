import { describe, expect, it } from 'vitest'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

describe('disconnected self-hosted typography', () => {
  it('uses installed or system fonts without remote stylesheet or font downloads', () => {
    const css = readFileSync(resolve(__dirname, '../index.css'), 'utf8')
    expect(css).not.toMatch(/(?:@import\s+(?:url\()?|src:\s*url\()\s*['"]?(?:https?:)?\/\//i)
    expect(css).toContain("url('/fonts/pacifico-v23-latin.woff2')")
    expect(css).toMatch(/font-family:\s*'Inter',\s*system-ui,\s*-apple-system,\s*sans-serif/)
  })
})
