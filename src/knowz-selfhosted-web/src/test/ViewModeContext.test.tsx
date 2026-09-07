import { describe, it, expect, beforeEach } from 'vitest'
import { renderHook, act } from '@testing-library/react'
import type { ReactNode } from 'react'
import { ViewModeProvider, useViewMode } from '../contexts/ViewModeContext'

function wrapper({ children }: { children: ReactNode }) {
  return <ViewModeProvider>{children}</ViewModeProvider>
}

describe('ViewModeContext', () => {
  beforeEach(() => {
    localStorage.clear()
  })

  it('Should_DefaultToGrid_WhenLocalStorageIsEmpty', () => {
    const { result } = renderHook(() => useViewMode('knowledge'), { wrapper })
    expect(result.current.viewMode).toBe('grid')
  })

  it('Should_PersistToLocalStorage_WhenSetViewModeCalled', () => {
    const { result } = renderHook(() => useViewMode('knowledge'), { wrapper })
    act(() => result.current.setViewMode('compact'))
    expect(result.current.viewMode).toBe('compact')
    expect(localStorage.getItem('knowz-sh-view-mode:knowledge')).toBe('compact')
  })

  it('Should_ReadFromLocalStorage_OnMount', () => {
    localStorage.setItem('knowz-sh-view-mode:knowledge', 'list')
    const { result } = renderHook(() => useViewMode('knowledge'), { wrapper })
    expect(result.current.viewMode).toBe('list')
  })

  it('Should_ScopeStorageByPageKey_WhenDifferentPageKeysUsed', () => {
    const { result: r1 } = renderHook(() => useViewMode('knowledge'), { wrapper })
    const { result: r2 } = renderHook(() => useViewMode('vaults'), { wrapper })

    act(() => r1.current.setViewMode('gallery'))
    act(() => r2.current.setViewMode('code'))

    expect(localStorage.getItem('knowz-sh-view-mode:knowledge')).toBe('gallery')
    expect(localStorage.getItem('knowz-sh-view-mode:vaults')).toBe('code')
  })

  it('Should_FallbackToGrid_WhenStoredValueIsInvalid', () => {
    localStorage.setItem('knowz-sh-view-mode:knowledge', 'invalid-mode')
    const { result } = renderHook(() => useViewMode('knowledge'), { wrapper })
    expect(result.current.viewMode).toBe('grid')
  })

  it('Should_DefaultToKnowledge_WhenNoPageKeyProvided', () => {
    localStorage.setItem('knowz-sh-view-mode:knowledge', 'code')
    const { result } = renderHook(() => useViewMode(), { wrapper })
    expect(result.current.viewMode).toBe('code')
  })

  it('Should_ThrowError_WhenUsedOutsideProvider', () => {
    // Suppress React's error-boundary console noise for this intentional throw.
    const originalError = console.error
    console.error = () => {}
    try {
      expect(() => {
        renderHook(() => useViewMode())
      }).toThrow('useViewMode must be used within ViewModeProvider')
    } finally {
      console.error = originalError
    }
  })

  it('Should_UpdateState_WhenStorageEventFiredForPrefix', () => {
    const { result } = renderHook(() => useViewMode('knowledge'), { wrapper })
    expect(result.current.viewMode).toBe('grid')

    act(() => {
      window.dispatchEvent(
        new StorageEvent('storage', {
          key: 'knowz-sh-view-mode:knowledge',
          newValue: 'code',
        }),
      )
    })

    expect(result.current.viewMode).toBe('code')
  })
})
