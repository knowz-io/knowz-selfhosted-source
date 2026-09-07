import { useContext, useMemo } from 'react'
import { AuthContext } from '../lib/auth'
import {
  formatDate,
  formatDateTime,
  formatTime,
  formatRelative,
  type FormatOptions,
} from '../lib/format-utils'

/**
 * Default timezone when the authenticated user has no explicit preference.
 * Matches the platform convention of defaulting to Eastern Time.
 */
export const DEFAULT_USER_TIMEZONE = 'America/New_York'

export interface BoundFormatters {
  /** IANA timezone the formatters are currently bound to. */
  timeZone: string
  /** Format as short date (e.g. "Apr 10, 2026"). */
  date: (value: string | Date) => string
  /** Format as date + time (e.g. "Apr 10, 2026 at 11:49 AM"). */
  dateTime: (value: string | Date) => string
  /** Format as time only (e.g. "11:49 AM"). */
  time: (value: string | Date) => string
  /** Format as relative phrase ("just now", "5 minutes ago"). */
  relative: (value: string | Date) => string
}

/**
 * Returns date formatters bound to the current user's timezone preference.
 * Falls back to `America/New_York` when the user has no preference set.
 *
 * Usage:
 * ```tsx
 * const fmt = useFormatters()
 * <span>{fmt.dateTime(createdAt)}</span>
 * ```
 *
 * All formatters handle the selfhosted API's naive UTC timestamps
 * (no `Z` suffix) correctly — see `parseAsUtc` in format-utils.ts.
 */
export function useFormatters(): BoundFormatters {
  // Read the auth context directly rather than via `useAuth()` so this hook
  // degrades gracefully when used outside an AuthProvider — for example in
  // isolated component tests that don't wrap the tree with auth. In that
  // case we fall back to DEFAULT_USER_TIMEZONE.
  const ctx = useContext(AuthContext)
  const timeZone = ctx?.user?.timeZonePreference ?? DEFAULT_USER_TIMEZONE

  return useMemo(() => {
    const options: FormatOptions = { timeZone }
    return {
      timeZone,
      date: (value) => formatDate(value, options),
      dateTime: (value) => formatDateTime(value, options),
      time: (value) => formatTime(value, options),
      relative: (value) => formatRelative(value, options),
    }
  }, [timeZone])
}
