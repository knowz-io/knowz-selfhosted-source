import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { renderWithProviders } from './test-utils'
import PlatformSyncPage from '../pages/admin/PlatformSyncPage'
import type { PlatformConnectionDto, VaultSyncStatusDto } from '../lib/types'

vi.mock('../lib/api-client', () => ({
  api: {
    getPlatformConnection: vi.fn(),
    listPlatformConnections: vi.fn(),
    createPlatformConnection: vi.fn(),
    updatePlatformConnection: vi.fn(),
    deletePlatformConnectionById: vi.fn(),
    testPlatformConnectionById: vi.fn(),
    establishSyncLink: vi.fn(),
    listVaults: vi.fn(),
    upsertPlatformConnection: vi.fn(),
    deletePlatformConnection: vi.fn(),
    testPlatformConnection: vi.fn(),
    testPlatformConnectionCandidate: vi.fn(),
    listSyncLinks: vi.fn(),
    runSyncLink: vi.fn(),
    removeSyncLink: vi.fn(),
    getPlatformSyncHistory: vi.fn(),
    listPlatformVaults: vi.fn(),
    listPlatformKnowledge: vi.fn(),
    getPlatformKnowledge: vi.fn(),
    pullPlatformItem: vi.fn(),
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

const mockListConnections = vi.mocked(api.listPlatformConnections)
const mockListLinks = vi.mocked(api.listSyncLinks)
const mockListVaults = vi.mocked(api.listVaults)
const mockListPlatformVaults = vi.mocked(api.listPlatformVaults)
const mockGetHistory = vi.mocked(api.getPlatformSyncHistory)

const connectedDto: PlatformConnectionDto = {
  id: 'c-cloud',
  platformApiUrl: 'https://api.knowz.io',
  displayName: 'Prod',
  hasApiKey: true,
  apiKeyMask: 'ukz_****ABCD',
  remoteTenantId: 'tenant-1',
  lastTestedAt: '2026-03-01T10:00:00Z',
  lastTestStatus: 'Ok',
  lastTestError: null,
  updatedAt: '2026-03-01T10:00:00Z',
}

const link: VaultSyncStatusDto = {
  linkId: 'link-1',
  localVaultId: 'local-1',
  localVaultName: 'Engineering Docs',
  remoteVaultId: 'remote-1',
  platformApiUrl: 'https://api.knowz.io',
  status: 'Ok',
  lastSyncError: null,
  lastSyncCompletedAt: '2026-03-01T12:00:00Z',
  lastPullCursor: null,
  lastPushCursor: null,
  syncEnabled: true,
}

describe('PlatformSyncPage', () => {
  beforeEach(() => {
    mockListConnections.mockResolvedValue([connectedDto])
    mockListLinks.mockResolvedValue([link])
    mockGetHistory.mockResolvedValue([])
    mockListVaults.mockResolvedValue({ vaults: [] })
    mockListPlatformVaults.mockResolvedValue({ vaults: [], totalCount: 0 })
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('Should_RenderDestinationsHeading_WhenMounted', async () => {
    renderWithProviders(<PlatformSyncPage />)
    expect(screen.getByRole('heading', { level: 1, name: 'Destinations' })).toBeInTheDocument()
  })

  it('Should_RenderManualOnlySubtitle_WhenMounted', async () => {
    renderWithProviders(<PlatformSyncPage />)
    expect(screen.getByText(/Nothing syncs automatically/)).toBeInTheDocument()
  })

  it('Should_RenderSecondaryFilePlaneCard_LinkingToDataTab', async () => {
    renderWithProviders(<PlatformSyncPage />)
    expect(
      screen.getByRole('heading', { name: 'File export / import' }),
    ).toBeInTheDocument()
    const link = screen.getByRole('link', { name: /file export \/ import/i })
    expect(link).toHaveAttribute('href', '/settings?tab=data')
  })

  it('Should_RenderConnectionCard_WithHeader', async () => {
    renderWithProviders(<PlatformSyncPage />)
    expect(screen.getByRole('heading', { name: 'Connections' })).toBeInTheDocument()
    // The primary Cloud connection is a card headed by its display name.
    await waitFor(() => {
      expect(screen.getByRole('heading', { name: 'Prod' })).toBeInTheDocument()
    })
    expect(screen.getByTestId('primary-badge')).toBeInTheDocument()
  })

  it('Should_RenderVaultLinksTable_WithLinks', async () => {
    renderWithProviders(<PlatformSyncPage />)
    await waitFor(() => {
      expect(screen.getByText('Vault Sync Links')).toBeInTheDocument()
    })
    // "Engineering Docs" appears both in the VaultLinksTable row and in the
    // SyncHistoryTable vault filter option. At least one must exist.
    await waitFor(() => {
      expect(screen.getAllByText('Engineering Docs').length).toBeGreaterThan(0)
    })
  })

  it('Should_RenderSyncHistoryTable_WhenMounted', async () => {
    renderWithProviders(<PlatformSyncPage />)
    await waitFor(() => {
      expect(screen.getByText('Sync History')).toBeInTheDocument()
    })
    await waitFor(() => {
      expect(mockGetHistory).toHaveBeenCalled()
    })
  })

  it('Should_OpenBrowseModal_WhenBrowseKnowzCloudClicked', async () => {
    const user = userEvent.setup()
    renderWithProviders(<PlatformSyncPage />)
    await waitFor(() => {
      expect(screen.getAllByText('Engineering Docs').length).toBeGreaterThan(0)
    })
    await user.click(screen.getByRole('button', { name: /browse knowz cloud/i }))
    // Modal renders a heading "Browse Knowz Cloud" (h2) which is distinct from
    // the trigger button. Assert on the heading role.
    await waitFor(() => {
      expect(screen.getByRole('heading', { name: /browse knowz cloud/i })).toBeInTheDocument()
    })
  })

  it('Should_TreatConnection404AsNull_WhenEndpointMissing', async () => {
    const { ApiError } = await import('../lib/api-client')
    mockListConnections.mockRejectedValue(new ApiError(404, 'Not found'))
    renderWithProviders(<PlatformSyncPage />)
    await waitFor(() => {
      expect(screen.getByText('Not Connected')).toBeInTheDocument()
    })
    expect(screen.getByRole('heading', { name: 'Connect to Knowz Cloud' })).toBeInTheDocument()
  })

  it('Should_CallGetHistoryWithDefaultLimit50_OnMount', async () => {
    renderWithProviders(<PlatformSyncPage />)
    await waitFor(() => {
      expect(mockGetHistory).toHaveBeenCalledWith(1, 50, undefined)
    })
  })
})

// ---------- UI_SH_PeerDestination : several destinations on one page ----------
describe('PlatformSyncPage — peer destinations (UI_SH_PeerDestination)', () => {
  const peerDto: PlatformConnectionDto = {
    ...connectedDto,
    id: 'c-peer',
    platformApiUrl: 'https://knowz.example.com',
    displayName: 'Office instance',
  }

  beforeEach(() => {
    mockListConnections.mockResolvedValue([connectedDto, peerDto])
    mockListLinks.mockResolvedValue([link])
    mockGetHistory.mockResolvedValue([])
    mockListVaults.mockResolvedValue({ vaults: [] })
    mockListPlatformVaults.mockResolvedValue({ vaults: [], totalCount: 0 })
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('Should_ListEveryConnection_WithOnePrimaryBadge', async () => {
    renderWithProviders(<PlatformSyncPage />)
    await waitFor(() => {
      expect(screen.getByRole('heading', { name: 'Office instance' })).toBeInTheDocument()
    })
    expect(screen.getByRole('heading', { name: 'Prod' })).toBeInTheDocument()
    expect(screen.getAllByTestId('primary-badge')).toHaveLength(1)
    expect(screen.getByText('2 configured')).toBeInTheDocument()
  })

  it('Should_StateTheAllowlistRequirement_InTheSubtitle', () => {
    renderWithProviders(<PlatformSyncPage />)
    expect(screen.getByText(/must be allowlisted by the operator/)).toBeInTheDocument()
    expect(screen.getByText(/Nothing syncs automatically/)).toBeInTheDocument()
  })

  it('Should_OpenTheLinkVaultFlow_WithADestinationPicker', async () => {
    const user = userEvent.setup()
    renderWithProviders(<PlatformSyncPage />)
    await waitFor(() => {
      expect(screen.getByRole('heading', { name: 'Office instance' })).toBeInTheDocument()
    })
    await user.click(screen.getByRole('button', { name: /link a vault/i }))
    await waitFor(() => {
      expect(screen.getByRole('heading', { name: 'Link a vault' })).toBeInTheDocument()
    })
    const picker = screen.getByRole('combobox', { name: 'Destination' }) as HTMLSelectElement
    expect(picker.value).toBe('c-cloud')
    expect(screen.getByRole('option', { name: /Office instance · Allowlisted peer/ })).toBeInTheDocument()
  })

  it('Should_GeneraliseTheBrowseButton_WhenAPeerExists', async () => {
    const user = userEvent.setup()
    renderWithProviders(<PlatformSyncPage />)
    await waitFor(() => {
      expect(screen.getByRole('button', { name: /browse a destination/i })).toBeInTheDocument()
    })
    await user.click(screen.getByRole('button', { name: /browse a destination/i }))
    await waitFor(() => {
      expect(screen.getByTestId('browse-destination-picker')).toBeInTheDocument()
    })
  })
})
