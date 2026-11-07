import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query'
import { beforeEach, expect, it, vi } from 'vitest'
import { AccountEncryptedRecord } from '../components/portability/AccountEncryptedRecord'

const state = vi.hoisted(() => ({
  auth: { user: { id: 'user-a', tenantId: 'tenant-a' }, activeTenantId: null as string | null },
  callback: undefined as (() => void) | undefined,
  body: 'Before restore',
  history: 'Version 1',
}))
vi.mock('../lib/auth', () => ({ useAuth: () => state.auth }))
vi.mock('../components/portability/EncryptedRecordWorkspace', () => ({
  EncryptedRecordWorkspace: ({ onKnowledgeChanged }: { onKnowledgeChanged?: () => void }) => {
    state.callback = onKnowledgeChanged
    return <button onClick={() => {
      state.body = 'Restored ordinary body'
      state.history = 'Version 2'
      onKnowledgeChanged?.()
    }}>Committed protected change</button>
  },
}))

beforeEach(() => {
  state.auth = { user: { id: 'user-a', tenantId: 'tenant-a' }, activeTenantId: null }
  state.body = 'Before restore'; state.history = 'Version 1'; state.callback = undefined
})

it('refreshes the normal item body, metadata and history after the protected workspace commits', async () => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity } } })
  const item = vi.fn(async () => state.body), history = vi.fn(async () => state.history)
  function NormalDetail() {
    const body = useQuery({ queryKey: ['knowledge', 'item-a'], queryFn: item })
    const versions = useQuery({ queryKey: ['knowledge-versions', 'item-a'], queryFn: history })
    return <><p>{body.data}</p><p>{versions.data}</p><AccountEncryptedRecord vaultId="vault-a" knowledgeId="item-a" /></>
  }
  render(<QueryClientProvider client={client}><NormalDetail /></QueryClientProvider>)
  await screen.findByText('Before restore'); await screen.findByText('Version 1')
  fireEvent.click(screen.getByRole('button', { name: 'Committed protected change' }))
  await screen.findByText('Restored ordinary body'); await screen.findByText('Version 2')
  expect(item).toHaveBeenCalledTimes(2); expect(history).toHaveBeenCalledTimes(2)
  expect(screen.queryByText('Before restore')).toBeNull()
})

it.each(['tenant', 'item', 'account'])('does not refresh another %s from a stale workspace callback', async change => {
  const client = new QueryClient(), invalidate = vi.spyOn(client, 'invalidateQueries')
  const content = (id: string) => <QueryClientProvider client={client}><AccountEncryptedRecord vaultId="vault-a" knowledgeId={id} /></QueryClientProvider>
  const view = render(content('item-a')); const oldCallback = state.callback
  if (change === 'tenant') state.auth.activeTenantId = 'tenant-b'
  if (change === 'account') state.auth.user = { id: 'user-b', tenantId: 'tenant-a' }
  view.rerender(content(change === 'item' ? 'item-b' : 'item-a'))
  oldCallback?.()
  await waitFor(() => expect(invalidate).not.toHaveBeenCalled())
})
