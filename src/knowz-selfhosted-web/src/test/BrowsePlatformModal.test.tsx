import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { renderWithProviders } from './test-utils'
import BrowsePlatformModal from '../components/platform-sync/BrowsePlatformModal'
import type {
  VaultSyncStatusDto,
  PlatformVaultDto,
  PlatformKnowledgeSummaryDto,
  PlatformKnowledgeDetailDto,
} from '../lib/types'

vi.mock('../lib/api-client', () => ({
  api: {
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

const mockListVaults = vi.mocked(api.listPlatformVaults)
const mockListKnowledge = vi.mocked(api.listPlatformKnowledge)
const mockGetKnowledge = vi.mocked(api.getPlatformKnowledge)
const mockPullItem = vi.mocked(api.pullPlatformItem)

const vaultA: PlatformVaultDto = {
  id: 'vault-a',
  name: 'Vault A',
  description: 'First vault',
  knowledgeCount: 3,
  updatedAt: '2026-03-01T00:00:00Z',
}

const knowledgeItem: PlatformKnowledgeSummaryDto = {
  id: 'k-1',
  title: 'First Item',
  summary: 'A summary',
  updatedAt: '2026-03-01T00:00:00Z',
  createdBy: 'alice',
}

const knowledgeDetail: PlatformKnowledgeDetailDto = {
  id: 'k-1',
  title: 'First Item',
  summary: 'A summary',
  content: 'This is the full content of the knowledge item.',
  tags: null,
  updatedAt: '2026-03-01T00:00:00Z',
  createdBy: 'alice',
}

const link: VaultSyncStatusDto = {
  linkId: 'link-1',
  localVaultId: 'local-vault-1',
  localVaultName: 'Local A',
  remoteVaultId: 'vault-a',
  platformApiUrl: 'https://api.knowz.io',
  status: 'Ok',
  lastSyncError: null,
  lastSyncCompletedAt: null,
  lastPullCursor: null,
  lastPushCursor: null,
  syncEnabled: true,
}

describe('BrowsePlatformModal', () => {
  beforeEach(() => {
    mockListVaults.mockResolvedValue({ vaults: [vaultA], totalCount: 1 })
    mockListKnowledge.mockResolvedValue({
      items: [knowledgeItem],
      page: 1,
      pageSize: 25,
      totalCount: 1,
    })
    mockGetKnowledge.mockResolvedValue(knowledgeDetail)
    mockPullItem.mockResolvedValue({
      success: true,
      outcome: 'Created',
      localKnowledgeId: 'local-k-1',
      message: null,
      duration: '00:00:01',
    })
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('Should_RenderHeader_WhenOpened', () => {
    renderWithProviders(<BrowsePlatformModal links={[link]} onClose={() => {}} />)
    expect(screen.getByText('Browse Knowz Cloud')).toBeInTheDocument()
  })

  it('Should_RenderVaultList_WhenVaultsLoad', async () => {
    renderWithProviders(<BrowsePlatformModal links={[link]} onClose={() => {}} />)
    await waitFor(() => {
      expect(screen.getByText('Vault A')).toBeInTheDocument()
      expect(screen.getByText('First vault')).toBeInTheDocument()
    })
  })

  it('Should_LoadKnowledge_WhenVaultClicked', async () => {
    const user = userEvent.setup()
    renderWithProviders(<BrowsePlatformModal links={[link]} onClose={() => {}} />)
    await waitFor(() => {
      expect(screen.getByText('Vault A')).toBeInTheDocument()
    })
    await user.click(screen.getByText('Vault A'))
    await waitFor(() => {
      expect(mockListKnowledge).toHaveBeenCalledWith('vault-a', 1, 25, undefined)
      expect(screen.getByText('First Item')).toBeInTheDocument()
    })
  })

  it('Should_DebounceSearch_BeforeCallingApi', async () => {
    const user = userEvent.setup()
    renderWithProviders(<BrowsePlatformModal links={[link]} onClose={() => {}} />)
    await waitFor(() => {
      expect(screen.getByText('Vault A')).toBeInTheDocument()
    })
    await user.click(screen.getByText('Vault A'))
    await waitFor(() => {
      expect(screen.getByPlaceholderText('Search items...')).toBeInTheDocument()
    })
    mockListKnowledge.mockClear()
    await user.type(screen.getByPlaceholderText('Search items...'), 'hello')
    // Debounced search triggers a single new call with the trimmed query.
    await waitFor(
      () => {
        expect(mockListKnowledge).toHaveBeenCalledWith('vault-a', 1, 25, 'hello')
      },
      { timeout: 2000 },
    )
  })

  it('Should_DefaultStrategyToSkip_WhenVaultSelected', async () => {
    const user = userEvent.setup()
    renderWithProviders(<BrowsePlatformModal links={[link]} onClose={() => {}} />)
    await waitFor(() => {
      expect(screen.getByText('Vault A')).toBeInTheDocument()
    })
    await user.click(screen.getByText('Vault A'))
    await waitFor(() => {
      expect(screen.getByText('First Item')).toBeInTheDocument()
    })
    const select = screen.getByRole('combobox') as HTMLSelectElement
    expect(select.value).toBe('Skip')
  })

  it('Should_RevealOverwriteCheckbox_WhenOverwriteSelected', async () => {
    const user = userEvent.setup()
    renderWithProviders(<BrowsePlatformModal links={[link]} onClose={() => {}} />)
    await waitFor(() => {
      expect(screen.getByText('Vault A')).toBeInTheDocument()
    })
    await user.click(screen.getByText('Vault A'))
    await waitFor(() => {
      expect(screen.getByText('First Item')).toBeInTheDocument()
    })
    const select = screen.getByRole('combobox')
    await user.selectOptions(select, 'Overwrite')
    expect(screen.getByText(/I understand this will replace local data/)).toBeInTheDocument()
  })

  it('Should_WarnToAcknowledge_WhenOverwritePullWithoutConfirm', async () => {
    const user = userEvent.setup()
    renderWithProviders(<BrowsePlatformModal links={[link]} onClose={() => {}} />)
    await waitFor(() => {
      expect(screen.getByText('Vault A')).toBeInTheDocument()
    })
    await user.click(screen.getByText('Vault A'))
    await waitFor(() => {
      expect(screen.getByText('First Item')).toBeInTheDocument()
    })
    // Select the item
    const checkboxes = screen.getAllByRole('checkbox')
    await user.click(checkboxes[0])
    await user.selectOptions(screen.getByRole('combobox'), 'Overwrite')
    // Pull without acknowledging
    await user.click(screen.getByRole('button', { name: /pull selected/i }))
    await waitFor(() => {
      expect(screen.getByText(/Please check the acknowledgement box/)).toBeInTheDocument()
    })
    expect(mockPullItem).not.toHaveBeenCalled()
  })

  it('Should_ShowPreview_WhenPreviewClicked', async () => {
    const user = userEvent.setup()
    renderWithProviders(<BrowsePlatformModal links={[link]} onClose={() => {}} />)
    await waitFor(() => {
      expect(screen.getByText('Vault A')).toBeInTheDocument()
    })
    await user.click(screen.getByText('Vault A'))
    await waitFor(() => {
      expect(screen.getByText('First Item')).toBeInTheDocument()
    })
    await user.click(screen.getByRole('button', { name: /preview/i }))
    await waitFor(() => {
      expect(
        screen.getByText('This is the full content of the knowledge item.'),
      ).toBeInTheDocument()
    })
  })

  it('Should_CallOnClose_WhenCloseClicked', async () => {
    const user = userEvent.setup()
    const onClose = vi.fn()
    renderWithProviders(<BrowsePlatformModal links={[link]} onClose={onClose} />)
    await user.click(screen.getByRole('button', { name: /close/i }))
    expect(onClose).toHaveBeenCalled()
  })

  it('Should_DisablePull_WhenNoItemsSelected', async () => {
    const user = userEvent.setup()
    renderWithProviders(<BrowsePlatformModal links={[link]} onClose={() => {}} />)
    await waitFor(() => {
      expect(screen.getByText('Vault A')).toBeInTheDocument()
    })
    await user.click(screen.getByText('Vault A'))
    await waitFor(() => {
      expect(screen.getByText('First Item')).toBeInTheDocument()
    })
    expect(screen.getByRole('button', { name: /pull selected/i })).toBeDisabled()
  })
})
