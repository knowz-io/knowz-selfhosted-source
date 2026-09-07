/** Credentials are scoped to the explicitly selected endpoint, never used for fallback hosts. */
export const CONNECTION_CHANGED = 'knowz:connection-changed'

export function normalizeConnectionUrl(value: string): string {
  const url = new URL(value.trim() || window.location.origin)
  if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password || url.search || url.hash) {
    throw new Error('Use an HTTP or HTTPS API URL without credentials, query or fragment.')
  }
  return url.href.replace(/\/+$/, '')
}

function clearConnectionState() {
  for (const key of ['apiUrl', 'apiKey', 'activeTenantId', 'knowz-conversations']) localStorage.removeItem(key)
  sessionStorage.removeItem('authToken')
}
export function resetBrowserConnection() {
  clearConnectionState()
  window.dispatchEvent(new Event(CONNECTION_CHANGED))
}
export function saveBrowserConnection(url: string, key: string) {
  const normalized = normalizeConnectionUrl(url)
  clearConnectionState()
  if (normalized !== window.location.origin) localStorage.setItem('apiUrl', normalized)
  if (key.trim()) localStorage.setItem('apiKey', key.trim())
  window.dispatchEvent(new Event(CONNECTION_CHANGED))
}

export async function probeConnection(url: string, key: string): Promise<{
  reachable: boolean; authenticated: boolean; message: string
}> {
  const endpoint = normalizeConnectionUrl(url)
  const headers: Record<string, string> = key.trim() ? { 'X-Api-Key': key.trim() } : {}
  // Only an unchanged endpoint without an entered key may test its current session.
  if (!key.trim() && endpoint === normalizeConnectionUrl(localStorage.getItem('apiUrl') || '')) {
    const token = sessionStorage.getItem('authToken')
    if (token) headers.Authorization = `Bearer ${token}`
  }
  const options = { redirect: 'error' as const, credentials: 'omit' as const, signal: AbortSignal.timeout(10000) }
  let reachable = false
  try {
    const health = await fetch(`${endpoint}/healthz`, options)
    reachable = health.ok
    const identity = await fetch(`${endpoint}/api/v1/auth/me`, { ...options, headers })
    const user = identity.ok ? await identity.json().catch(() => null) : null
    const authenticated = Boolean(user && typeof user.id === 'string' && typeof user.username === 'string' && user.role !== undefined)
    return { reachable, authenticated, message: authenticated ? 'Knowz API authentication verified.' : 'API authentication was not verified. Check the endpoint and credential.' }
  } catch {
    return { reachable, authenticated: false, message: 'Connection could not be verified. Check the URL and that the instance is running.' }
  }
}
