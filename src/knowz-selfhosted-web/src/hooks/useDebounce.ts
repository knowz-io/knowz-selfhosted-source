import { useEffect, useState } from 'react'

/**
 * Returns a debounced copy of {@link value} that only updates after the value
 * has remained stable for {@link delayMs} milliseconds. Standard 300ms pattern
 * for search inputs that drive network requests.
 */
export function useDebounce<T>(value: T, delayMs: number = 300): T {
  const [debounced, setDebounced] = useState(value)

  useEffect(() => {
    const handle = setTimeout(() => setDebounced(value), delayMs)
    return () => clearTimeout(handle)
  }, [value, delayMs])

  return debounced
}
