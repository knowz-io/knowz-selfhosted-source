import { describe, it, expect, vi, beforeEach, beforeAll } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { renderWithProviders } from './test-utils'
import KnowledgeDetailPage from '../pages/KnowledgeDetailPage'

// jsdom does not implement scrollIntoView
beforeAll(() => {
  Element.prototype.scrollIntoView = vi.fn()
})

// Mock react-router-dom useParams
vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual('react-router-dom')
  return {
    ...actual,
    useParams: () => ({ id: 'knowledge-1' }),
    useNavigate: () => vi.fn(),
  }
})

// Mock the api module
const mockChat = vi.fn()
vi.mock('../lib/api-client', () => ({
  api: {
    getKnowledge: vi.fn(),
    updateKnowledge: vi.fn(),
    deleteKnowledge: vi.fn(),
    getKnowledgeAttachments: vi.fn(),
    listFiles: vi.fn(),
    attachFileToKnowledge: vi.fn(),
    detachFileFromKnowledge: vi.fn(),
    getFileCleanupPolicy: vi.fn().mockResolvedValue({ mode: 'PromptUser' }),
    uploadFile: vi.fn(),
    downloadFile: vi.fn(),
    reprocessKnowledge: vi.fn(),
    getEnrichmentStatus: vi.fn(),
    getVersionHistory: vi.fn(),
    chat: (...args: unknown[]) => mockChat(...args),
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

// Mock format-markdown
vi.mock('../lib/format-markdown', () => ({
  formatMarkdown: (text: string) => text,
}))

// Import mock api after mock setup
import { api } from '../lib/api-client'

const mockKnowledge = {
  id: 'knowledge-1',
  title: 'Test Knowledge Item',
  content: 'This is the full content.',
  summary: 'This is a summary.',
  briefSummary: 'Brief summary.',
  type: 'note',
  tags: ['test'],
  source: '',
  vaults: [],
  createdAt: '2024-01-01T00:00:00Z',
  updatedAt: '2024-01-01T00:00:00Z',
  isIndexed: true,
  indexedAt: '2024-01-01T00:00:00Z',
}

describe('KnowledgeChatPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    ;(api.getKnowledge as ReturnType<typeof vi.fn>).mockResolvedValue(mockKnowledge)
    ;(api.getKnowledgeAttachments as ReturnType<typeof vi.fn>).mockResolvedValue([])
    ;(api.getEnrichmentStatus as ReturnType<typeof vi.fn>).mockRejectedValue(
      new (class extends Error { status = 404 })('Not found')
    )
    mockChat.mockResolvedValue({ answer: 'Test answer' })
  })

  async function openChatPanel() {
    renderWithProviders(<KnowledgeDetailPage />)
    await waitFor(() => {
      expect(screen.getByText('Test Knowledge Item')).toBeInTheDocument()
    })
    // Click the chat FAB
    const chatButton = screen.getByTitle('Chat with this knowledge item')
    await userEvent.click(chatButton)
  }

  describe('Detail Level Picker', () => {
    it('Should_ShowThreeDetailLevelOptions_WhenChatPanelOpened', async () => {
      await openChatPanel()
      expect(screen.getByText('Concise')).toBeInTheDocument()
      expect(screen.getByText('Balanced')).toBeInTheDocument()
      expect(screen.getByText('Detailed')).toBeInTheDocument()
    })

    it('Should_DefaultToConcise_WhenChatPanelOpened', async () => {
      await openChatPanel()
      const conciseBtn = screen.getByText('Concise')
      // The default selected button should have the primary styling
      expect(conciseBtn.closest('button')).toHaveClass('bg-primary')
    })

    it('Should_HighlightSelectedLevel_WhenUserClicksBalanced', async () => {
      await openChatPanel()
      const balancedBtn = screen.getByText('Balanced')
      await userEvent.click(balancedBtn)
      expect(balancedBtn.closest('button')).toHaveClass('bg-primary')
      // Concise should no longer be highlighted
      const conciseBtn = screen.getByText('Concise')
      expect(conciseBtn.closest('button')).not.toHaveClass('bg-primary')
    })

    it('Should_PrependDetailLevelToMessage_WhenSending', async () => {
      await openChatPanel()
      const textarea = screen.getByPlaceholderText('Ask a quick question...')
      await userEvent.type(textarea, 'What is this about?')
      const sendButton = screen.getByLabelText('Send message')
      await userEvent.click(sendButton)

      await waitFor(() => {
        expect(mockChat).toHaveBeenCalledWith(
          expect.objectContaining({
            question: '[Detail level: concise] What is this about?',
          })
        )
      })
    })

    it('Should_PrependDetailedLevel_WhenDetailedSelected', async () => {
      await openChatPanel()
      const detailedBtn = screen.getByText('Detailed')
      await userEvent.click(detailedBtn)
      const textarea = screen.getByPlaceholderText('Ask for a thorough analysis...')
      await userEvent.type(textarea, 'Explain everything')
      const sendButton = screen.getByLabelText('Send message')
      await userEvent.click(sendButton)

      await waitFor(() => {
        expect(mockChat).toHaveBeenCalledWith(
          expect.objectContaining({
            question: '[Detail level: detailed] Explain everything',
          })
        )
      })
    })
  })

  describe('Dynamic Placeholder', () => {
    it('Should_ShowConcisePlaceholder_WhenConciseSelected', async () => {
      await openChatPanel()
      expect(screen.getByPlaceholderText('Ask a quick question...')).toBeInTheDocument()
    })

    it('Should_ShowBalancedPlaceholder_WhenBalancedSelected', async () => {
      await openChatPanel()
      await userEvent.click(screen.getByText('Balanced'))
      expect(screen.getByPlaceholderText('Ask about this knowledge...')).toBeInTheDocument()
    })

    it('Should_ShowDetailedPlaceholder_WhenDetailedSelected', async () => {
      await openChatPanel()
      await userEvent.click(screen.getByText('Detailed'))
      expect(screen.getByPlaceholderText('Ask for a thorough analysis...')).toBeInTheDocument()
    })
  })

  describe('Maximize/Minimize Toggle', () => {
    it('Should_ShowMaximizeButton_WhenChatPanelOpened', async () => {
      await openChatPanel()
      expect(screen.getByLabelText('Maximize chat')).toBeInTheDocument()
    })

    it('Should_ExpandPanel_WhenMaximizeClicked', async () => {
      await openChatPanel()
      const maximizeBtn = screen.getByLabelText('Maximize chat')
      await userEvent.click(maximizeBtn)
      // After maximize, the minimize button should appear
      expect(screen.getByLabelText('Minimize chat')).toBeInTheDocument()
    })

    it('Should_ShrinkPanel_WhenMinimizeClicked', async () => {
      await openChatPanel()
      // Maximize first
      await userEvent.click(screen.getByLabelText('Maximize chat'))
      // Then minimize
      await userEvent.click(screen.getByLabelText('Minimize chat'))
      // Should show maximize button again
      expect(screen.getByLabelText('Maximize chat')).toBeInTheDocument()
    })
  })
})
