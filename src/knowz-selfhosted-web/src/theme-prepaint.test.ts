/**
 * First-paint theme guard: index.html pre-paints the --background tokens and applies the
 * stored theme class before the bundle arrives. Fails if either side drifts.
 */
import { describe, it, expect } from 'vitest'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

const root = resolve(__dirname, '..')
const indexHtml = readFileSync(resolve(root, 'index.html'), 'utf8')
const indexCss = readFileSync(resolve(root, 'src/index.css'), 'utf8')
const prepaintJs = readFileSync(resolve(root, 'public/theme-prepaint.js'), 'utf8')

function tokenBackground(selector: ':root' | '.dark'): string {
  const start = indexCss.indexOf(`${selector} {`)
  expect(start).toBeGreaterThan(-1)
  const block = indexCss.slice(start, indexCss.indexOf('}', start))
  const match = block.match(/--background:\s*([^;]+);/)
  expect(match, `--background missing under ${selector}`).not.toBeNull()
  return match![1].trim()
}

function prepaintRule(selector: string): string {
  const style = indexHtml.match(/<style id="theme-prepaint">([\s\S]*?)<\/style>/)
  expect(style).not.toBeNull()
  const escapedSelector = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
  const rule = style![1].match(new RegExp(`${escapedSelector}\\s*\\{([^}]*)\\}`))
  expect(rule, `no pre-paint rule for ${selector}`).not.toBeNull()
  return rule![1]
}

describe('self-hosted first-paint theme', () => {
  it('Should_ActivateTheContentSecurityPolicyBeforeLoadingScripts', () => {
    const policy = indexHtml.indexOf('<meta http-equiv="Content-Security-Policy"')
    expect(policy).toBeGreaterThan(-1)
    expect(policy).toBeLessThan(indexHtml.indexOf('<script'))
  })

  it('Should_LoadThePrepaintScriptBeforeTheModuleEntry_AsAPlainScript', () => {
    const script = indexHtml.indexOf('<script src="/theme-prepaint.js"></script>')
    expect(script).toBeGreaterThan(-1)
    expect(script).toBeLessThan(indexHtml.indexOf('type="module"'))
    expect(indexHtml).not.toMatch(/theme-prepaint\.js"\s+(defer|async|type=)/)
    // The CSP in index.html is `script-src 'self' https:` — no inline, no module needed.
    expect(prepaintJs).not.toMatch(/\b(import|export)\b/)
  })

  it('Should_MirrorTheLightAndDarkBackgroundTokens', () => {
    expect(prepaintRule('html')).toContain(`hsl(${tokenBackground(':root')})`)
    expect(prepaintRule('html.dark')).toContain(`hsl(${tokenBackground('.dark')})`)
  })

  it('Should_DefaultToLightLikeLibTheme_AndOnlyGoDarkWhenStored', () => {
    // src/lib/theme.ts: dark only when 'dark' is stored, otherwise light.
    expect(prepaintJs).toContain("localStorage.getItem('theme')")
    expect(prepaintJs).toContain("theme === 'dark'")
  })
})
