/**
 * Date and byte-size formatters.
 *
 * The selfhosted API returns DateTime values read from SQL Server `datetime2`
 * columns without a timezone indicator (e.g. "2026-04-10T15:49:12.1834427").
 * `new Date(str)` parses such strings as LOCAL time, which causes timestamps
 * written in UTC to display as if they were in the browser's zone.
 *
 * `parseAsUtc` detects strings lacking a zone indicator and appends `Z` before
 * parsing, matching the main Knowz web client's `src/knowz-web-client/src/lib/
 * utils.ts` convention. All date display in this app should go through the
 * `formatDate` / `formatDateTime` / `formatTime` / `formatRelative` helpers
 * below, NOT `new Date(...).toLocaleString()` directly.
 */

export function formatFileSize(bytes: number): string {
  if (bytes === 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  const k = 1024
  const i = Math.floor(Math.log(bytes) / Math.log(k))
  const size = bytes / Math.pow(k, i)
  return `${size.toFixed(i > 0 ? 1 : 0)} ${units[i]}`
}

// --- Date parsing ---

/**
 * Parse a date value ensuring correct UTC interpretation.
 *
 * - Date instances are returned as-is.
 * - Date-only strings (YYYY-MM-DD, no time component) are parsed as LOCAL
 *   dates to avoid timezone shift (e.g. "2026-12-25" at midnight UTC would
 *   become Dec 24 in western zones).
 * - Naive ISO datetimes (no Z, no +/-HH:MM offset) are treated as UTC by
 *   appending `Z` before parsing. This is the common selfhosted API format.
 * - Strings that already carry a zone indicator are parsed as-is.
 */
export function parseAsUtc(date: string | Date): Date {
  if (date instanceof Date) return date

  // Date-only strings: avoid timezone shift by using the local-date constructor.
  if (/^\d{4}-\d{2}-\d{2}$/.test(date)) {
    const [y, m, d] = date.split('-').map(Number)
    return new Date(y, m - 1, d)
  }

  // The `-` check starts at index 10 to skip the date portion's hyphens.
  if (!date.endsWith('Z') && !date.includes('+') && !date.includes('-', 10)) {
    return new Date(date + 'Z')
  }

  return new Date(date)
}

// --- Display formatters ---

export interface FormatOptions {
  /** IANA timezone identifier (e.g. "America/New_York"). Defaults to browser. */
  timeZone?: string
  /** BCP-47 locale (e.g. "en-US"). Defaults to browser. */
  locale?: string
}

/**
 * Format a server timestamp as a short date (e.g. "Apr 10, 2026").
 */
export function formatDate(date: string | Date, options: FormatOptions = {}): string {
  const d = parseAsUtc(date)
  return new Intl.DateTimeFormat(options.locale ?? 'en-US', {
    month: 'short',
    day: 'numeric',
    year: 'numeric',
    timeZone: options.timeZone,
  }).format(d)
}

/**
 * Format a server timestamp as date + time (e.g. "Apr 10, 2026 at 11:49 AM").
 */
export function formatDateTime(date: string | Date, options: FormatOptions = {}): string {
  const d = parseAsUtc(date)
  return new Intl.DateTimeFormat(options.locale ?? 'en-US', {
    month: 'short',
    day: 'numeric',
    year: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
    hour12: true,
    timeZone: options.timeZone,
  }).format(d)
}

/**
 * Format a server timestamp as time only (e.g. "11:49 AM").
 */
export function formatTime(date: string | Date, options: FormatOptions = {}): string {
  const d = parseAsUtc(date)
  return new Intl.DateTimeFormat(options.locale ?? 'en-US', {
    hour: 'numeric',
    minute: '2-digit',
    hour12: true,
    timeZone: options.timeZone,
  }).format(d)
}

/**
 * Format a server timestamp as a relative phrase ("just now", "5 minutes ago",
 * "yesterday", or a short date for values > 7 days old).
 *
 * Important: this uses `parseAsUtc` so that relative calculations against a
 * UTC server timestamp are correct regardless of the browser's zone. Before
 * this helper, a just-created item could display as "4 hours ago" if the
 * browser was in EDT.
 */
export function formatRelative(date: string | Date, options: FormatOptions = {}): string {
  const d = parseAsUtc(date)
  const diffMs = Date.now() - d.getTime()
  const diffSec = Math.round(diffMs / 1000)

  if (diffSec < 0) return 'just now' // clock skew
  if (diffSec < 45) return 'just now'
  if (diffSec < 90) return '1 minute ago'

  const diffMin = Math.round(diffSec / 60)
  if (diffMin < 45) return `${diffMin} minutes ago`
  if (diffMin < 90) return '1 hour ago'

  const diffHours = Math.round(diffMin / 60)
  if (diffHours < 24) return `${diffHours} hours ago`
  if (diffHours < 36) return 'yesterday'

  const diffDays = Math.round(diffHours / 24)
  if (diffDays < 7) return `${diffDays} days ago`

  // More than a week — fall back to a short absolute date.
  return formatDate(d, options)
}
