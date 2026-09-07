import { describe, it, expect } from 'vitest'
import type {
  FileMetadataDto,
  FileUploadResult,
  FileListResponse,
  FileAttachmentDto,
} from '../lib/types'

describe('File Types', () => {
  it('Should_HaveFileMetadataDto_WithRequiredFields', () => {
    const metadata: FileMetadataDto = {
      id: '123',
      fileName: 'test.pdf',
      sizeBytes: 1024,
      blobMigrationPending: false,
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
    }
    expect(metadata.id).toBe('123')
    expect(metadata.fileName).toBe('test.pdf')
    expect(metadata.sizeBytes).toBe(1024)
    expect(metadata.blobMigrationPending).toBe(false)
    expect(metadata.createdAt).toBe('2026-01-01T00:00:00Z')
    expect(metadata.updatedAt).toBe('2026-01-01T00:00:00Z')
  })

  it('Should_HaveFileMetadataDto_WithOptionalFields', () => {
    const metadata: FileMetadataDto = {
      id: '123',
      fileName: 'test.pdf',
      contentType: 'application/pdf',
      sizeBytes: 1024,
      blobUri: 'https://blob.test/file.pdf',
      transcriptionText: 'some transcript',
      extractedText: 'some extracted text',
      visionDescription: 'a picture of a cat',
      blobMigrationPending: false,
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
    }
    expect(metadata.contentType).toBe('application/pdf')
    expect(metadata.blobUri).toBe('https://blob.test/file.pdf')
    expect(metadata.transcriptionText).toBe('some transcript')
    expect(metadata.extractedText).toBe('some extracted text')
    expect(metadata.visionDescription).toBe('a picture of a cat')
  })

  it('Should_HaveFileUploadResult_WithAllFields', () => {
    const result: FileUploadResult = {
      fileRecordId: 'abc-123',
      fileName: 'upload.txt',
      contentType: 'text/plain',
      sizeBytes: 512,
      blobUri: 'https://blob.test/upload.txt',
      success: true,
    }
    expect(result.fileRecordId).toBe('abc-123')
    expect(result.fileName).toBe('upload.txt')
    expect(result.contentType).toBe('text/plain')
    expect(result.sizeBytes).toBe(512)
    expect(result.blobUri).toBe('https://blob.test/upload.txt')
    expect(result.success).toBe(true)
  })

  it('Should_HaveFileListResponse_WithPagination', () => {
    const response: FileListResponse = {
      items: [],
      page: 1,
      pageSize: 20,
      totalItems: 0,
      totalPages: 0,
    }
    expect(response.items).toEqual([])
    expect(response.page).toBe(1)
    expect(response.pageSize).toBe(20)
    expect(response.totalItems).toBe(0)
    expect(response.totalPages).toBe(0)
  })

  it('Should_HaveFileAttachmentDto_WithAllFields', () => {
    const attachment: FileAttachmentDto = {
      id: 'att-1',
      fileRecordId: 'file-1',
      createdAt: '2026-01-01T00:00:00Z',
    }
    expect(attachment.id).toBe('att-1')
    expect(attachment.fileRecordId).toBe('file-1')
    expect(attachment.createdAt).toBe('2026-01-01T00:00:00Z')
  })

  it('Should_HaveFileAttachmentDto_WithOptionalKnowledgeAndCommentIds', () => {
    const attachment: FileAttachmentDto = {
      id: 'att-1',
      fileRecordId: 'file-1',
      knowledgeId: 'knowledge-1',
      commentId: 'comment-1',
      createdAt: '2026-01-01T00:00:00Z',
    }
    expect(attachment.knowledgeId).toBe('knowledge-1')
    expect(attachment.commentId).toBe('comment-1')
  })
})
