import { describe, it, expect, beforeEach, vi } from 'vitest'

describe('VERIFY-L2 light theme default', () => {
  beforeEach(() => {
    localStorage.clear()
    document.documentElement.classList.remove('dark')
    vi.resetModules()
  })

  it('Should_DefaultToLightMode_WhenNoLocalStorageSet', async () => {
    expect(localStorage.getItem('theme')).toBeNull()

    const { initTheme } = await import('../lib/theme')
    initTheme()

    expect(document.documentElement.classList.contains('dark')).toBe(false)
  })

  it('Should_RespectLightMode_WhenExplicitlySetInLocalStorage', async () => {
    localStorage.setItem('theme', 'light')

    const { initTheme } = await import('../lib/theme')
    initTheme()

    expect(document.documentElement.classList.contains('dark')).toBe(false)
  })

  it('Should_RespectDarkMode_WhenExplicitlySetInLocalStorage', async () => {
    localStorage.setItem('theme', 'dark')

    const { initTheme } = await import('../lib/theme')
    initTheme()

    expect(document.documentElement.classList.contains('dark')).toBe(true)
  })
})
