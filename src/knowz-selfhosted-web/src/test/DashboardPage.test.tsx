import { describe, it, expect, vi, beforeEach } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import { renderWithProviders } from './test-utils'
import DashboardPage from '../pages/DashboardPage'

vi.mock('../lib/api-client', () => ({
  api: {
    listKnowledge: vi.fn().mockResolvedValue({
      items: [
        {
          id: 'k-home-1',
          title: 'Home Knowledge Card',
          summary: 'Shown on the knowledge home',
          type: 'Note',
          vaultId: null,
          vaultName: 'Library',
          createdByUserId: null,
          createdByUserName: null,
          createdAt: '2026-04-10T12:00:00Z',
          updatedAt: '2026-04-10T12:00:00Z',
          isIndexed: true,
        },
      ],
      page: 1,
      pageSize: 20,
      totalItems: 1,
      totalPages: 1,
    }),
    listVaults: vi.fn().mockResolvedValue({ vaults: [] }),
    getKnowledgeCreators: vi.fn().mockResolvedValue([]),
    getStats: vi.fn(),
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

describe('VERIFY-L4 knowledge home', () => {
  beforeEach(() => {
    localStorage.clear()
  })

  it('Should_ShowKnowledgeCards_NotStatsDashboard_WhenRootRenders', async () => {
    renderWithProviders(<DashboardPage />, { initialEntries: ['/'] })

    await waitFor(() => {
      expect(screen.getByText('Home Knowledge Card')).toBeInTheDocument()
    })
    expect(screen.getByTestId('knowledge-list-view-grid')).toBeInTheDocument()
    expect(screen.queryByText(/indexed and ready to explore/i)).not.toBeInTheDocument()
    expect(screen.queryByText(/content distribution/i)).not.toBeInTheDocument()
    expect(screen.queryByText(/knowledge momentum/i)).not.toBeInTheDocument()
  })
})
