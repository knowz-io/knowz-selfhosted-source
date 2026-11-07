import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { renderWithProviders } from './test-utils'
import LinkVaultModal from '../components/platform-sync/LinkVaultModal'
import type { PlatformConnectionDto, VaultSyncStatusDto } from '../lib/types'

vi.mock('../lib/api-client', () => ({
  api: {
    listVaults: vi.fn(),
    listPlatformVaults: vi.fn(),
    establishSyncLink: vi.fn(),
  },
  ApiError: class ApiError extends Error {
    status: number
    constructor(status: number, message: string) {
      super(message)
      this.status = status
      this.name = 'ApiError'
    }
  },
}))

import { api } from '../lib/api-client'

const mockListVaults = vi.mocked(api.listVaults)
const mockListPlatformVaults = vi.mocked(api.listPlatformVaults)
const mockEstablish = vi.mocked(api.establishSyncLink)

const base = {
  hasApiKey: true,
  apiKeyMask: 'ukz_****ABCD',
  remoteTenantId: null,
  lastTestedAt: null,
  lastTestStatus: 'Ok' as const,
  lastTestError: null,
  updatedAt: '2026-09-23T00:00:00Z',
}
const cloud: PlatformConnectionDto = {
  ...base,
  id: 'c-cloud',
  platformApiUrl: 'https://api.knowz.io',
  displayName: null,
}
const peer: PlatformConnectionDto = {
  ...base,
  id: 'c-peer',
  platformApiUrl: 'https://knowz.example.com',
  displayName: 'Office instance',
}

const existingLink: VaultSyncStatusDto = {
  linkId: 'link-1',
  localVaultId: 'local-linked',
  localVaultName: 'Already linked',
  remoteVaultId: 'remote-x',
  platformApiUrl: 'https://api.knowz.io',
  platformConnectionId: 'c-cloud',
  status: 'Ok',
  lastSyncError: null,
  lastSyncCompletedAt: null,
  lastPullCursor: null,
  lastPushCursor: null,
  syncEnabled: true,
}

describe('LinkVaultModal (UI_SH_PeerDestination)', () => {
  beforeEach(() => {
    mockListVaults.mockResolvedValue({
      vaults: [
        { id: 'local-a', name: 'Engineering', isDefault: false, createdAt: '2026-01-01T00:00:00Z' },
        { id: 'local-linked', name: 'Already linked', isDefault: false, createdAt: '2026-01-01T00:00:00Z' },
      ],
    })
    mockListPlatformVaults.mockResolvedValue({
      vaults: [
        { id: 'remote-a', name: 'Remote Engineering', description: null, knowledgeCount: 3, updatedAt: null },
      ],
      totalCount: 1,
    })
    mockEstablish.mockResolvedValue({
      ...existingLink,
      linkId: 'link-new',
      localVaultId: 'local-a',
      remoteVaultId: 'remote-a',
    })
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('Should_DefaultToThePrimary_AndBrowseItsVaults', async () => {
    renderWithProviders(
      <LinkVaultModal connections={[cloud, peer]} links={[]} onClose={() => {}} />,
    )
    expect(screen.getByRole('heading', { name: 'Link a vault' })).toBeInTheDocument()
    const picker = screen.getByRole('combobox', { name: 'Destination' }) as HTMLSelectElement
    expect(picker.value).toBe('c-cloud')
    await waitFor(() => {
      expect(mockListPlatformVaults).toHaveBeenCalledWith('c-cloud')
    })
    expect(screen.getByText(/It moves nothing/)).toBeInTheDocument()
  })

  it('Should_EstablishWithPlatformConnectionId_ForTheChosenPeer', async () => {
    const user = userEvent.setup()
    const onClose = vi.fn()
    renderWithProviders(
      <LinkVaultModal connections={[cloud, peer]} links={[existingLink]} onClose={onClose} />,
    )
    await user.selectOptions(screen.getByRole('combobox', { name: 'Destination' }), 'c-peer')
    await waitFor(() => {
      expect(mockListPlatformVaults).toHaveBeenCalledWith('c-peer')
    })
    expect(screen.getByRole('combobox', { name: 'Vault on Office instance' })).toBeInTheDocument()

    await waitFor(() => {
      expect(screen.getByRole('option', { name: 'Engineering' })).toBeInTheDocument()
    })
    // A vault that already has a link is not offered again.
    expect(screen.queryByRole('option', { name: 'Already linked' })).toBeNull()

    await user.selectOptions(screen.getByRole('combobox', { name: 'Vault on this instance' }), 'local-a')
    await waitFor(() => {
      expect(screen.getByRole('option', { name: 'Remote Engineering' })).toBeInTheDocument()
    })
    await user.selectOptions(screen.getByRole('combobox', { name: 'Vault on Office instance' }), 'remote-a')
    await user.click(screen.getByRole('button', { name: /link vault/i }))

    await waitFor(() => {
      expect(mockEstablish).toHaveBeenCalledWith({
        localVaultId: 'local-a',
        remoteVaultId: 'remote-a',
        platformConnectionId: 'c-peer',
      })
    })
    await waitFor(() => expect(onClose).toHaveBeenCalled())
  })

  it('Should_DisableSubmit_UntilBothVaultsAreChosen', async () => {
    renderWithProviders(<LinkVaultModal connections={[cloud]} links={[]} onClose={() => {}} />)
    expect(screen.getByRole('button', { name: /link vault/i })).toBeDisabled()
  })

  it('Should_ShowTheServerSentence_WhenTheLinkIsRefused', async () => {
    const user = userEvent.setup()
    const { ApiError } = await import('../lib/api-client')
    mockEstablish.mockRejectedValue(new ApiError(400, 'Vault local-a already has a sync link'))
    renderWithProviders(<LinkVaultModal connections={[cloud]} links={[]} onClose={() => {}} />)
    await waitFor(() => {
      expect(screen.getByRole('option', { name: 'Engineering' })).toBeInTheDocument()
      expect(screen.getByRole('option', { name: 'Remote Engineering' })).toBeInTheDocument()
    })
    await user.selectOptions(screen.getByRole('combobox', { name: 'Vault on this instance' }), 'local-a')
    await user.selectOptions(screen.getByRole('combobox', { name: 'Vault on Knowz Cloud' }), 'remote-a')
    await user.click(screen.getByRole('button', { name: /link vault/i }))
    const alert = await screen.findByRole('alert')
    expect(alert.textContent).toContain('Vault local-a already has a sync link')
  })

  it('Should_AskForADestination_WhenNoneIsConfigured', () => {
    renderWithProviders(<LinkVaultModal connections={[]} links={[]} onClose={() => {}} />)
    expect(screen.getByTestId('link-vault-no-destination')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /link vault/i })).toBeDisabled()
    expect(mockListPlatformVaults).not.toHaveBeenCalled()
  })
})
