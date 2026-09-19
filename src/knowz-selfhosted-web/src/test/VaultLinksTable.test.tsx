import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { renderWithProviders } from './test-utils'
import VaultLinksTable from '../components/platform-sync/VaultLinksTable'
import type { VaultSyncResult, VaultSyncStatusDto } from '../lib/types'

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

import { api, ApiError } from '../lib/api-client'

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

function syncResult(over: Partial<VaultSyncResult> = {}): VaultSyncResult {
  return {
    success: true,
    direction: 'Full',
    pullAccepted: 0,
    pullSkipped: 0,
    pushAccepted: 0,
    pushSkipped: 0,
    tombstonesApplied: 0,
    details: [],
    error: null,
    duration: '00:00:01.2340000',
    partial: false,
    ...over,
  }
}

describe('VaultLinksTable', () => {
  beforeEach(() => {
    mockRunSync.mockResolvedValue(syncResult())
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

// ---------- UI_SH_DestinationsPolish : S5 run-result banner ----------
describe('VaultLinksTable — run-result banner (S5, VERIFY-SH-2..4)', () => {
  beforeEach(() => {
    mockRemove.mockResolvedValue(undefined as unknown as void)
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  const renderTable = () =>
    renderWithProviders(
      <VaultLinksTable links={[linkA]} isLoading={false} onBrowsePlatform={() => {}} />,
    )

  it('Should_ShowPullCountsOnly_WhenPullOnlyRunSucceeds', async () => {
    const user = userEvent.setup()
    mockRunSync.mockResolvedValue(
      syncResult({ direction: 'PullOnly', pullAccepted: 7, pullSkipped: 2 }),
    )
    renderTable()
    await user.click(screen.getByRole('button', { name: /^pull$/i }))

    const banner = await screen.findByTestId('sync-run-banner')
    expect(banner.textContent).toContain('7 accepted')
    expect(banner.textContent).toContain('2 skipped')
    expect(banner.textContent).not.toMatch(/Push:/)
  })

  it('Should_ShowPushCountsOnly_WhenPushOnlyRunSucceeds', async () => {
    const user = userEvent.setup()
    mockRunSync.mockResolvedValue(
      syncResult({ direction: 'PushOnly', pushAccepted: 4, pushSkipped: 1 }),
    )
    renderTable()
    await user.click(screen.getByRole('button', { name: /^push$/i }))

    const banner = await screen.findByTestId('sync-run-banner')
    expect(banner.textContent).toContain('4 accepted')
    expect(banner.textContent).toContain('1 skipped')
    expect(banner.textContent).not.toMatch(/Pull:/)
  })

  it('Should_ShowBothHalves_WhenFullRunSucceeds', async () => {
    const user = userEvent.setup()
    mockRunSync.mockResolvedValue(
      syncResult({
        direction: 'Full',
        pullAccepted: 3,
        pullSkipped: 0,
        pushAccepted: 5,
        pushSkipped: 6,
      }),
    )
    renderTable()
    await user.click(screen.getByRole('button', { name: /^full$/i }))

    const banner = await screen.findByTestId('sync-run-banner')
    expect(banner.textContent).toMatch(/Pull: 3 accepted, 0 skipped/)
    expect(banner.textContent).toMatch(/Push: 5 accepted, 6 skipped/)
  })

  it('Should_DismissBanner_WhenDismissClicked', async () => {
    const user = userEvent.setup()
    mockRunSync.mockResolvedValue(syncResult({ direction: 'PullOnly', pullAccepted: 7 }))
    renderTable()
    await user.click(screen.getByRole('button', { name: /^pull$/i }))
    await screen.findByTestId('sync-run-banner')

    await user.click(screen.getByRole('button', { name: /dismiss/i }))
    expect(screen.queryByTestId('sync-run-banner')).not.toBeInTheDocument()
  })

  it('Should_RenderDetailsVerbatim_WhenServerReturnsDetails', async () => {
    const user = userEvent.setup()
    mockRunSync.mockResolvedValue(
      syncResult({
        direction: 'PullOnly',
        pullAccepted: 1,
        details: ['Skipped 1 item with an unsupported type.'],
      }),
    )
    renderTable()
    await user.click(screen.getByRole('button', { name: /^pull$/i }))

    const banner = await screen.findByTestId('sync-run-banner')
    expect(banner.textContent).toContain('Skipped 1 item with an unsupported type.')
  })

  it('Should_ShowAmberPartialBanner_WhenRunHitsItemCap', async () => {
    const user = userEvent.setup()
    mockRunSync.mockResolvedValue(
      syncResult({ direction: 'PullOnly', pullAccepted: 100, partial: true }),
    )
    renderTable()
    await user.click(screen.getByRole('button', { name: /^pull$/i }))

    const banner = await screen.findByTestId('sync-run-banner')
    expect(banner.textContent).toContain('100-item limit')
    expect(banner.textContent).toContain('Run again')
    expect(banner.textContent).toContain('100 accepted')
    expect(banner.className).toMatch(/amber/)
  })

  it('Should_ShowRateLimitCopy_WhenServerReturns429ForTheHourlyQuota', async () => {
    const user = userEvent.setup()
    // Verbatim `VaultSyncOrchestrator.cs:109` (RateLimitReason.HourlyQuotaExceeded).
    mockRunSync.mockRejectedValue(
      new ApiError(429, 'Rate limit exceeded (10 runs per hour).'),
    )
    renderTable()
    await user.click(screen.getByRole('button', { name: /^pull$/i }))

    const banner = await screen.findByTestId('sync-run-banner')
    expect(banner.textContent).toContain('10 sync runs per hour')
    expect(banner.className).toMatch(/amber/)
  })

  it('Should_ShowServerMessage_WhenServerReturns422', async () => {
    const user = userEvent.setup()
    mockRunSync.mockRejectedValue(new ApiError(422, 'Remote refused'))
    renderTable()
    await user.click(screen.getByRole('button', { name: /^pull$/i }))

    const banner = await screen.findByTestId('sync-run-banner')
    expect(banner.textContent).toContain('Remote refused')
    expect(banner.className).toMatch(/red/)
  })

  it('Should_ShowUserSafeCopy_WhenAnUnexpectedErrorIsThrown', async () => {
    const user = userEvent.setup()
    mockRunSync.mockRejectedValue(new Error('relation "sync_runs" does not exist'))
    renderTable()
    await user.click(screen.getByRole('button', { name: /^pull$/i }))

    const banner = await screen.findByTestId('sync-run-banner')
    expect(banner.textContent).toContain('Something went wrong')
    expect(banner.textContent).not.toContain('relation')
  })

  it('Should_ShowServerError_WhenBodySaysSuccessFalse', async () => {
    const user = userEvent.setup()
    mockRunSync.mockResolvedValue(
      syncResult({ success: false, direction: 'PullOnly', error: 'Vault link is disabled' }),
    )
    renderTable()
    await user.click(screen.getByRole('button', { name: /^pull$/i }))

    const banner = await screen.findByTestId('sync-run-banner')
    expect(banner.textContent).toContain('Vault link is disabled')
    expect(banner.className).toMatch(/red/)
  })
})

// ---------- UI_SH_DestinationsPolish : S6 caps + S7 modal honesty ----------
describe('VaultLinksTable — visible caps and honest removal copy (S6, S7)', () => {
  it('Should_ShowCapsHelperLine_AboveTheTable', () => {
    renderWithProviders(
      <VaultLinksTable links={[linkA]} isLoading={false} onBrowsePlatform={() => {}} />,
    )
    const helper = screen.getByTestId('sync-caps-helper')
    expect(helper.textContent).toContain('100 items per run')
    expect(helper.textContent).toContain('10 runs per hour')
    expect(helper.textContent).toMatch(/manual/i)
  })

  it('Should_DescribeDirectionInButtonTitles', () => {
    renderWithProviders(
      <VaultLinksTable links={[linkA]} isLoading={false} onBrowsePlatform={() => {}} />,
    )
    expect(screen.getByRole('button', { name: /^pull$/i }).getAttribute('title')).toBe(
      'Pull: Knowz Cloud → this instance',
    )
    expect(screen.getByRole('button', { name: /^push$/i }).getAttribute('title')).toBe(
      'Push: this instance → Knowz Cloud',
    )
    expect(screen.getByRole('button', { name: /^full$/i }).getAttribute('title')).toBe(
      'Full: pull then push',
    )
  })

  it('Should_NotPromiseAutomaticPulls_InRemoveLinkModal', async () => {
    const user = userEvent.setup()
    renderWithProviders(
      <VaultLinksTable links={[linkA]} isLoading={false} onBrowsePlatform={() => {}} />,
    )
    await user.click(screen.getByRole('button', { name: /remove link/i }))
    const body = screen.getByTestId('remove-link-body')
    expect(body.textContent).toContain('Local data is not deleted.')
    expect(body.textContent).toContain('You can link the vault again later.')
    expect(body.textContent).not.toMatch(/automatic/i)
  })
})

// ---------- Gate #3 gap round 1 : honest 429 reasons + concurrency lock ----------
describe('VaultLinksTable — 429 reasons and the one-run-at-a-time rule (REQUIRED-1)', () => {
  const linkB: VaultSyncStatusDto = {
    ...linkA,
    linkId: 'link-2',
    localVaultId: 'local-2',
    localVaultName: 'Design Docs',
  }

  afterEach(() => {
    vi.clearAllMocks()
  })

  const renderTwoLinks = () =>
    renderWithProviders(
      <VaultLinksTable
        links={[linkA, linkB]}
        isLoading={false}
        onBrowsePlatform={() => {}}
      />,
    )

  it('Should_NameTheConcurrentRun_When429IsAConcurrencyRefusal', async () => {
    const user = userEvent.setup()
    // Verbatim `VaultSyncOrchestrator.cs:110` (RateLimitReason.ConcurrentRunInProgress).
    mockRunSync.mockRejectedValue(
      new ApiError(429, 'Another sync is already in progress for this tenant.'),
    )
    renderWithProviders(
      <VaultLinksTable links={[linkA]} isLoading={false} onBrowsePlatform={() => {}} />,
    )
    await user.click(screen.getByRole('button', { name: /^pull$/i }))

    const banner = await screen.findByTestId('sync-run-banner')
    expect(banner.textContent).toContain('already running')
    expect(banner.textContent).not.toContain('10 sync runs per hour')
    expect(banner.className).toMatch(/amber/)
  })

  it('Should_ShowTheServerSentence_WhenA429ReasonIsNeitherKnownShape', async () => {
    const user = userEvent.setup()
    // Verbatim `VaultSyncOrchestrator.cs:111` (RateLimitReason.ItemLimitExceeded).
    mockRunSync.mockRejectedValue(
      new ApiError(429, 'Run exceeds the 100-item-per-run limit.'),
    )
    renderWithProviders(
      <VaultLinksTable links={[linkA]} isLoading={false} onBrowsePlatform={() => {}} />,
    )
    await user.click(screen.getByRole('button', { name: /^pull$/i }))

    const banner = await screen.findByTestId('sync-run-banner')
    expect(banner.textContent).toContain('Run exceeds the 100-item-per-run limit.')
    expect(banner.textContent).not.toContain('10 sync runs per hour')
    expect(banner.className).toMatch(/amber/)
  })

  it('Should_DisableEveryRunButton_WhileAnyRunIsInFlight', async () => {
    const user = userEvent.setup()
    let release: (result: VaultSyncResult) => void = () => {}
    mockRunSync.mockImplementation(
      () =>
        new Promise<VaultSyncResult>((resolve) => {
          release = resolve
        }),
    )
    renderTwoLinks()

    const pulls = screen.getAllByRole('button', { name: /^pull$/i })
    const pushes = screen.getAllByRole('button', { name: /^push$/i })
    const fulls = screen.getAllByRole('button', { name: /^full$/i })
    expect(pulls[1]).toBeEnabled()

    await user.click(pulls[0])

    // The server allows one run per tenant, so the other link's actions must be
    // unavailable too — not just the row that was clicked.
    await waitFor(() => expect(pulls[1]).toBeDisabled())
    expect(pushes[1]).toBeDisabled()
    expect(fulls[1]).toBeDisabled()
    expect(pulls[0]).toBeDisabled()

    release(syncResult({ direction: 'PullOnly', pullAccepted: 1 }))
    await screen.findByTestId('sync-run-banner')
    await waitFor(() => expect(pulls[1]).toBeEnabled())
  })
})

// ---------- Gate #3 gap round 1 : banner hygiene (OPTIONAL-2, -3, -5) ----------
describe('VaultLinksTable — banner safety and announcement (OPTIONAL-2, -3, -5)', () => {
  afterEach(() => {
    vi.clearAllMocks()
  })

  const renderTable = () =>
    renderWithProviders(
      <VaultLinksTable links={[linkA]} isLoading={false} onBrowsePlatform={() => {}} />,
    )

  it('Should_ReplaceInternalText_When422MessageLooksLikeMachineOutput', async () => {
    const user = userEvent.setup()
    mockRunSync.mockRejectedValue(
      new ApiError(422, 'Npgsql exception at System.Data.ReadAsync(...)'),
    )
    renderTable()
    await user.click(screen.getByRole('button', { name: /^pull$/i }))

    const banner = await screen.findByTestId('sync-run-banner')
    expect(banner.textContent).toContain('The run did not complete.')
    expect(banner.textContent).not.toContain('Npgsql')
    expect(banner.textContent).not.toContain('System.Data')
  })

  it('Should_ReplaceInternalText_WhenSuccessFalseBodyLooksLikeMachineOutput', async () => {
    const user = userEvent.setup()
    mockRunSync.mockResolvedValue(
      syncResult({
        success: false,
        direction: 'PullOnly',
        error: 'DbUpdateException: relation "sync_runs" does not exist',
      }),
    )
    renderTable()
    await user.click(screen.getByRole('button', { name: /^pull$/i }))

    const banner = await screen.findByTestId('sync-run-banner')
    expect(banner.textContent).toContain('The run did not complete.')
    expect(banner.textContent).not.toContain('DbUpdateException')
  })

  it('Should_KeepProductShapedText_When422MessageIsSafe', async () => {
    const user = userEvent.setup()
    mockRunSync.mockRejectedValue(new ApiError(422, 'Sync is disabled for this vault'))
    renderTable()
    await user.click(screen.getByRole('button', { name: /^pull$/i }))

    const banner = await screen.findByTestId('sync-run-banner')
    expect(banner.textContent).toContain('Sync is disabled for this vault')
  })

  it('Should_AnnounceFailuresAsAlerts_AndOutcomesAsStatus', async () => {
    const user = userEvent.setup()
    mockRunSync.mockRejectedValue(new ApiError(422, 'Remote refused'))
    const { unmount } = renderTable()
    await user.click(screen.getByRole('button', { name: /^pull$/i }))
    expect((await screen.findByTestId('sync-run-banner')).getAttribute('role')).toBe(
      'alert',
    )
    unmount()

    mockRunSync.mockResolvedValue(syncResult({ direction: 'PullOnly', pullAccepted: 1 }))
    renderTable()
    await user.click(screen.getByRole('button', { name: /^pull$/i }))
    expect((await screen.findByTestId('sync-run-banner')).getAttribute('role')).toBe(
      'status',
    )
  })

  it('Should_ClearRunBanner_WhenARemovalStarts', async () => {
    const user = userEvent.setup()
    mockRunSync.mockResolvedValue(syncResult({ direction: 'PullOnly', pullAccepted: 1 }))
    mockRemove.mockResolvedValue(undefined as unknown as void)
    renderTable()
    await user.click(screen.getByRole('button', { name: /^pull$/i }))
    await screen.findByTestId('sync-run-banner')

    await user.click(screen.getByRole('button', { name: /remove link/i }))
    const confirmButtons = screen.getAllByRole('button', { name: /remove link/i })
    await user.click(confirmButtons[confirmButtons.length - 1])

    await waitFor(() =>
      expect(screen.queryByTestId('sync-run-banner')).not.toBeInTheDocument(),
    )
  })
})
