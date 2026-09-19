import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { renderWithProviders } from './test-utils'
import PlatformSyncPage from '../pages/admin/PlatformSyncPage'
import type { PlatformConnectionDto, VaultSyncStatusDto } from '../lib/types'

vi.mock('../lib/api-client', () => ({
  api: {
    getPlatformConnection: vi.fn(),
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

const mockGetConnection = vi.mocked(api.getPlatformConnection)
const mockListLinks = vi.mocked(api.listSyncLinks)
const mockGetHistory = vi.mocked(api.getPlatformSyncHistory)

const connectedDto: PlatformConnectionDto = {
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
    mockGetConnection.mockResolvedValue(connectedDto)
    mockListLinks.mockResolvedValue([link])
    mockGetHistory.mockResolvedValue([])
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
    await waitFor(() => {
      expect(screen.getByRole('heading', { name: 'Connection' })).toBeInTheDocument()
    })
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
    mockGetConnection.mockRejectedValue(new ApiError(404, 'Not found'))
    renderWithProviders(<PlatformSyncPage />)
    await waitFor(() => {
      expect(screen.getByText('Not Connected')).toBeInTheDocument()
    })
  })

  it('Should_CallGetHistoryWithDefaultLimit50_OnMount', async () => {
    renderWithProviders(<PlatformSyncPage />)
    await waitFor(() => {
      expect(mockGetHistory).toHaveBeenCalledWith(1, 50, undefined)
    })
  })
})
