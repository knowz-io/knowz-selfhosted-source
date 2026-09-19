/**
 * SH_InstanceCopyHygiene R10 — maps a thrown or returned error to product copy.
 *
 * Rules:
 *  - a known HTTP status maps to one sentence;
 *  - anything else maps to one generic sentence, plus at most a short detail
 *    line when the detail is already product-shaped;
 *  - EF / SQL / stack / driver text NEVER reaches the user.
 */

const STATUS_COPY: Record<number, string> = {
  400: "That request wasn't valid. Check the values and try again.",
  401: 'Your session has expired. Sign in again to continue.',
  403: "You don't have permission to do that.",
  404: "We couldn't find what you were looking for.",
  409: 'Someone else changed this first. Reload and try again.',
  413: 'That file is too large for this instance.',
  429: 'Too many requests. Wait a moment and try again.',
  500: 'Something went wrong on this instance. Try again in a moment.',
  502: 'This instance could not reach a service it depends on.',
  503: 'This instance is still starting up. Try again in a moment.',
  504: 'That took too long to respond. Try again in a moment.',
}

const GENERIC = 'Something went wrong. Try again in a moment.'
const OFFLINE = 'Could not reach this instance. Check that it is running.'

/** Detail text that looks like machine internals is dropped, never rendered. */
const INTERNAL_MARKERS = [
  /\b\d{2}[A-Z]\d{2}\b/,           // SQLSTATE, e.g. 42P01
  /\brelation\b/i,
  /\bcolumn\b/i,
  /\btable\b/i,
  /\bnpgsql\b/i,
  /\bsql\b/i,
  /\bDbUpdate/i,
  /\bEntityFramework/i,
  /\bSystem\./,
  /\bat [A-Za-z0-9_.]+\(/,          // stack frames
  /\bexception\b/i,
  /\bstack trace\b/i,
  /\bconnection string\b/i,
]

export function isSafeDetail(detail: string): boolean {
  if (detail.length === 0 || detail.length > 160) return false
  return !INTERNAL_MARKERS.some((re) => re.test(detail))
}

export function toUserError(err: unknown, fallback: string = GENERIC): string {
  if (err == null) return fallback

  const status =
    typeof err === 'object' && err !== null && 'status' in err
      ? Number((err as { status: unknown }).status)
      : undefined

  if (typeof status === 'number' && !Number.isNaN(status)) {
    if (status === 0) return OFFLINE
    const mapped = STATUS_COPY[status]
    if (mapped) return mapped
    if (status >= 500) return STATUS_COPY[500]
    if (status >= 400) return STATUS_COPY[400]
  }

  const raw =
    err instanceof Error
      ? err.message
      : typeof err === 'string'
        ? err
        : ''

  if (/failed to fetch|networkerror|load failed/i.test(raw)) return OFFLINE

  const detail = raw.trim()
  if (isSafeDetail(detail)) return `${fallback} ${detail}`.trim()
  return fallback
}

export default toUserError
