import { describe, it, expect, vi } from 'vitest'
import { screen, fireEvent } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { renderWithProviders } from './test-utils'
import AttachmentViewer from '../components/AttachmentViewer'

const imageFile = {
  id: 'file-1',
  fileName: 'photo.jpg',
  contentType: 'image/jpeg',
  sizeBytes: 204800,
  extractedText: undefined,
  blobUri: 'https://example.com/blob/photo.jpg',
  transcriptionText: undefined,
  visionDescription: 'A cat sitting on a sofa',
  blobMigrationPending: false,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
}

const pdfFile = {
  id: 'file-2',
  fileName: 'document.pdf',
  contentType: 'application/pdf',
  sizeBytes: 1048576,
  extractedText: 'This is the extracted text from the PDF document.',
  blobUri: 'https://example.com/blob/document.pdf',
  transcriptionText: undefined,
  visionDescription: undefined,
  blobMigrationPending: false,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
}

const genericFile = {
  id: 'file-3',
  fileName: 'data.csv',
  contentType: 'text/csv',
  sizeBytes: 512,
  extractedText: undefined,
  blobUri: undefined,
  transcriptionText: undefined,
  visionDescription: undefined,
  blobMigrationPending: false,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
}

describe('AttachmentViewer', () => {
  it('Should_ShowFileName_WhenModalOpened', () => {
    renderWithProviders(
      <AttachmentViewer file={pdfFile} onClose={() => {}} />
    )
    expect(screen.getByText('document.pdf')).toBeInTheDocument()
  })

  it('Should_ShowFileSize_WhenModalOpened', () => {
    renderWithProviders(
      <AttachmentViewer file={pdfFile} onClose={() => {}} />
    )
    expect(screen.getByText(/1\.0 MB/)).toBeInTheDocument()
  })

  it('Should_ShowContentType_WhenModalOpened', () => {
    renderWithProviders(
      <AttachmentViewer file={pdfFile} onClose={() => {}} />
    )
    expect(screen.getByText('application/pdf')).toBeInTheDocument()
  })

  it('Should_ShowExtractedText_WhenPdfFile', () => {
    renderWithProviders(
      <AttachmentViewer file={pdfFile} onClose={() => {}} />
    )
    expect(screen.getByText('Extracted Content')).toBeInTheDocument()
    expect(screen.getByText('This is the extracted text from the PDF document.')).toBeInTheDocument()
  })

  it('Should_ShowVisionDescription_WhenImageFile', () => {
    renderWithProviders(
      <AttachmentViewer file={imageFile} onClose={() => {}} />
    )
    expect(screen.getByText('A cat sitting on a sofa')).toBeInTheDocument()
  })

  it('Should_ShowDownloadButton_WhenGenericFile', () => {
    renderWithProviders(
      <AttachmentViewer file={genericFile} onClose={() => {}} />
    )
    expect(screen.getByText(/Download/)).toBeInTheDocument()
  })

  it('Should_CallOnClose_WhenCloseButtonClicked', async () => {
    const onClose = vi.fn()
    renderWithProviders(
      <AttachmentViewer file={pdfFile} onClose={onClose} />
    )
    const closeButton = screen.getByLabelText('Close')
    await userEvent.click(closeButton)
    expect(onClose).toHaveBeenCalledTimes(1)
  })

  it('Should_CallOnClose_WhenBackdropClicked', async () => {
    const onClose = vi.fn()
    renderWithProviders(
      <AttachmentViewer file={pdfFile} onClose={onClose} />
    )
    const backdrop = screen.getByTestId('attachment-viewer-backdrop')
    await userEvent.click(backdrop)
    expect(onClose).toHaveBeenCalledTimes(1)
  })

  it('Should_CallOnClose_WhenEscapeKeyPressed', () => {
    const onClose = vi.fn()
    renderWithProviders(
      <AttachmentViewer file={pdfFile} onClose={onClose} />
    )
    fireEvent.keyDown(document, { key: 'Escape' })
    expect(onClose).toHaveBeenCalledTimes(1)
  })

  it('Should_HaveBackdropOverlay_WhenRendered', () => {
    renderWithProviders(
      <AttachmentViewer file={pdfFile} onClose={() => {}} />
    )
    const backdrop = screen.getByTestId('attachment-viewer-backdrop')
    expect(backdrop).toBeInTheDocument()
  })
})
