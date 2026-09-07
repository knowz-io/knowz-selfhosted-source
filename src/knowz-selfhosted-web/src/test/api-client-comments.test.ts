import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { api } from '../lib/api-client'

describe('API Client - Comment Methods', () => {
  const mockFetch = vi.fn()
  let store: Record<string, string>

  beforeEach(() => {
    vi.stubGlobal('fetch', mockFetch)
    store = {
      apiUrl: 'http://localhost:5000',
      apiKey: 'test-api-key',
    }
    const mockStorage = {
      getItem: vi.fn((key: string) => store[key] ?? null),
      setItem: vi.fn((key: string, value: string) => { store[key] = value }),
      removeItem: vi.fn((key: string) => { delete store[key] }),
      clear: vi.fn(() => { store = {} }),
      length: 0,
      key: vi.fn(() => null),
    }
    vi.stubGlobal('localStorage', mockStorage)
  })

  afterEach(() => {
    mockFetch.mockReset()
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  // --- Comment CRUD ---

  it('Should_AddComment_WhenCalledWithKnowledgeIdAndData', async () => {
    const comment = {
      id: 'comment-1',
      knowledgeId: 'knowledge-1',
      authorName: 'Test User',
      body: 'A contribution',
      isAnswer: false,
      createdAt: '2026-02-16T10:00:00Z',
      updatedAt: '2026-02-16T10:00:00Z',
      attachmentCount: 0,
    }
    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: () => Promise.resolve(comment),
    })

    const result = await api.addComment('knowledge-1', { body: 'A contribution' })

    expect(result).toEqual(comment)
    const [url, options] = mockFetch.mock.calls[0]
    expect(url).toBe('http://localhost:5000/api/v1/knowledge/knowledge-1/comments')
    expect(options.method).toBe('POST')
    expect(JSON.parse(options.body)).toEqual({ body: 'A contribution' })
  })

  it('Should_AddComment_WithParentCommentId_WhenReplyingToComment', async () => {
    const reply = {
      id: 'reply-1',
      knowledgeId: 'knowledge-1',
      parentCommentId: 'comment-1',
      authorName: 'Test User',
      body: 'A reply',
      isAnswer: false,
      createdAt: '2026-02-16T10:00:00Z',
      updatedAt: '2026-02-16T10:00:00Z',
      attachmentCount: 0,
    }
    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: () => Promise.resolve(reply),
    })

    const result = await api.addComment('knowledge-1', {
      body: 'A reply',
      parentCommentId: 'comment-1',
    })

    expect(result).toEqual(reply)
    const [, options] = mockFetch.mock.calls[0]
    expect(JSON.parse(options.body)).toEqual({
      body: 'A reply',
      parentCommentId: 'comment-1',
    })
  })

  it('Should_ListComments_WhenCalledWithKnowledgeId', async () => {
    const comments = [
      {
        id: 'comment-1',
        knowledgeId: 'knowledge-1',
        authorName: 'User A',
        body: 'First comment',
        isAnswer: false,
        createdAt: '2026-02-16T10:00:00Z',
        updatedAt: '2026-02-16T10:00:00Z',
        attachmentCount: 0,
        replies: [],
      },
    ]
    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: () => Promise.resolve(comments),
    })

    const result = await api.listComments('knowledge-1')

    expect(result).toEqual(comments)
    const [url, options] = mockFetch.mock.calls[0]
    expect(url).toBe('http://localhost:5000/api/v1/knowledge/knowledge-1/comments')
    expect(options.method).toBeUndefined() // GET is default
  })

  it('Should_UpdateComment_WhenCalledWithIdAndData', async () => {
    const updated = {
      id: 'comment-1',
      knowledgeId: 'knowledge-1',
      authorName: 'Test User',
      body: 'Updated body',
      isAnswer: false,
      createdAt: '2026-02-16T10:00:00Z',
      updatedAt: '2026-02-16T11:00:00Z',
      attachmentCount: 0,
    }
    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: () => Promise.resolve(updated),
    })

    const result = await api.updateComment('comment-1', { body: 'Updated body' })

    expect(result).toEqual(updated)
    const [url, options] = mockFetch.mock.calls[0]
    expect(url).toBe('http://localhost:5000/api/v1/comments/comment-1')
    expect(options.method).toBe('PUT')
    expect(JSON.parse(options.body)).toEqual({ body: 'Updated body' })
  })

  it('Should_DeleteComment_WhenCalledWithId', async () => {
    // WorkGroupID: kc-fix-attach-delete-transcript-20260411-080000 —
    // deleteComment now returns a CommentDeleteResult envelope and forwards the
    // `deleteFiles` flag as a query param (defaults to false).
    const deleteResult = {
      filesPreserved: 0,
      filesDeleted: 0,
      preservedFileNames: [],
      deletedFileNames: [],
    }
    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: () => Promise.resolve(deleteResult),
    })

    const result = await api.deleteComment('comment-1')

    expect(result).toEqual(deleteResult)
    const [url, options] = mockFetch.mock.calls[0]
    expect(url).toBe('http://localhost:5000/api/v1/comments/comment-1?deleteFiles=false')
    expect(options.method).toBe('DELETE')
  })

  it('Should_DeleteComment_WithDeleteFilesTrue_WhenFlagPassed', async () => {
    // WorkGroupID: kc-fix-attach-delete-transcript-20260411-080000 —
    // When caller explicitly sets deleteFiles=true the URL query string reflects it.
    const deleteResult = {
      filesPreserved: 0,
      filesDeleted: 1,
      preservedFileNames: [],
      deletedFileNames: ['photo.jpg'],
    }
    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: () => Promise.resolve(deleteResult),
    })

    const result = await api.deleteComment('comment-1', true)

    expect(result).toEqual(deleteResult)
    const [url, options] = mockFetch.mock.calls[0]
    expect(url).toBe('http://localhost:5000/api/v1/comments/comment-1?deleteFiles=true')
    expect(options.method).toBe('DELETE')
  })

  // --- Comment Attachment Methods ---

  it('Should_AttachFileToComment_WhenCalledWithIds', async () => {
    const attachment = {
      id: 'att-1',
      fileRecordId: 'file-1',
      commentId: 'comment-1',
      createdAt: '2026-02-16T10:00:00Z',
    }
    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: () => Promise.resolve(attachment),
    })

    const result = await api.attachFileToComment('comment-1', 'file-1')

    expect(result).toEqual(attachment)
    const [url, options] = mockFetch.mock.calls[0]
    expect(url).toBe('http://localhost:5000/api/v1/comments/comment-1/attachments')
    expect(options.method).toBe('POST')
    expect(JSON.parse(options.body)).toEqual({ fileRecordId: 'file-1' })
  })

  it('Should_GetCommentAttachments_WhenCalledWithCommentId', async () => {
    const attachments = [
      {
        id: 'file-1',
        fileName: 'doc.pdf',
        sizeBytes: 2048,
        blobMigrationPending: false,
        createdAt: '2026-02-16T10:00:00Z',
        updatedAt: '2026-02-16T10:00:00Z',
      },
    ]
    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: () => Promise.resolve(attachments),
    })

    const result = await api.getCommentAttachments('comment-1')

    expect(result).toEqual(attachments)
    const [url] = mockFetch.mock.calls[0]
    expect(url).toBe('http://localhost:5000/api/v1/comments/comment-1/attachments')
  })

  it('Should_DetachFileFromComment_WhenCalledWithIds', async () => {
    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: () => Promise.resolve({}),
    })

    await api.detachFileFromComment('comment-1', 'file-1')

    const [url, options] = mockFetch.mock.calls[0]
    expect(url).toBe('http://localhost:5000/api/v1/comments/comment-1/attachments/file-1')
    expect(options.method).toBe('DELETE')
  })
})
