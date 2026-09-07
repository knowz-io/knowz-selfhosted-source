import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { renderWithProviders } from './test-utils'
import FilesPage from '../pages/FilesPage'

// Mock the api module
vi.mock('../lib/api-client', () => ({
  api: {
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

const mockListFiles = vi.mocked(api.listFiles)
const mockDeleteFile = vi.mocked(api.deleteFile)

describe('FilesPage', () => {
  beforeEach(() => {
    mockListFiles.mockResolvedValue({
      items: [
        {
          id: 'file-1',
          fileName: 'report.pdf',
          contentType: 'application/pdf',
          sizeBytes: 1048576,
          blobMigrationPending: false,
          knowledgeId: 'knowledge-1',
          knowledgeTitle: 'Quarterly report',
          textExtractionStatus: 2,
          createdAt: '2026-01-15T10:00:00Z',
          updatedAt: '2026-01-15T10:00:00Z',
        },
        {
          id: 'file-2',
          fileName: 'photo.jpg',
          contentType: 'image/jpeg',
          sizeBytes: 2097152,
          blobMigrationPending: false,
          createdAt: '2026-01-14T10:00:00Z',
          updatedAt: '2026-01-14T10:00:00Z',
        },
      ],
      page: 1,
      pageSize: 20,
      totalItems: 2,
      totalPages: 1,
    })
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('Should_RenderUploadAction_WhenMounted', async () => {
    // The page title is now rendered by the Layout header, not the page itself.
    // The page must still render the primary Upload action.
    renderWithProviders(<FilesPage />)
    expect(screen.getByText('Upload')).toBeInTheDocument()
  })

  it('Should_DisplayFileList_WhenFilesExist', async () => {
    renderWithProviders(<FilesPage />)
    // Responsive layout renders both desktop table and mobile card in jsdom
    // (media queries are not evaluated), so we use getAllByText.
    await waitFor(() => {
      expect(screen.getAllByText('report.pdf').length).toBeGreaterThan(0)
    })
    expect(screen.getAllByText('photo.jpg').length).toBeGreaterThan(0)
  })

  it('Should_DisplayFileSize_FormattedCorrectly', async () => {
    renderWithProviders(<FilesPage />)
    await waitFor(() => {
      expect(screen.getAllByText('1.0 MB').length).toBeGreaterThan(0)
    })
    expect(screen.getAllByText('2.0 MB').length).toBeGreaterThan(0)
  })

  it('Should_DisplayEmptyState_WhenNoFiles', async () => {
    mockListFiles.mockResolvedValue({
      items: [],
      page: 1,
      pageSize: 20,
      totalItems: 0,
      totalPages: 0,
    })
    renderWithProviders(<FilesPage />)
    await waitFor(() => {
      expect(screen.getByText('No files')).toBeInTheDocument()
    })
  })

  it('Should_HaveSearchInput_WhenRendered', async () => {
    renderWithProviders(<FilesPage />)
    expect(screen.getByPlaceholderText('Search files...')).toBeInTheDocument()
  })

  it('Should_HaveUploadButton_WhenRendered', async () => {
    renderWithProviders(<FilesPage />)
    expect(screen.getByText('Upload')).toBeInTheDocument()
  })

  it('Should_RenderFileList_WhenFilesLoaded', async () => {
    // SH_CompactHeroes: the verbose hero + stat cards were removed in favor of a
    // compact action row + direct table. We now assert that the file table
    // renders the expected item names instead of the removed stat summaries.
    renderWithProviders(<FilesPage />)

    await waitFor(() => {
      expect(screen.getAllByText('report.pdf').length).toBeGreaterThan(0)
    })

    expect(screen.getAllByText('photo.jpg').length).toBeGreaterThan(0)
  })

  it('Should_ShowDragDropArea_WhenRendered', async () => {
    renderWithProviders(<FilesPage />)
    expect(screen.getByText(/drag.*drop/i)).toBeInTheDocument()
  })

  it('Should_CallDeleteFile_WhenDeleteButtonClicked', async () => {
    mockDeleteFile.mockResolvedValue(undefined)
    renderWithProviders(<FilesPage />)
    const user = userEvent.setup()

    await waitFor(() => {
      expect(screen.getAllByText('report.pdf').length).toBeGreaterThan(0)
    })

    // Find delete buttons (there should be one per file per rendered layout)
    const deleteButtons = screen.getAllByTitle('Delete')
    await user.click(deleteButtons[0])

    await waitFor(() => {
      expect(mockDeleteFile).toHaveBeenCalledWith('file-1')
    })
  })

  it('Should_ShowPagination_WhenMultiplePages', async () => {
    mockListFiles.mockResolvedValue({
      items: [
        {
          id: 'file-1',
          fileName: 'report.pdf',
          contentType: 'application/pdf',
          sizeBytes: 1024,
          blobMigrationPending: false,
          createdAt: '2026-01-15T10:00:00Z',
          updatedAt: '2026-01-15T10:00:00Z',
        },
      ],
      page: 1,
      pageSize: 20,
      totalItems: 40,
      totalPages: 2,
    })
    renderWithProviders(<FilesPage />)
    await waitFor(() => {
      expect(screen.getByText(/Page 1 of 2/)).toBeInTheDocument()
    })
  })
})
