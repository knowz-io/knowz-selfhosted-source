import { describe, expect, it } from 'vitest'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

function readCss(relativeFromSrc: string): string {
  return readFileSync(resolve(__dirname, '..', relativeFromSrc), 'utf8')
}

function rootBlock(css: string): string {
  const match = css.match(/:root\s*\{[\s\S]*?\n\s*\}/)
  return match?.[0] ?? ''
}

function cssVarsFromRoot(css: string): Record<string, string> {
  const vars: Record<string, string> = {}
  const re = /--([a-z0-9-]+)\s*:\s*([^;]+);/gi
  let match: RegExpExecArray | null
  while ((match = re.exec(rootBlock(css))) !== null) {
    vars[`--${match[1]}`] = match[2].trim()
  }
  return vars
}

describe('VERIFY-L1 design tokens', () => {
  it('Should_MatchPlatformPrimaryBackgroundAndRadiusLg', () => {
    const indexCss = readCss('index.css')
    const tokensCss = readCss('styles/design-tokens.css')
    const vars = {
      ...cssVarsFromRoot(tokensCss),
      ...cssVarsFromRoot(indexCss),
    }

    expect(vars['--primary']).toBe('221.2 83.2% 53.3%')
    expect(vars['--background']).toBe('210 24% 98%')
    expect(vars['--radius-lg']).toBe('0.75rem')
  })

  it('Should_ExposeTypeRoles_MetaLabelBody', () => {
    const tokensCss = readCss('styles/design-tokens.css')
    expect(tokensCss).toMatch(/--fs-meta:\s*0\.75rem/)
    expect(tokensCss).toMatch(/--fs-label:\s*0\.8125rem/)
    expect(tokensCss).toMatch(/--fs-body:\s*0\.875rem/)
    expect(tokensCss).toContain('.t-meta')
    expect(tokensCss).toContain('.t-label')
    expect(tokensCss).toContain('.t-body')
  })

  it('Should_ImportDesignTokens_FromIndexCss', () => {
    const indexCss = readCss('index.css')
    expect(indexCss).toMatch(/@import\s+['"]\.\/styles\/design-tokens\.css['"]/)
  })

  it('Should_DropGlassRounded28AndBackdropBlurFromSurfaces', () => {
    const indexCss = readCss('index.css')
    expect(indexCss).not.toContain('rounded-[28px]')
    expect(indexCss).not.toMatch(/\.sh-surface[\s\S]{0,240}backdrop-blur-xl/)
    expect(indexCss).not.toMatch(/\.sh-toolbar[\s\S]{0,240}backdrop-blur-xl/)
  })

  it('Should_MapRadiusLg_InTailwindConfig', () => {
    const tailwind = readFileSync(
      resolve(__dirname, '../../tailwind.config.js'),
      'utf8',
    )
    expect(tailwind).toContain('--radius-lg')
    expect(tailwind).toContain("fontFamily")
    expect(tailwind).toMatch(/Pacifico/)
  })
})
