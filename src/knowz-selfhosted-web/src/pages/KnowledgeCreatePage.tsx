import { useState, useRef, useCallback } from 'react'
import { useNavigate, Link } from 'react-router-dom'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from '../lib/api-client'
import { ArrowLeft, Upload, X, Loader2, Paperclip, CheckCircle2, AlertCircle } from 'lucide-react'
import MarkdownContent from '../components/MarkdownContent'
import { formatFileSize } from '../lib/format-utils'

const KNOWLEDGE_TYPES = ['Note', 'Document', 'Email', 'Image', 'Audio', 'Video', 'Code', 'Link']

interface PendingFile {
  id: string
  file: File
  fileRecordId?: string
  uploading: boolean
  error?: string
}

export default function KnowledgeCreatePage() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()

  const [title, setTitle] = useState('')
  const [content, setContent] = useState('')
  const [type, setType] = useState('Note')
  const [vaultId, setVaultId] = useState('')
  const [tags, setTags] = useState('')
  const [source, setSource] = useState('')
  const [validationError, setValidationError] = useState('')
  const [activeTab, setActiveTab] = useState<'write' | 'preview'>('write')
  const [pendingFiles, setPendingFiles] = useState<PendingFile[]>([])
  const [isDragOver, setIsDragOver] = useState(false)
  const fileInputRef = useRef<HTMLInputElement>(null)

  const vaults = useQuery({
    queryKey: ['vaults', 'create'],
    queryFn: () => api.listVaults(false),
  })

  const isAnyUploading = pendingFiles.some((f) => f.uploading)

  const uploadFile = useCallback(async (pendingFile: PendingFile) => {
    setPendingFiles((prev) =>
      prev.map((f) => (f.id === pendingFile.id ? { ...f, uploading: true, error: undefined } : f)),
    )

    try {
      const result = await api.uploadFile(pendingFile.file)
      setPendingFiles((prev) =>
        prev.map((f) =>
          f.id === pendingFile.id ? { ...f, uploading: false, fileRecordId: result.fileRecordId } : f,
        ),
      )
    } catch (err) {
      setPendingFiles((prev) =>
        prev.map((f) =>
          f.id === pendingFile.id
            ? { ...f, uploading: false, error: err instanceof Error ? err.message : 'Upload failed' }
            : f,
        ),
      )
    }
  }, [])

  const addFiles = useCallback(
    (files: FileList | File[]) => {
      const newPendingFiles: PendingFile[] = Array.from(files).map((file) => ({
        id: `${Date.now()}-${Math.random().toString(36).slice(2)}`,
        file,
        uploading: false,
      }))

      setPendingFiles((prev) => [...prev, ...newPendingFiles])

      // Start uploading each file immediately
      newPendingFiles.forEach((pf) => uploadFile(pf))
    },
    [uploadFile],
  )

  const removeFile = useCallback((id: string) => {
    setPendingFiles((prev) => prev.filter((f) => f.id !== id))
  }, [])

  const handleFileSelect = (e: React.ChangeEvent<HTMLInputElement>) => {
    if (e.target.files && e.target.files.length > 0) {
      addFiles(e.target.files)
    }
    if (fileInputRef.current) {
      fileInputRef.current.value = ''
    }
  }

  const handleDragOver = useCallback((e: React.DragEvent) => {
    e.preventDefault()
    setIsDragOver(true)
  }, [])

  const handleDragLeave = useCallback((e: React.DragEvent) => {
    e.preventDefault()
    setIsDragOver(false)
  }, [])

  const handleDrop = useCallback(
    (e: React.DragEvent) => {
      e.preventDefault()
      setIsDragOver(false)
      if (e.dataTransfer.files.length > 0) {
        addFiles(e.dataTransfer.files)
      }
    },
    [addFiles],
  )

  const createMut = useMutation({
    mutationFn: () => {
      const completedFileRecordIds = pendingFiles
        .filter((f) => f.fileRecordId)
        .map((f) => f.fileRecordId!)

      return api.createKnowledge({
        title: title || undefined,
        content,
        type,
        vaultId: vaultId || undefined,
        tags: tags
          .split(',')
          .map((t) => t.trim())
          .filter(Boolean),
        source: source || undefined,
        attachmentFileRecordIds: completedFileRecordIds.length > 0 ? completedFileRecordIds : undefined,
      })
    },
    onSuccess: (data) => {
      queryClient.invalidateQueries({ queryKey: ['knowledge'] })
      navigate(`/knowledge/${data.id}`)
    },
  })

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault()
    setValidationError('')
    const hasContent = content.trim().length > 0
    const hasFiles = pendingFiles.some((f) => f.fileRecordId)
    if (!hasContent && !hasFiles) {
      setValidationError('Content or at least one attachment is required.')
      return
    }
    createMut.mutate()
  }

  return (
    <div className="mx-auto max-w-3xl space-y-4">
      <Link
        to="/knowledge"
        className="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground transition-colors"
      >
        <ArrowLeft size={16} /> Back to Knowledge
      </Link>

      <h1 className="text-2xl font-bold">Create Knowledge</h1>

      <form onSubmit={handleSubmit} className="space-y-4">
        <div>
          <label className="block text-sm font-medium mb-1">Title</label>
          <input
            type="text"
            value={title}
            onChange={(e) => setTitle(e.target.value)}
            placeholder="Optional - auto-generated from content if empty"
            className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm"
          />
        </div>

        <div>
          <label className="block text-sm font-medium mb-1">
            Content
          </label>
          <div className="border border-input rounded-md overflow-hidden">
            <div className="flex border-b border-input bg-muted">
              <button
                type="button"
                onClick={() => setActiveTab('write')}
                className={`px-4 py-2 text-sm font-medium transition-colors ${
                  activeTab === 'write'
                    ? 'text-foreground bg-card border-b-2 border-foreground'
                    : 'text-muted-foreground hover:text-foreground'
                }`}
              >
                Write
              </button>
              <button
                type="button"
                onClick={() => setActiveTab('preview')}
                className={`px-4 py-2 text-sm font-medium transition-colors ${
                  activeTab === 'preview'
                    ? 'text-foreground bg-card border-b-2 border-foreground'
                    : 'text-muted-foreground hover:text-foreground'
                }`}
              >
                Preview
              </button>
            </div>
            {activeTab === 'write' ? (
              <textarea
                value={content}
                onChange={(e) => setContent(e.target.value)}
                rows={12}
                placeholder="Enter knowledge content... (supports Markdown)"
                className="w-full px-3 py-2 bg-card text-sm font-mono border-0 focus:ring-0 focus:outline-none"
              />
            ) : (
              <div className="px-3 py-2 bg-card min-h-[288px]">
                {content.trim() ? (
                  <MarkdownContent content={content} />
                ) : (
                  <p className="text-muted-foreground text-sm italic">
                    Nothing to preview
                  </p>
                )}
              </div>
            )}
          </div>
          {validationError && (
            <p className="text-red-600 dark:text-red-400 text-sm mt-1">{validationError}</p>
          )}
        </div>

        {/* File Upload Zone */}
        <div>
          <label className="block text-sm font-medium mb-1">Attachments</label>
          <div
            onDragOver={handleDragOver}
            onDragLeave={handleDragLeave}
            onDrop={handleDrop}
            onClick={() => fileInputRef.current?.click()}
            className={`border-2 border-dashed rounded-lg p-6 text-center cursor-pointer transition-colors ${
              isDragOver
                ? 'border-primary bg-primary/5'
                : 'border-border hover:border-primary/50 hover:bg-muted/30'
            }`}
          >
            <Upload size={24} className="mx-auto text-muted-foreground mb-2" />
            <p className="text-sm text-muted-foreground">
              <span className="font-medium text-foreground">Click to upload</span> or drag and drop
            </p>
            <p className="text-xs text-muted-foreground mt-1">
              Files will be uploaded immediately and attached on save
            </p>
          </div>
          <input
            ref={fileInputRef}
            type="file"
            multiple
            onChange={handleFileSelect}
            className="hidden"
          />

          {/* Pending files list */}
          {pendingFiles.length > 0 && (
            <div className="mt-3 space-y-2">
              {pendingFiles.map((pf) => (
                <div
                  key={pf.id}
                  className="flex items-center gap-3 px-3 py-2 bg-card border border-border/60 rounded-lg"
                >
                  <Paperclip size={14} className="text-muted-foreground flex-shrink-0" />
                  <div className="flex-1 min-w-0">
                    <p className="text-sm truncate">{pf.file.name}</p>
                    <p className="text-[10px] text-muted-foreground">{formatFileSize(pf.file.size)}</p>
                  </div>
                  <div className="flex items-center gap-2 flex-shrink-0">
                    {pf.uploading && (
                      <Loader2 size={14} className="animate-spin text-primary" />
                    )}
                    {pf.fileRecordId && !pf.uploading && (
                      <CheckCircle2 size={14} className="text-green-600 dark:text-green-400" />
                    )}
                    {pf.error && (
                      <span className="flex items-center gap-1 text-[10px] text-red-600 dark:text-red-400">
                        <AlertCircle size={12} />
                        {pf.error}
                      </span>
                    )}
                    <button
                      type="button"
                      onClick={(e) => {
                        e.stopPropagation()
                        removeFile(pf.id)
                      }}
                      className="p-1 text-muted-foreground hover:text-red-600 rounded transition-colors"
                      title="Remove"
                    >
                      <X size={14} />
                    </button>
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>

        <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
          <div>
            <label className="block text-sm font-medium mb-1">Type</label>
            <select
              value={type}
              onChange={(e) => setType(e.target.value)}
              className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm"
            >
              {KNOWLEDGE_TYPES.map((t) => (
                <option key={t} value={t}>{t}</option>
              ))}
            </select>
          </div>

          <div>
            <label className="block text-sm font-medium mb-1">Vault</label>
            <select
              value={vaultId}
              onChange={(e) => setVaultId(e.target.value)}
              className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm"
            >
              <option value="">Default vault</option>
              {vaults.data?.vaults.map((v) => (
                <option key={v.id} value={v.id}>{v.name}</option>
              ))}
            </select>
          </div>
        </div>

        <div>
          <label className="block text-sm font-medium mb-1">Tags</label>
          <input
            type="text"
            value={tags}
            onChange={(e) => setTags(e.target.value)}
            placeholder="Comma-separated tags"
            className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm"
          />
        </div>

        <div>
          <label className="block text-sm font-medium mb-1">Source</label>
          <input
            type="text"
            value={source}
            onChange={(e) => setSource(e.target.value)}
            placeholder="URL or reference (optional)"
            className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm"
          />
        </div>

        {createMut.error && (
          <p className="text-red-600 dark:text-red-400 text-sm">
            {createMut.error instanceof Error ? createMut.error.message : 'Failed to create'}
          </p>
        )}

        <button
          type="submit"
          disabled={createMut.isPending || isAnyUploading}
          className="px-6 py-2 bg-primary text-primary-foreground rounded-md text-sm font-medium disabled:opacity-50 hover:opacity-90 transition-opacity"
        >
          {createMut.isPending ? 'Creating...' : isAnyUploading ? 'Uploading files...' : 'Create'}
        </button>
      </form>
    </div>
  )
}
