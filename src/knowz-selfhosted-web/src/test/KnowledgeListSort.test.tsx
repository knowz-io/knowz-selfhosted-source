import { describe, it, expect, vi, beforeEach } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import { renderWithProviders } from './test-utils'
import KnowledgeListPage from '../pages/KnowledgeListPage'

// Mock the api-client module
const mockListKnowledge = vi.fn().mockResolvedValue({
  items: [
    {
      id: 'k-1',
      title: 'Newest Item',
      summary: 'Created most recently',
      type: 'Note',
      vaultId: null,
      vaultName: null,
      createdByUserId: null,
      createdByUserName: null,
      createdAt: '2026-04-08T12:00:00Z',
      updatedAt: '2026-04-08T12:00:00Z',
      isIndexed: true,
    },
    {
      id: 'k-2',
      title: 'Older Item',
      summary: 'Created earlier',
      type: 'Document',
      vaultId: null,
      vaultName: null,
      createdByUserId: null,
      createdByUserName: null,
      createdAt: '2026-04-01T12:00:00Z',
      updatedAt: '2026-04-01T12:00:00Z',
      isIndexed: false,
    },
  ],
  page: 1,
  pageSize: 20,
  totalItems: 2,
  totalPages: 1,
})

vi.mock('../lib/api-client', () => ({
  api: {
    listKnowledge: (...args: unknown[]) => mockListKnowledge(...args),
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

describe('KnowledgeListPage Sort Order', () => {
  beforeEach(() => {
    mockListKnowledge.mockClear()
    // Force 'list' view mode so the sort tests can assert table rows.
    localStorage.setItem('knowz-sh-view-mode:knowledge', 'list')
  })

  it('Should_DefaultToCreatedDescending_WhenNoSortParamsSpecified', async () => {
    renderWithProviders(<KnowledgeListPage />, { initialEntries: ['/knowledge'] })

    await waitFor(() => {
      expect(mockListKnowledge).toHaveBeenCalled()
    })

    // Check the call parameters — sort should default to 'created' with 'desc'
    const callArgs = mockListKnowledge.mock.calls[0][0]
    expect(callArgs.sort).toBe('created')
    expect(callArgs.sortDir).toBe('desc')
  })

  it('Should_DisplayItems_InMostRecentFirstOrder', async () => {
    renderWithProviders(<KnowledgeListPage />, { initialEntries: ['/knowledge'] })

    await waitFor(() => {
      expect(screen.getByText('Newest Item')).toBeInTheDocument()
    })

    // Both items should be visible
    expect(screen.getByText('Newest Item')).toBeInTheDocument()
    expect(screen.getByText('Older Item')).toBeInTheDocument()

    // Verify newest appears before older in the DOM
    const rows = screen.getAllByRole('row')
    const newestRow = rows.find(row => row.textContent?.includes('Newest Item'))
    const olderRow = rows.find(row => row.textContent?.includes('Older Item'))
    expect(newestRow).toBeDefined()
    expect(olderRow).toBeDefined()

    // Newest should be before Older in the DOM
    const newestIndex = rows.indexOf(newestRow!)
    const olderIndex = rows.indexOf(olderRow!)
    expect(newestIndex).toBeLessThan(olderIndex)
  })

  it('Should_PassSortParamsToApi_WhenVaultIdFilterApplied', async () => {
    renderWithProviders(<KnowledgeListPage />, {
      initialEntries: ['/knowledge?vaultId=vault-1'],
    })

    await waitFor(() => {
      expect(mockListKnowledge).toHaveBeenCalled()
    })

    // Even with vaultId filter, sort should still default to created desc
    const callArgs = mockListKnowledge.mock.calls[0][0]
    expect(callArgs.sort).toBe('created')
    expect(callArgs.sortDir).toBe('desc')
    expect(callArgs.vaultId).toBe('vault-1')
  })
})
