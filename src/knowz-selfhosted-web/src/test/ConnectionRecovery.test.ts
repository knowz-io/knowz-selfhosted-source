import { it, expect, vi, beforeEach } from 'vitest'
import { api } from '../lib/api-client'

beforeEach(() => { localStorage.clear(); sessionStorage.clear(); vi.restoreAllMocks() })
it('does not replay a failed write against the page origin', async () => {
  localStorage.setItem('apiUrl', 'https://saved.example')
  const fetch = vi.spyOn(globalThis, 'fetch').mockRejectedValue(new TypeError('network failed'))
  await expect(api.createKnowledge({ content: 'must stay at selected endpoint' })).rejects.toThrow()
  expect(fetch).toHaveBeenCalledTimes(1)
  expect(localStorage.getItem('apiUrl')).toBe('https://saved.example')
})
it('does not replay failed uploads against a different origin', async () => {
  localStorage.setItem('apiUrl', 'https://saved.example')
  const fetch = vi.spyOn(globalThis, 'fetch').mockRejectedValue(new TypeError('network failed'))
  await expect(api.uploadFile(new File(['private'], 'note.txt'))).rejects.toThrow()
  expect(fetch).toHaveBeenCalledTimes(1)
})
it('prevents transport redirects from forwarding credentials or writes to another host', async () => {
  localStorage.setItem('apiUrl', 'https://selected.example'); localStorage.setItem('apiKey','selected-key')
  const fetch = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(JSON.stringify({id:'item'})))
  await api.createKnowledge({content:'private'})
  expect(fetch.mock.calls[0][1]?.redirect).toBe('error')
})
it('does not clear a new session when an old request later returns unauthorized', async () => {
  sessionStorage.setItem('authToken','old-token')
  let respond!: (response:Response) => void
  vi.spyOn(globalThis,'fetch').mockImplementation(() => new Promise(resolve => {respond=resolve}))
  const pending = api.getMe()
  sessionStorage.setItem('authToken','new-token')
  respond(new Response('{}',{status:401}))
  await expect(pending).rejects.toThrow()
  expect(sessionStorage.getItem('authToken')).toBe('new-token')
})
