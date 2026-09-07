import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { api } from '../lib/api-client'

describe('API Client - File Methods', () => {
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

  it('Should_UploadFile_WhenCalledWithFile', async () => {
    const uploadResult = {
      fileRecordId: 'file-1',
      fileName: 'test.pdf',
      contentType: 'application/pdf',
      sizeBytes: 1024,
      blobUri: 'https://blob.test/test.pdf',
      success: true,
    }
    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: () => Promise.resolve(uploadResult),
    })

    const file = new File(['hello'], 'test.pdf', { type: 'application/pdf' })
    const result = await api.uploadFile(file)

    expect(result).toEqual(uploadResult)
    expect(mockFetch).toHaveBeenCalledTimes(1)

    const [url, options] = mockFetch.mock.calls[0]
    expect(url).toBe('http://localhost:5000/api/v1/files/upload')
    expect(options.method).toBe('POST')
    expect(options.body).toBeInstanceOf(FormData)
    // Should NOT set Content-Type header (browser sets it with boundary for FormData)
    expect(options.headers['Content-Type']).toBeUndefined()
  })

  it('Should_ListFiles_WhenCalledWithPagination', async () => {
    const listResponse = {
      items: [],
      page: 1,
      pageSize: 20,
      totalItems: 0,
      totalPages: 0,
    }
    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: () => Promise.resolve(listResponse),
    })

    const result = await api.listFiles(1, 20)

    expect(result).toEqual(listResponse)
    const [url] = mockFetch.mock.calls[0]
    expect(url).toContain('/api/v1/files')
    expect(url).toContain('page=1')
    expect(url).toContain('pageSize=20')
  })

  it('Should_ListFiles_WhenCalledWithSearchAndFilter', async () => {
    const listResponse = {
      items: [],
      page: 1,
      pageSize: 20,
      totalItems: 0,
      totalPages: 0,
    }
    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: () => Promise.resolve(listResponse),
    })

    await api.listFiles(1, 20, 'report', 'application/pdf')

    const [url] = mockFetch.mock.calls[0]
    expect(url).toContain('search=report')
    expect(url).toContain('contentTypeFilter=application%2Fpdf')
  })

  it('Should_GetFileMetadata_WhenCalledWithId', async () => {
    const metadata = {
      id: 'file-1',
      fileName: 'test.pdf',
      sizeBytes: 1024,
      blobMigrationPending: false,
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
    }
    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: () => Promise.resolve(metadata),
    })

    const result = await api.getFileMetadata('file-1')

    expect(result).toEqual(metadata)
    const [url] = mockFetch.mock.calls[0]
    expect(url).toBe('http://localhost:5000/api/v1/files/file-1')
  })

  it('Should_DownloadFile_WhenCalledWithId', async () => {
    const blob = new Blob(['file content'], { type: 'application/pdf' })
    mockFetch.mockResolvedValueOnce({
      ok: true,
      blob: () => Promise.resolve(blob),
    })

    const result = await api.downloadFile('file-1')

    expect(result).toBeInstanceOf(Blob)
    const [url] = mockFetch.mock.calls[0]
    expect(url).toBe('http://localhost:5000/api/v1/files/file-1/download')
  })

  it('Should_DeleteFile_WhenCalledWithId', async () => {
    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: () => Promise.resolve({}),
    })

    await api.deleteFile('file-1')

    const [url, options] = mockFetch.mock.calls[0]
    expect(url).toBe('http://localhost:5000/api/v1/files/file-1')
    expect(options.method).toBe('DELETE')
  })

  it('Should_AttachFileToKnowledge_WhenCalledWithIds', async () => {
    const attachment = {
      id: 'att-1',
      fileRecordId: 'file-1',
      knowledgeId: 'knowledge-1',
      createdAt: '2026-01-01T00:00:00Z',
    }
    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: () => Promise.resolve(attachment),
    })

    const result = await api.attachFileToKnowledge('knowledge-1', 'file-1')

    expect(result).toEqual(attachment)
    const [url, options] = mockFetch.mock.calls[0]
    expect(url).toBe('http://localhost:5000/api/v1/knowledge/knowledge-1/attachments')
    expect(options.method).toBe('POST')
    expect(JSON.parse(options.body)).toEqual({ fileRecordId: 'file-1' })
  })

  it('Should_GetKnowledgeAttachments_WhenCalledWithKnowledgeId', async () => {
    const attachments = [
      {
        id: 'file-1',
        fileName: 'test.pdf',
        sizeBytes: 1024,
        blobMigrationPending: false,
        createdAt: '2026-01-01T00:00:00Z',
        updatedAt: '2026-01-01T00:00:00Z',
      },
    ]
    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: () => Promise.resolve(attachments),
    })

    const result = await api.getKnowledgeAttachments('knowledge-1')

    expect(result).toEqual(attachments)
    const [url] = mockFetch.mock.calls[0]
    expect(url).toBe('http://localhost:5000/api/v1/knowledge/knowledge-1/attachments')
  })

  it('Should_DetachFileFromKnowledge_WhenCalledWithIds', async () => {
    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: () => Promise.resolve({}),
    })

    await api.detachFileFromKnowledge('knowledge-1', 'file-1')

    const [url, options] = mockFetch.mock.calls[0]
    expect(url).toBe('http://localhost:5000/api/v1/knowledge/knowledge-1/attachments/file-1')
    expect(options.method).toBe('DELETE')
  })

  // WorkGroupID: kc-feat-file-delete-orphan-policy-20260616-140729 — FEAT_SelfHostedFileCleanupPolicy (N6).
  it('Should_OmitDeleteFilesParam_WhenUndefined', async () => {
    mockFetch.mockResolvedValueOnce({ ok: true, json: () => Promise.resolve({}) })

    await api.detachFileFromKnowledge('knowledge-1', 'file-1')

    const [url] = mockFetch.mock.calls[0]
    expect(url).not.toContain('deleteFiles')
  })

  it('Should_AppendDeleteFilesTrue_WhenOptedIn', async () => {
    mockFetch.mockResolvedValueOnce({ ok: true, json: () => Promise.resolve({}) })

    await api.detachFileFromKnowledge('knowledge-1', 'file-1', true)

    const [url] = mockFetch.mock.calls[0]
    expect(url).toBe('http://localhost:5000/api/v1/knowledge/knowledge-1/attachments/file-1?deleteFiles=true')
  })

  it('Should_AppendDeleteFilesFalse_WhenPreserveChosen', async () => {
    mockFetch.mockResolvedValueOnce({ ok: true, json: () => Promise.resolve({}) })

    await api.detachFileFromKnowledge('knowledge-1', 'file-1', false)

    const [url] = mockFetch.mock.calls[0]
    expect(url).toContain('?deleteFiles=false')
  })

  it('Should_GetFileCleanupPolicy_FromSettingsEndpoint', async () => {
    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: () => Promise.resolve({ mode: 'PromptUser' }),
    })

    const result = await api.getFileCleanupPolicy()

    expect(result).toEqual({ mode: 'PromptUser' })
    const [url] = mockFetch.mock.calls[0]
    expect(url).toBe('http://localhost:5000/api/v1/settings/file-cleanup')
  })
})
