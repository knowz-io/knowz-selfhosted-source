import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { renderWithProviders } from './test-utils'
import VaultLinksTable from '../components/platform-sync/VaultLinksTable'
import type { VaultSyncStatusDto } from '../lib/types'

vi.mock('../lib/api-client', () => ({
  api: {
    runSyncLink: vi.fn(),
    removeSyncLink: vi.fn(),
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

const mockRunSync = vi.mocked(api.runSyncLink)
const mockRemove = vi.mocked(api.removeSyncLink)

const linkA: VaultSyncStatusDto = {
  linkId: 'link-1',
  localVaultId: 'local-1',
  localVaultName: 'Engineering Docs',
  remoteVaultId: 'abcdef1234567890',
  platformApiUrl: 'https://api.knowz.io',
  status: 'Ok',
  lastSyncError: null,
  lastSyncCompletedAt: '2026-03-01T12:00:00Z',
  lastPullCursor: null,
  lastPushCursor: null,
  syncEnabled: true,
}

describe('VaultLinksTable', () => {
  beforeEach(() => {
    mockRunSync.mockResolvedValue({
      id: 'run-1',
      vaultSyncLinkId: 'link-1',
      userId: 'user-1',
      userEmail: null,
      operation: 'PullVault',
      direction: 'Pull',
      knowledgeId: null,
      itemCount: 0,
      bytesTransferred: 0,
      status: 'InProgress',
      errorMessage: null,
      startedAt: '2026-03-01T12:00:00Z',
      completedAt: null,
    })
    mockRemove.mockResolvedValue(undefined as unknown as void)
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('Should_RenderEmptyState_WhenNoLinks', () => {
    renderWithProviders(
      <VaultLinksTable links={[]} isLoading={false} onBrowsePlatform={() => {}} />,
    )
    expect(screen.getByText('No vault sync links configured.')).toBeInTheDocument()
  })

  it('Should_RenderColumnHeaders_WhenLinksExist', () => {
    renderWithProviders(
      <VaultLinksTable links={[linkA]} isLoading={false} onBrowsePlatform={() => {}} />,
    )
    expect(screen.getByText('Local Vault')).toBeInTheDocument()
    expect(screen.getByText('Knowz Cloud Vault')).toBeInTheDocument()
    expect(screen.getByText('Status')).toBeInTheDocument()
    expect(screen.getByText('Last Sync')).toBeInTheDocument()
    expect(screen.getByText('Actions')).toBeInTheDocument()
  })

  it('Should_RenderLinkRow_WithLocalVaultName', () => {
    renderWithProviders(
      <VaultLinksTable links={[linkA]} isLoading={false} onBrowsePlatform={() => {}} />,
    )
    expect(screen.getByText('Engineering Docs')).toBeInTheDocument()
  })

  it('Should_CallRunSyncPullOnly_WhenPullClicked', async () => {
    const user = userEvent.setup()
    renderWithProviders(
      <VaultLinksTable links={[linkA]} isLoading={false} onBrowsePlatform={() => {}} />,
    )
    await user.click(screen.getByRole('button', { name: /^pull$/i }))
    await waitFor(() => {
      expect(mockRunSync).toHaveBeenCalledWith('local-1', 'PullOnly')
    })
  })

  it('Should_CallRunSyncPushOnly_WhenPushClicked', async () => {
    const user = userEvent.setup()
    renderWithProviders(
      <VaultLinksTable links={[linkA]} isLoading={false} onBrowsePlatform={() => {}} />,
    )
    await user.click(screen.getByRole('button', { name: /^push$/i }))
    await waitFor(() => {
      expect(mockRunSync).toHaveBeenCalledWith('local-1', 'PushOnly')
    })
  })

  it('Should_CallRunSyncFull_WhenFullClicked', async () => {
    const user = userEvent.setup()
    renderWithProviders(
      <VaultLinksTable links={[linkA]} isLoading={false} onBrowsePlatform={() => {}} />,
    )
    await user.click(screen.getByRole('button', { name: /^full$/i }))
    await waitFor(() => {
      expect(mockRunSync).toHaveBeenCalledWith('local-1', 'Full')
    })
  })

  it('Should_ShowDeleteConfirmation_WhenDeleteClicked', async () => {
    const user = userEvent.setup()
    renderWithProviders(
      <VaultLinksTable links={[linkA]} isLoading={false} onBrowsePlatform={() => {}} />,
    )
    await user.click(screen.getByRole('button', { name: /remove link/i }))
    expect(screen.getByText('Remove Sync Link?')).toBeInTheDocument()
  })

  it('Should_CallRemoveSyncLink_WhenDeleteConfirmed', async () => {
    const user = userEvent.setup()
    renderWithProviders(
      <VaultLinksTable links={[linkA]} isLoading={false} onBrowsePlatform={() => {}} />,
    )
    await user.click(screen.getByRole('button', { name: /remove link/i }))
    // Modal now open — it has its own "Remove Link" button alongside the
    // original row-level "Remove link" button. The modal button is the one
    // that sits next to a Cancel button, so click Cancel target's sibling.
    const buttons = screen.getAllByRole('button', { name: /remove link/i })
    // The last matching button is the one rendered inside the modal.
    await user.click(buttons[buttons.length - 1])
    await waitFor(() => {
      expect(mockRemove).toHaveBeenCalledWith('local-1')
    })
  })

  it('Should_CallOnBrowsePlatform_WhenBrowseClicked', async () => {
    const user = userEvent.setup()
    const onBrowse = vi.fn()
    renderWithProviders(
      <VaultLinksTable links={[linkA]} isLoading={false} onBrowsePlatform={onBrowse} />,
    )
    await user.click(screen.getByRole('button', { name: /browse knowz cloud/i }))
    expect(onBrowse).toHaveBeenCalled()
  })

  it('Should_ShowLoadingSkeleton_WhenIsLoading', () => {
    const { container } = renderWithProviders(
      <VaultLinksTable links={[]} isLoading={true} onBrowsePlatform={() => {}} />,
    )
    expect(container.querySelectorAll('.animate-pulse').length).toBeGreaterThan(0)
  })
})
