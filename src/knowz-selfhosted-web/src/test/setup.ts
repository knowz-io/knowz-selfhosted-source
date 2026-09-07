import '@testing-library/jest-dom'
import { afterEach, beforeEach } from 'vitest'

// Mock window.matchMedia for jsdom (not available by default)
Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: (query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addListener: () => {},
    removeListener: () => {},
    addEventListener: () => {},
    removeEventListener: () => {},
    dispatchEvent: () => false,
  }),
})

function createMemoryStorage(): Storage {
  let store: Record<string, string> = {}

  return {
    getItem: (key: string) => (Object.prototype.hasOwnProperty.call(store, key) ? store[key] : null),
    setItem: (key: string, value: string) => {
      store[key] = String(value)
    },
    removeItem: (key: string) => {
      delete store[key]
    },
    clear: () => {
      store = {}
    },
    key: (index: number) => Object.keys(store)[index] ?? null,
    get length() {
      return Object.keys(store).length
    },
  }
}

function hasUsableLocalStorage() {
  try {
    const storage = globalThis.localStorage as Partial<Storage> | undefined
    return !!storage &&
      typeof storage.getItem === 'function' &&
      typeof storage.setItem === 'function' &&
      typeof storage.removeItem === 'function' &&
      typeof storage.clear === 'function' &&
      storage.getItem('__missing__') === null
  } catch {
    return false
  }
}

const fallbackLocalStorage = createMemoryStorage()

if (!hasUsableLocalStorage()) {
  Object.defineProperty(globalThis, 'localStorage', {
    configurable: true,
    writable: true,
    value: fallbackLocalStorage,
  })
}

beforeEach(() => {
  globalThis.localStorage?.clear?.()
})

afterEach(() => {
  globalThis.localStorage?.clear?.()
})
