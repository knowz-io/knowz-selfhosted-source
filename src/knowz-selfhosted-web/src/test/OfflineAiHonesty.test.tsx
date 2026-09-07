import { describe, it, expect, vi, beforeEach, beforeAll } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { renderWithProviders } from './test-utils'
import ChatPage from '../pages/ChatPage'
import AskPage from '../pages/AskPage'
import FilesPage from '../pages/FilesPage'

vi.mock('../lib/api-client', () => ({
  api: {
    listVaults: vi.fn(),
    chatStream: vi.fn(),
    askStream: vi.fn(),
    listFiles: vi.fn(),
    uploadFile: vi.fn(),
    deleteFile: vi.fn(),
    downloadFile: vi.fn(),
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

const NOTICE = /AI is not configured on this instance/i

// jsdom has no layout engine; ChatPage auto-scrolls on every render.
beforeAll(() => {
  Element.prototype.scrollIntoView = vi.fn()
})

describe('SH_OfflineAiHonesty — Chat surface (VERIFY-O7, O9)', () => {
  beforeEach(() => {
    localStorage.clear()
    vi.mocked(api.listVaults).mockResolvedValue({ vaults: [] } as never)
    // NoOp backend: sources preamble carries aiUnavailable, then zero tokens, then done.
    vi.mocked(api.chatStream).mockImplementation(async (_req: never, onEvent: never) => {
      const emit = onEvent as unknown as (e: unknown) => void
      emit({ type: 'sources', sources: [], confidence: 0, aiUnavailable: true })
      emit({ type: 'done' })
    })
  })

  it('Should_RenderNotice_And_NotPersistToHistory_WhenAiUnavailable', async () => {
    const user = userEvent.setup()
    renderWithProviders(<ChatPage />)

    await user.type(screen.getByPlaceholderText(/Ask a question/i), 'hello')
    await user.keyboard('{Enter}')

    await waitFor(() => expect(screen.getByTestId('ai-unavailable-notice')).toBeInTheDocument())
    expect(screen.getByTestId('ai-unavailable-notice').textContent).toMatch(NOTICE)

    // VERIFY-O7: no confidence theatre and no assistant bubble persisted.
    expect(screen.queryByText(/% confidence/)).toBeNull()
    const stored = JSON.parse(localStorage.getItem('knowz-conversations') ?? '[]')
    const messages = stored.flatMap((c: { messages: unknown[] }) => c.messages ?? [])
    expect(messages.filter((m: { role: string }) => m.role === 'assistant')).toHaveLength(0)
  })

  it('Should_NotOfferEnabledResearchToggle_WhenAiUnavailable', async () => {
    const user = userEvent.setup()
    renderWithProviders(<ChatPage />)

    await user.type(screen.getByPlaceholderText(/Ask a question/i), 'hello')
    await user.keyboard('{Enter}')
    await waitFor(() => expect(screen.getByTestId('ai-unavailable-notice')).toBeInTheDocument())

    const research = screen.queryByRole('button', { name: /research/i })
    expect(research === null || (research as HTMLButtonElement).disabled).toBe(true)

    // The surface stays alive: the composer is still usable.
    expect(screen.getByPlaceholderText(/Ask a question/i)).not.toBeDisabled()
  })
})

describe('SH_OfflineAiHonesty — Ask surface (VERIFY-O8)', () => {
  beforeEach(() => {
    vi.mocked(api.askStream).mockImplementation(async (_req: never, onEvent: never) => {
      const emit = onEvent as unknown as (e: unknown) => void
      emit({ type: 'sources', sources: [], confidence: 0, aiUnavailable: true })
      emit({ type: 'done' })
    })
  })

  it('Should_RenderNotice_And_NoAnswerCard_WhenAiUnavailable', async () => {
    const user = userEvent.setup()
    renderWithProviders(<AskPage />)

    await user.type(screen.getByPlaceholderText(/Ask a question/i), 'hello')
    await user.click(screen.getByRole('button', { name: /^Ask$/i }))

    await waitFor(() => expect(screen.getByTestId('ai-unavailable-notice')).toBeInTheDocument())
    expect(screen.getByTestId('ai-unavailable-notice').textContent).toMatch(NOTICE)
    expect(screen.queryByRole('heading', { name: /^Answer$/i })).toBeNull()
    expect(screen.queryByText(/^Sources \(/)).toBeNull()
    expect(screen.queryByText(/% confidence/)).toBeNull()
  })
})

describe('SH_OfflineAiHonesty — Files provider badge (VERIFY-O10)', () => {
  const baseFile = {
    id: 'file-1',
    fileName: 'report.pdf',
    contentType: 'application/pdf',
    sizeBytes: 1024,
    blobMigrationPending: false,
    knowledgeId: 'knowledge-1',
    knowledgeTitle: 'Quarterly report',
    textExtractionStatus: 2,
    createdAt: '2026-01-15T10:00:00Z',
    updatedAt: '2026-01-15T10:00:00Z',
  }

  it('Should_HideProviderBadge_WhenProviderIsNoOp', async () => {
    vi.mocked(api.listFiles).mockResolvedValue({
      items: [{ ...baseFile, attachmentAIProvider: 'NoOp' }],
      page: 1,
      pageSize: 20,
      totalItems: 1,
      totalPages: 1,
    } as never)

    renderWithProviders(<FilesPage />)
    await waitFor(() => expect(screen.getAllByText('report.pdf').length).toBeGreaterThan(0))
    await userEvent.setup().click(screen.getAllByText('report.pdf')[0])
    await waitFor(() => expect(screen.getAllByTestId('extraction-status-badge').length).toBeGreaterThan(0))

    expect(screen.queryAllByTestId('provider-badge')).toHaveLength(0)
    expect(screen.queryByText('NoOp')).toBeNull()
  })

  it('Should_RenderMappedLabel_NeverRawId_ForKnownProvider', async () => {
    vi.mocked(api.listFiles).mockResolvedValue({
      items: [{ ...baseFile, attachmentAIProvider: 'AzureOpenAI' }],
      page: 1,
      pageSize: 20,
      totalItems: 1,
      totalPages: 1,
    } as never)

    renderWithProviders(<FilesPage />)
    await waitFor(() => expect(screen.getAllByText('report.pdf').length).toBeGreaterThan(0))
    await userEvent.setup().click(screen.getAllByText('report.pdf')[0])

    await waitFor(() => expect(screen.getAllByTestId('provider-badge').length).toBeGreaterThan(0))
    for (const badge of screen.getAllByTestId('provider-badge')) {
      expect(badge.textContent).toContain('Azure OpenAI')
      expect(badge.textContent).not.toContain('AzureOpenAI')
    }
  })
})
