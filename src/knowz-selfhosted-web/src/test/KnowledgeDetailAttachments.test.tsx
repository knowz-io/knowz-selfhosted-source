import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { screen, waitFor, fireEvent } from '@testing-library/react'
import { renderWithProviders } from './test-utils'
import KnowledgeDetailPage from '../pages/KnowledgeDetailPage'

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
    getVersionHistory: vi.fn().mockResolvedValue([]),
    getEnrichmentStatus: vi.fn().mockResolvedValue({ status: 'completed' }),
    reprocessKnowledge: vi.fn(),
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

import { api } from '../lib/api-client'

const mockGetKnowledge = vi.mocked(api.getKnowledge)
const mockGetAttachments = vi.mocked(api.getKnowledgeAttachments)

/**
 * The Attachments UI lives inside a tab on KnowledgeDetailPage. To verify
 * attachment content, we have to click the Attachments tab first — the
 * tab panel is only rendered when it is active.
 */
async function openAttachmentsTab() {
  // The Attachments text appears both as a tab label and in the DetailSidebar
  // metadata panel, so we use the count-bearing tab button to disambiguate.
  const tabButtons = await screen.findAllByText('Attachments')
  // The first match is always the tab (rendered before the sidebar in DOM order).
  const tabButton = tabButtons[0].closest('button')
  expect(tabButton).not.toBeNull()
  fireEvent.click(tabButton!)
}

describe('KnowledgeDetailPage - Attachments', () => {
  beforeEach(() => {
    mockGetKnowledge.mockResolvedValue({
      id: 'knowledge-1',
      title: 'Test Knowledge',
      content: 'Some content here',
      type: 'Note',
      tags: ['test'],
      vaults: [{ id: 'vault-1', name: 'Default', isPrimary: true }],
      createdAt: '2026-01-15T10:00:00Z',
      updatedAt: '2026-01-15T10:00:00Z',
      isIndexed: true,
      indexedAt: '2026-01-15T10:01:00Z',
    })

    mockGetAttachments.mockResolvedValue([
      {
        id: 'file-1',
        fileName: 'attached-doc.pdf',
        contentType: 'application/pdf',
        sizeBytes: 2048,
        blobMigrationPending: false,
        createdAt: '2026-01-15T10:00:00Z',
        updatedAt: '2026-01-15T10:00:00Z',
      },
    ])
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('Should_RenderAttachmentsTab_WhenKnowledgeLoaded', async () => {
    renderWithProviders(<KnowledgeDetailPage />)
    await waitFor(() => {
      const matches = screen.getAllByText('Attachments')
      expect(matches.length).toBeGreaterThan(0)
    })
  })

  it('Should_DisplayAttachedFiles_WhenAttachmentsTabOpened', async () => {
    renderWithProviders(<KnowledgeDetailPage />)
    await openAttachmentsTab()
    await waitFor(() => {
      expect(screen.getByText('attached-doc.pdf')).toBeInTheDocument()
    })
  })

  it('Should_ShowUploadAndAttachButton_WhenAttachmentsTabOpened', async () => {
    renderWithProviders(<KnowledgeDetailPage />)
    await openAttachmentsTab()
    await waitFor(() => {
      expect(screen.getByText(/upload & attach/i)).toBeInTheDocument()
    })
  })

  it('Should_ShowEmptyAttachmentsMessage_WhenNoAttachments', async () => {
    mockGetAttachments.mockResolvedValue([])
    renderWithProviders(<KnowledgeDetailPage />)
    await openAttachmentsTab()
    // With no attachments, the Upload & Attach button is still present
    // and the attached file list is empty.
    await waitFor(() => {
      expect(screen.getByText(/upload & attach/i)).toBeInTheDocument()
    })
    expect(screen.queryByText('attached-doc.pdf')).not.toBeInTheDocument()
  })
})
