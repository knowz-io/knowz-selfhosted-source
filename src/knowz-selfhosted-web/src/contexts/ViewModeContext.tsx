import { createContext, useContext, useState, useEffect, useCallback, type ReactNode } from 'react'

export type ViewMode = 'grid' | 'compact' | 'list' | 'gallery' | 'code'

interface ViewModeContextValue {
  viewMode: ViewMode
  setViewMode: (mode: ViewMode) => void
}

interface InternalContextValue {
  viewModes: Record<string, ViewMode>
  setViewModeForKey: (pageKey: string, mode: ViewMode) => void
}

const ViewModeContext = createContext<InternalContextValue | undefined>(undefined)

const STORAGE_KEY_PREFIX = 'knowz-sh-view-mode:'
const DEFAULT_VIEW_MODE: ViewMode = 'grid'

function isValidViewMode(value: unknown): value is ViewMode {
  return (
    typeof value === 'string' &&
    ['grid', 'compact', 'list', 'gallery', 'code'].includes(value)
  )
}

function readViewModeFromStorage(pageKey: string): ViewMode {
  try {
    const stored = localStorage.getItem(`${STORAGE_KEY_PREFIX}${pageKey}`)
    if (stored && isValidViewMode(stored)) return stored
  } catch {
    // localStorage not available
  }
  return DEFAULT_VIEW_MODE
}

interface ViewModeProviderProps {
  children: ReactNode
}

export function ViewModeProvider({ children }: ViewModeProviderProps) {
  const [viewModes, setViewModes] = useState<Record<string, ViewMode>>(() => ({
    knowledge: readViewModeFromStorage('knowledge'),
  }))

  const setViewModeForKey = useCallback((pageKey: string, mode: ViewMode) => {
    setViewModes(prev => ({ ...prev, [pageKey]: mode }))
    try {
      localStorage.setItem(`${STORAGE_KEY_PREFIX}${pageKey}`, mode)
    } catch {
      // localStorage not available
    }
  }, [])

  // Cross-tab sync
  useEffect(() => {
    const handleStorageChange = (e: StorageEvent) => {
      if (e.key?.startsWith(STORAGE_KEY_PREFIX) && e.newValue && isValidViewMode(e.newValue)) {
        const pageKey = e.key.replace(STORAGE_KEY_PREFIX, '')
        setViewModes(prev => ({ ...prev, [pageKey]: e.newValue as ViewMode }))
      }
    }
    window.addEventListener('storage', handleStorageChange)
    return () => window.removeEventListener('storage', handleStorageChange)
  }, [])

  return (
    <ViewModeContext.Provider value={{ viewModes, setViewModeForKey }}>
      {children}
    </ViewModeContext.Provider>
  )
}

export function useViewMode(pageKey: string = 'knowledge'): ViewModeContextValue {
  const ctx = useContext(ViewModeContext)
  if (ctx === undefined) {
    throw new Error('useViewMode must be used within ViewModeProvider')
  }

  const viewMode = ctx.viewModes[pageKey] ?? readViewModeFromStorage(pageKey)

  const setViewMode = useCallback((mode: ViewMode) => {
    ctx.setViewModeForKey(pageKey, mode)
  }, [ctx, pageKey])

  return { viewMode, setViewMode }
}
