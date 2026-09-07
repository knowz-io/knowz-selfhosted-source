import { describe, it, expect, vi, beforeEach } from 'vitest'
import { screen, waitFor, fireEvent } from '@testing-library/react'
import { renderWithProviders } from './test-utils'
import KnowledgeListPage from '../pages/KnowledgeListPage'

vi.mock('../lib/api-client', () => ({
  api: {
    listKnowledge: vi.fn().mockResolvedValue({
      items: [
        {
          id: 'k-1',
          title: 'Alpha Item',
          summary: 'First item',
          type: 'Note',
          vaultId: null,
          vaultName: 'My Vault',
          createdByUserId: null,
          createdByUserName: null,
          createdAt: '2026-04-10T12:00:00Z',
          updatedAt: '2026-04-10T12:00:00Z',
          isIndexed: true,
        },
        {
          id: 'k-2',
          title: 'Beta Item',
          summary: null,
          type: 'Document',
          vaultId: null,
          vaultName: null,
          createdByUserId: null,
          createdByUserName: null,
          createdAt: '2026-04-09T12:00:00Z',
          updatedAt: '2026-04-09T12:00:00Z',
          isIndexed: false,
        },
      ],
      page: 1,
      pageSize: 20,
      totalItems: 2,
      totalPages: 1,
    }),
    listVaults: vi.fn().mockResolvedValue({ vaults: [] }),
    getKnowledgeCreators: vi.fn().mockResolvedValue([]),
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

describe('KnowledgeListPage ViewMode rendering', () => {
  beforeEach(() => {
    localStorage.clear()
  })

  it('Should_RenderGridView_WhenViewModeIsGrid', async () => {
    renderWithProviders(<KnowledgeListPage />, { initialEntries: ['/knowledge'] })
    await waitFor(() => expect(screen.getByText('Alpha Item')).toBeInTheDocument())
    expect(screen.getByTestId('knowledge-list-view-grid')).toBeInTheDocument()
  })

  it('Should_RenderCompactView_WhenViewModeIsCompact', async () => {
    localStorage.setItem('knowz-sh-view-mode:knowledge', 'compact')
    renderWithProviders(<KnowledgeListPage />, { initialEntries: ['/knowledge'] })
    await waitFor(() => expect(screen.getByText('Alpha Item')).toBeInTheDocument())
    expect(screen.getByTestId('knowledge-list-view-compact')).toBeInTheDocument()
  })

  it('Should_RenderListView_WhenViewModeIsList', async () => {
    localStorage.setItem('knowz-sh-view-mode:knowledge', 'list')
    renderWithProviders(<KnowledgeListPage />, { initialEntries: ['/knowledge'] })
    await waitFor(() => expect(screen.getByText('Alpha Item')).toBeInTheDocument())
    expect(screen.getByTestId('knowledge-list-view-list')).toBeInTheDocument()
  })

  it('Should_RenderGalleryView_WhenViewModeIsGallery', async () => {
    localStorage.setItem('knowz-sh-view-mode:knowledge', 'gallery')
    renderWithProviders(<KnowledgeListPage />, { initialEntries: ['/knowledge'] })
    await waitFor(() => expect(screen.getByText('Alpha Item')).toBeInTheDocument())
    expect(screen.getByTestId('knowledge-list-view-gallery')).toBeInTheDocument()
  })

  it('Should_RenderCodeView_WhenViewModeIsCode', async () => {
    localStorage.setItem('knowz-sh-view-mode:knowledge', 'code')
    renderWithProviders(<KnowledgeListPage />, { initialEntries: ['/knowledge'] })
    await waitFor(() => expect(screen.getByText('Alpha Item')).toBeInTheDocument())
    expect(screen.getByTestId('knowledge-list-view-code')).toBeInTheDocument()
  })

  it('Should_ShowViewModeToggle_InToolbar', async () => {
    renderWithProviders(<KnowledgeListPage />, { initialEntries: ['/knowledge'] })
    await waitFor(() => expect(screen.getByTestId('view-mode-selector')).toBeInTheDocument())
  })

  it('Should_SwitchToCompact_WhenToggledViaDropdown', async () => {
    renderWithProviders(<KnowledgeListPage />, { initialEntries: ['/knowledge'] })
    await waitFor(() => expect(screen.getByText('Alpha Item')).toBeInTheDocument())

    fireEvent.click(screen.getByRole('button', { name: /view mode: grid/i }))
    fireEvent.click(screen.getByTestId('view-mode-compact'))

    expect(screen.getByTestId('knowledge-list-view-compact')).toBeInTheDocument()
  })
})
