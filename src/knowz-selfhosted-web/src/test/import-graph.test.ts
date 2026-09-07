import { describe, expect, it } from 'vitest'
import { readdirSync, readFileSync, statSync } from 'node:fs'
import { extname, join, relative, resolve } from 'node:path'

const SRC_ROOT = resolve(__dirname, '..')
const SOURCE_EXTENSIONS = new Set(['.ts', '.tsx', '.js', '.jsx', '.css'])

function walk(dir: string, files: string[] = []): string[] {
  for (const entry of readdirSync(dir)) {
    if (entry === 'test') continue
    const full = join(dir, entry)
    const stat = statSync(full)
    if (stat.isDirectory()) {
      walk(full, files)
      continue
    }
    if (SOURCE_EXTENSIONS.has(extname(entry))) files.push(full)
  }
  return files
}

describe('VERIFY-L7 import graph', () => {
  it('Should_NotImportKnowzWebClient_FromSelfhostedSource', () => {
    const importPattern =
      /(?:from|import)\s+['"][^'"]*knowz-web-client[^'"]*['"]|require\(\s*['"][^'"]*knowz-web-client[^'"]*['"]\s*\)|@import\s+['"][^'"]*knowz-web-client/
    const hits: string[] = []
    for (const file of walk(SRC_ROOT)) {
      const contents = readFileSync(file, 'utf8')
      if (importPattern.test(contents)) {
        hits.push(relative(SRC_ROOT, file))
      }
    }
    expect(hits).toEqual([])
  })
})
