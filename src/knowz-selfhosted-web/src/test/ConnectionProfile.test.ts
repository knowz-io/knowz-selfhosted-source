import { it, expect, vi, beforeEach } from 'vitest'
import { probeConnection, resetBrowserConnection, saveBrowserConnection } from '../lib/connection-profile'
beforeEach(() => { localStorage.clear(); sessionStorage.clear(); vi.restoreAllMocks() })
it('tests draft URL/key without using old token, key or tenant or changing storage', async () => {
  localStorage.setItem('apiUrl', 'https://old.example'); localStorage.setItem('apiKey', 'old-key')
  localStorage.setItem('activeTenantId', 'old-tenant'); sessionStorage.setItem('authToken', 'old-token')
  const fetch = vi.spyOn(globalThis, 'fetch')
    .mockResolvedValueOnce(new Response('Healthy', { status: 200 }))
    .mockResolvedValueOnce(new Response(JSON.stringify({ id: 'u1', username: 'admin', role: 0 }), { status: 200 }))
  expect(await probeConnection('https://draft.example', 'draft-key')).toMatchObject({ reachable: true, authenticated: true })
  expect(fetch.mock.calls[1][0]).toBe('https://draft.example/api/v1/auth/me')
  expect(fetch.mock.calls[1][1]?.headers).toEqual({ 'X-Api-Key': 'draft-key' })
  expect(fetch.mock.calls[1][1]?.redirect).toBe('error')
  expect(localStorage.getItem('apiKey')).toBe('old-key'); expect(sessionStorage.getItem('authToken')).toBe('old-token')
})
it('separates reachability from authentication and rejects wrong endpoint JSON', async () => {
  vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(new Response('Healthy')).mockResolvedValueOnce(new Response('{}'))
  expect(await probeConnection('https://wrong.example', '')).toMatchObject({ reachable: true, authenticated: false })
})
it('resets endpoint credentials, session, tenant and private conversation cache', () => {
  for (const key of ['apiUrl', 'apiKey', 'activeTenantId', 'knowz-conversations']) localStorage.setItem(key, 'stale')
  sessionStorage.setItem('authToken', 'stale')
  resetBrowserConnection()
  expect(localStorage.length).toBe(0); expect(sessionStorage.getItem('authToken')).toBeNull()
})
it('saves a new connection only after clearing the old session', () => {
  sessionStorage.setItem('authToken', 'old-token')
  saveBrowserConnection('https://new.example/', 'new-key')
  expect(localStorage.getItem('apiUrl')).toBe('https://new.example')
  expect(localStorage.getItem('apiKey')).toBe('new-key'); expect(sessionStorage.getItem('authToken')).toBeNull()
})
