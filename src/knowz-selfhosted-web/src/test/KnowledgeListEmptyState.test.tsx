import { describe, it, expect, vi, beforeEach } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import { renderWithProviders } from './test-utils'
import KnowledgeListPage from '../pages/KnowledgeListPage'

vi.mock('../lib/api-client', () => ({
  api: {
    listKnowledge: vi.fn().mockResolvedValue({
      items: [],
      page: 1,
      pageSize: 20,
      totalItems: 0,
      totalPages: 0,
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

describe('VERIFY-L4 empty knowledge home', () => {
  beforeEach(() => {
    localStorage.clear()
  })

  it('Should_ShowNewKnowledgeCta_WhenLibraryIsEmpty', async () => {
    renderWithProviders(<KnowledgeListPage />, { initialEntries: ['/knowledge'] })

    await waitFor(() => {
      expect(screen.getByTestId('knowledge-empty-cta')).toBeInTheDocument()
    })
    const cta = screen.getByTestId('knowledge-empty-cta')
    expect(cta).toHaveAttribute('href', '/knowledge/new')
    expect(cta.textContent).toMatch(/New Knowledge/)
    expect(screen.queryByText(/create your first item/i)).not.toBeInTheDocument()
  })
})
