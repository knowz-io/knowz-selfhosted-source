import { describe, it, expect, vi, afterEach } from 'vitest'
import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { renderWithProviders } from './test-utils'
import SyncHistoryTable from '../components/platform-sync/SyncHistoryTable'
import type { PlatformSyncRunDto, VaultSyncStatusDto } from '../lib/types'

const HISTORY_LIMITS = [25, 50, 100, 500] as const

const succeededRun: PlatformSyncRunDto = {
  id: 'run-1',
  vaultSyncLinkId: 'link-1',
  userId: 'user-abc',
  userEmail: 'alice@example.com',
  operation: 'PullVault',
  direction: 'Pull',
  knowledgeId: null,
  itemCount: 10,
  bytesTransferred: 1024,
  status: 'Succeeded',
  errorMessage: null,
  startedAt: '2026-03-01T12:00:00Z',
  completedAt: '2026-03-01T12:00:05Z',
}

const failedRun: PlatformSyncRunDto = {
  id: 'run-2',
  vaultSyncLinkId: 'link-1',
  userId: 'user-abc',
  userEmail: 'alice@example.com',
  operation: 'PushVault',
  direction: 'Push',
  knowledgeId: null,
  itemCount: 0,
  bytesTransferred: 0,
  status: 'Failed',
  errorMessage: 'Remote server returned 500',
  startedAt: '2026-03-01T13:00:00Z',
  completedAt: '2026-03-01T13:00:02Z',
}

const link: VaultSyncStatusDto = {
  linkId: 'link-1',
  localVaultId: 'local-1',
  localVaultName: 'Engineering Docs',
  remoteVaultId: 'remote-1',
  platformApiUrl: 'https://api.knowz.io',
  status: 'Ok',
  lastSyncError: null,
  lastSyncCompletedAt: null,
  lastPullCursor: null,
  lastPushCursor: null,
  syncEnabled: true,
}

function renderTable(
  props: Partial<React.ComponentProps<typeof SyncHistoryTable>> = {},
) {
  const defaults: React.ComponentProps<typeof SyncHistoryTable> = {
    history: [],
    isLoading: false,
    isFetching: false,
    onRefresh: vi.fn(),
    limit: 50,
    limitOptions: HISTORY_LIMITS,
    onLimitChange: vi.fn(),
    linkFilter: '',
    onLinkFilterChange: vi.fn(),
    links: [link],
  }
  return renderWithProviders(<SyncHistoryTable {...defaults} {...props} />)
}

describe('SyncHistoryTable', () => {
  afterEach(() => {
    vi.clearAllMocks()
  })

  it('Should_RenderEmptyState_WhenNoHistory', () => {
    renderTable({ history: [] })
    expect(screen.getByText('No sync history yet.')).toBeInTheDocument()
  })

  it('Should_RenderRows_WhenHistoryProvided', () => {
    renderTable({ history: [succeededRun] })
    expect(screen.getByText('PullVault')).toBeInTheDocument()
    expect(screen.getByText('Succeeded')).toBeInTheDocument()
    expect(screen.getByText('10')).toBeInTheDocument()
    expect(screen.getByText('alice@example.com')).toBeInTheDocument()
  })

  it('Should_CallOnLimitChange_WhenLimitSelectChanged', async () => {
    const user = userEvent.setup()
    const onLimitChange = vi.fn()
    renderTable({ onLimitChange })
    const showSelect = screen.getByLabelText(/show/i)
    await user.selectOptions(showSelect, '100')
    expect(onLimitChange).toHaveBeenCalledWith(100)
  })

  it('Should_PopulateVaultFilter_FromLinks', () => {
    renderTable()
    const vaultFilter = screen.getByLabelText(/vault/i) as HTMLSelectElement
    const options = Array.from(vaultFilter.querySelectorAll('option')).map(
      (o) => o.textContent,
    )
    expect(options).toContain('All')
    expect(options).toContain('Engineering Docs')
  })

  it('Should_CallOnLinkFilterChange_WhenVaultFilterChanged', async () => {
    const user = userEvent.setup()
    const onLinkFilterChange = vi.fn()
    renderTable({ onLinkFilterChange })
    const vaultFilter = screen.getByLabelText(/vault/i)
    await user.selectOptions(vaultFilter, 'link-1')
    expect(onLinkFilterChange).toHaveBeenCalledWith('link-1')
  })

  it('Should_CallOnRefresh_WhenRefreshClicked', async () => {
    const user = userEvent.setup()
    const onRefresh = vi.fn()
    renderTable({ onRefresh })
    await user.click(screen.getByRole('button', { name: /refresh/i }))
    expect(onRefresh).toHaveBeenCalled()
  })

  it('Should_ExpandErrorRow_WhenFailedRowClicked', async () => {
    const user = userEvent.setup()
    renderTable({ history: [failedRun] })
    // Error row is hidden until clicked
    expect(screen.queryByText('Remote server returned 500')).not.toBeInTheDocument()
    await user.click(screen.getByText('PushVault'))
    expect(screen.getByText('Remote server returned 500')).toBeInTheDocument()
  })

  it('Should_ShowLoadingSkeleton_WhenIsLoading', () => {
    const { container } = renderTable({ isLoading: true })
    expect(container.querySelectorAll('.animate-pulse').length).toBeGreaterThan(0)
  })
})
