import { describe, it, expect, beforeAll, afterAll, vi } from 'vitest'
import {
  parseAsUtc,
  formatDate,
  formatDateTime,
  formatTime,
  formatRelative,
  formatFileSize,
} from '../lib/format-utils'

describe('parseAsUtc', () => {
  it('passes through Date instances unchanged', () => {
    const d = new Date('2026-04-10T15:49:00Z')
    expect(parseAsUtc(d)).toBe(d)
  })

  it('treats naive ISO datetime (no Z, no offset) as UTC', () => {
    // This is the common selfhosted API response format after a DB round-trip.
    const d = parseAsUtc('2026-04-10T15:49:12.1834427')
    expect(d.toISOString()).toBe('2026-04-10T15:49:12.183Z')
  })

  it('respects trailing Z', () => {
    const d = parseAsUtc('2026-04-10T15:49:12Z')
    expect(d.toISOString()).toBe('2026-04-10T15:49:12.000Z')
  })

  it('respects positive timezone offset', () => {
    const d = parseAsUtc('2026-04-10T20:49:12+05:00')
    expect(d.toISOString()).toBe('2026-04-10T15:49:12.000Z')
  })

  it('respects negative timezone offset', () => {
    // Offset hyphen appears at index 19, past the date portion's hyphens at index 4/7.
    const d = parseAsUtc('2026-04-10T11:49:12-04:00')
    expect(d.toISOString()).toBe('2026-04-10T15:49:12.000Z')
  })

  it('treats date-only strings as LOCAL dates (no timezone shift)', () => {
    // "2026-12-25" should be Christmas in the local zone, not Dec 24 in the US.
    const d = parseAsUtc('2026-12-25')
    expect(d.getFullYear()).toBe(2026)
    expect(d.getMonth()).toBe(11) // December
    expect(d.getDate()).toBe(25)
  })
})

describe('formatDate', () => {
  it('formats a UTC timestamp in the given zone', () => {
    const result = formatDate('2026-04-10T15:49:12', { timeZone: 'America/New_York' })
    expect(result).toBe('Apr 10, 2026')
  })

  it('accepts a Date instance', () => {
    const result = formatDate(new Date('2026-04-10T15:49:12Z'), { timeZone: 'UTC' })
    expect(result).toBe('Apr 10, 2026')
  })
})

describe('formatDateTime', () => {
  it('converts naive UTC to Eastern display correctly', () => {
    // 15:49 UTC = 11:49 AM EDT (April is EDT, UTC-4).
    const result = formatDateTime('2026-04-10T15:49:12', { timeZone: 'America/New_York' })
    expect(result).toMatch(/Apr 10, 2026/)
    expect(result).toContain('11:49')
    expect(result).toContain('AM')
  })

  it('renders in UTC when requested', () => {
    const result = formatDateTime('2026-04-10T15:49:12', { timeZone: 'UTC' })
    expect(result).toMatch(/Apr 10, 2026/)
    expect(result).toContain('3:49')
    expect(result).toContain('PM')
  })

  it('renders in Los Angeles time correctly', () => {
    // 15:49 UTC = 8:49 AM PDT.
    const result = formatDateTime('2026-04-10T15:49:12', { timeZone: 'America/Los_Angeles' })
    expect(result).toContain('8:49')
    expect(result).toContain('AM')
  })
})

describe('formatTime', () => {
  it('formats just the time portion in the given zone', () => {
    const result = formatTime('2026-04-10T15:49:12', { timeZone: 'America/New_York' })
    expect(result).toContain('11:49')
    expect(result).toContain('AM')
  })
})

describe('formatRelative', () => {
  const FIXED_NOW = new Date('2026-04-10T16:00:00Z').getTime()

  beforeAll(() => {
    vi.useFakeTimers()
    vi.setSystemTime(FIXED_NOW)
  })

  afterAll(() => {
    vi.useRealTimers()
  })

  it('returns "just now" for values within 45 seconds', () => {
    expect(formatRelative('2026-04-10T15:59:30')).toBe('just now')
  })

  it('returns "just now" for values very slightly in the future (clock skew)', () => {
    expect(formatRelative('2026-04-10T16:00:05')).toBe('just now')
  })

  it('returns "N minutes ago" for values within 45 minutes', () => {
    expect(formatRelative('2026-04-10T15:55:00')).toBe('5 minutes ago')
  })

  it('returns "1 hour ago" for values between 45 and 90 minutes', () => {
    // 16:00 - 14:45 = 75 minutes → "1 hour ago" (falls in 45 < m < 90 branch)
    expect(formatRelative('2026-04-10T14:45:00')).toBe('1 hour ago')
  })

  it('returns "N hours ago" for values under 24 hours', () => {
    expect(formatRelative('2026-04-10T12:00:00')).toBe('4 hours ago')
  })

  it('returns "yesterday" for values ~25 hours old', () => {
    expect(formatRelative('2026-04-09T15:00:00')).toBe('yesterday')
  })

  it('returns "N days ago" for values within a week', () => {
    expect(formatRelative('2026-04-07T16:00:00')).toBe('3 days ago')
  })

  it('falls back to a short date for values older than a week', () => {
    expect(formatRelative('2026-03-01T16:00:00', { timeZone: 'UTC' })).toBe('Mar 1, 2026')
  })

  it('handles naive timestamps correctly — a just-created item is "just now", not "4 hours ago"', () => {
    // Regression test for the original bug: the server writes UTC but returns
    // the string without a Z. For a just-created item, the relative helper
    // must return "just now" regardless of browser zone.
    const serverNow = '2026-04-10T15:59:55.1234567' // ~5 seconds ago in UTC
    expect(formatRelative(serverNow)).toBe('just now')
  })
})

describe('formatFileSize', () => {
  it('formats bytes', () => {
    expect(formatFileSize(0)).toBe('0 B')
    expect(formatFileSize(512)).toBe('512 B')
  })

  it('formats kilobytes', () => {
    expect(formatFileSize(1024)).toBe('1.0 KB')
    expect(formatFileSize(2048)).toBe('2.0 KB')
  })

  it('formats megabytes', () => {
    expect(formatFileSize(1024 * 1024)).toBe('1.0 MB')
    expect(formatFileSize(2.2 * 1024 * 1024)).toBe('2.2 MB')
  })
})
