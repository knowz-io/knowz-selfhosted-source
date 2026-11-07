import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { renderWithProviders } from './test-utils'
import ConnectionList from '../components/platform-sync/ConnectionList'
import type { PlatformConnectionDto, VaultSyncStatusDto } from '../lib/types'

vi.mock('../lib/api-client', () => ({
  api: {
    testPlatformConnectionCandidate: vi.fn(),
    testPlatformConnection: vi.fn(),
    testPlatformConnectionById: vi.fn(),
    upsertPlatformConnection: vi.fn(),
    createPlatformConnection: vi.fn(),
    updatePlatformConnection: vi.fn(),
    deletePlatformConnection: vi.fn(),
    deletePlatformConnectionById: vi.fn(),
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

const mockCreate = vi.mocked(api.createPlatformConnection)
const mockTestById = vi.mocked(api.testPlatformConnectionById)
const mockTestPrimary = vi.mocked(api.testPlatformConnection)

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

const linkTo = (connectionId: string, url: string, n: number): VaultSyncStatusDto => ({
  linkId: `link-${n}`,
  localVaultId: `local-${n}`,
  localVaultName: `Vault ${n}`,
  remoteVaultId: `remote-${n}`,
  platformApiUrl: url,
  platformConnectionId: connectionId,
  status: 'Ok',
  lastSyncError: null,
  lastSyncCompletedAt: null,
  lastPullCursor: null,
  lastPushCursor: null,
  syncEnabled: true,
})

describe('ConnectionList (UI_SH_PeerDestination)', () => {
  beforeEach(() => {
    mockCreate.mockResolvedValue(peer)
    mockTestById.mockResolvedValue({
      status: 'Ok',
      message: 'Peer reachable.',
      remoteTenantId: 'abc',
      schemaVersion: '1.0',
    })
    mockTestPrimary.mockResolvedValue({
      status: 'Ok',
      message: 'Connection is healthy.',
      remoteTenantId: 'abc',
      schemaVersion: '1.0',
    })
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('Should_RenderEveryConnection_WithPrimaryBadgeOnTheFirst', () => {
    renderWithProviders(<ConnectionList connections={[cloud, peer]} links={[]} isLoading={false} />)
    expect(screen.getByRole('heading', { name: 'Knowz Cloud' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Office instance' })).toBeInTheDocument()
    const badges = screen.getAllByTestId('primary-badge')
    expect(badges).toHaveLength(1)
    const primaryCard = screen.getByTestId('platform-connection-card')
    expect(within(primaryCard).getByTestId('primary-badge')).toBeInTheDocument()
    expect(within(primaryCard).getByTestId('destination-kind').textContent).toBe('Knowz Cloud')
    const peerCard = screen.getByTestId('peer-connection-card')
    expect(within(peerCard).getByTestId('destination-kind').textContent).toBe('Allowlisted peer')
    expect(screen.getByText('2 configured')).toBeInTheDocument()
  })

  it('Should_ShowTheCloudConnectForm_WhenNothingIsConfigured', () => {
    renderWithProviders(<ConnectionList connections={[]} links={[]} isLoading={false} />)
    expect(screen.getByRole('heading', { name: 'Connect to Knowz Cloud' })).toBeInTheDocument()
    expect(screen.getByText('Not Connected')).toBeInTheDocument()
    expect(screen.queryByTestId('primary-badge')).toBeNull()
  })

  it('Should_NotFlashTheEmptyForm_WhileLoading', () => {
    renderWithProviders(<ConnectionList connections={[]} links={[]} isLoading />)
    expect(screen.getByTestId('connection-list-loading')).toBeInTheDocument()
    expect(screen.queryByPlaceholderText('https://api.knowz.io')).toBeNull()
  })

  it('Should_StateTheAllowlistRequirement_AndNoContinuousSync', () => {
    renderWithProviders(<ConnectionList connections={[cloud]} links={[]} isLoading={false} />)
    const honesty = screen.getByTestId('peer-honesty').textContent ?? ''
    expect(honesty).toContain('Sync:AllowedPlatformHosts')
    expect(honesty).toMatch(/private and LAN peers require explicit operator opt-in/i)
    expect(honesty).toMatch(/nothing syncs continuously/i)
    expect(honesty).not.toMatch(/mesh|automatic/i)
  })

  it('Should_PostToConnections_WhenAPeerIsAdded', async () => {
    const user = userEvent.setup()
    renderWithProviders(<ConnectionList connections={[cloud]} links={[]} isLoading={false} />)
    await user.click(screen.getByTestId('add-peer-button'))

    const form = screen.getByTestId('peer-connection-card')
    expect(within(form).getByRole('heading', { name: 'Add an allowlisted peer' })).toBeInTheDocument()
    expect(within(form).getByTestId('peer-url-guidance').textContent).toContain(
      'Sync:AllowedPlatformHosts',
    )

    await user.type(within(form).getByPlaceholderText('https://knowz.example.com'), 'https://knowz.example.com')
    await user.type(within(form).getByPlaceholderText('e.g. Office instance'), 'Office instance')
    await user.type(within(form).getByTestId('platform-connection-api-key'), 'ukz_peerkey')
    await user.click(within(form).getByRole('button', { name: /add peer/i }))

    await waitFor(() => {
      expect(mockCreate).toHaveBeenCalledWith({
        platformApiUrl: 'https://knowz.example.com',
        displayName: 'Office instance',
        apiKey: 'ukz_peerkey',
      })
    })
    // The add form closes on success; the button comes back.
    await waitFor(() => {
      expect(screen.queryByRole('heading', { name: 'Add an allowlisted peer' })).toBeNull()
    })
    expect(screen.getByTestId('add-peer-button')).toBeInTheDocument()
  })

  it('Should_SurfaceTheAllowlistRejection_WithTheOperatorAction', async () => {
    const user = userEvent.setup()
    const { ApiError } = await import('../lib/api-client')
    mockCreate.mockRejectedValue(
      new ApiError(400, "Host 'other.example.com' is not in the platform allowlist."),
    )
    renderWithProviders(<ConnectionList connections={[cloud]} links={[]} isLoading={false} />)
    await user.click(screen.getByTestId('add-peer-button'))
    const form = screen.getByTestId('peer-connection-card')
    await user.type(within(form).getByPlaceholderText('https://knowz.example.com'), 'https://other.example.com')
    await user.type(within(form).getByTestId('platform-connection-api-key'), 'ukz_peerkey')
    await user.click(within(form).getByRole('button', { name: /add peer/i }))

    const banner = await within(form).findByTestId('connection-banner')
    expect(banner.textContent).toContain("Host 'other.example.com' is not in the platform allowlist.")
    expect(banner.textContent).toContain('Sync:AllowedPlatformHosts')
  })

  it('Should_TestByConnectionId_ForANonPrimaryPeer', async () => {
    const user = userEvent.setup()
    renderWithProviders(<ConnectionList connections={[cloud, peer]} links={[]} isLoading={false} />)
    const peerCard = screen.getByTestId('peer-connection-card')
    await user.click(within(peerCard).getByRole('button', { name: /test connection/i }))
    await waitFor(() => {
      expect(mockTestById).toHaveBeenCalledWith('c-peer')
    })
    expect(mockTestPrimary).not.toHaveBeenCalled()
  })

  it('Should_TestThePrimary_ThroughTheLegacyRoute', async () => {
    const user = userEvent.setup()
    renderWithProviders(<ConnectionList connections={[cloud, peer]} links={[]} isLoading={false} />)
    const primaryCard = screen.getByTestId('platform-connection-card')
    await user.click(within(primaryCard).getByRole('button', { name: /test connection/i }))
    await waitFor(() => {
      expect(mockTestPrimary).toHaveBeenCalled()
    })
    expect(mockTestById).not.toHaveBeenCalled()
  })

  it('Should_CountLinks_PerConnection_InTheRemoveWarning', async () => {
    const user = userEvent.setup()
    const links = [
      linkTo('c-cloud', 'https://api.knowz.io', 1),
      linkTo('c-peer', 'https://knowz.example.com', 2),
      linkTo('c-peer', 'https://knowz.example.com', 3),
    ]
    renderWithProviders(<ConnectionList connections={[cloud, peer]} links={links} isLoading={false} />)
    const peerCard = screen.getByTestId('peer-connection-card')
    await user.click(within(peerCard).getByRole('button', { name: /^disconnect$/i }))
    expect(screen.getByText('Remove peer connection?')).toBeInTheDocument()
    expect(screen.getByText(/2 vault sync links still reference/)).toBeInTheDocument()
  })
})
