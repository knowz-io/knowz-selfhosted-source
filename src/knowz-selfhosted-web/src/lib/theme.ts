import { useState, useEffect } from 'react'

type Theme = 'light' | 'dark'

function getStorage(): Storage | null {
  try {
    const storage = globalThis.localStorage
    if (
      storage &&
      typeof storage.getItem === 'function' &&
      typeof storage.setItem === 'function'
    ) {
      return storage
    }
  } catch {
    // Ignore storage access errors and fall back to defaults.
  }
  return null
}

function getStoredTheme(): Theme {
  const stored = getStorage()?.getItem('theme')
  if (stored === 'dark' || stored === 'light') return stored
  return 'light'
}

function applyTheme(theme: Theme) {
  document.documentElement.classList.toggle('dark', theme === 'dark')
}

export function initTheme() {
  applyTheme(getStoredTheme())
}

export function useTheme() {
  const [theme, setTheme] = useState<Theme>(getStoredTheme)

  useEffect(() => {
    applyTheme(theme)
    getStorage()?.setItem('theme', theme)
  }, [theme])

  const toggle = () => setTheme((t) => (t === 'dark' ? 'light' : 'dark'))

  return { theme, toggle }
}
