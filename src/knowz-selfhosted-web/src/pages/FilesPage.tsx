import { Fragment, useState, useRef, useCallback } from 'react'
import { toUserError } from '../lib/toUserError'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { api } from '../lib/api-client'
import type { FileMetadataDto } from '../lib/types'
import {
  FileText,
  Upload,
  Search,
  X,
  Trash2,
  Download,
  ChevronLeft,
  ChevronRight,
  ChevronDown,
  ChevronUp,
  Loader2,
  BookOpen,
  Archive,
  Eye,
  Mic,
  FileSearch,
  Cpu,
  AlertCircle,
} from 'lucide-react'
import { formatFileSize, parseAsUtc, formatDate } from '../lib/format-utils'
import SurfaceCard from '../components/ui/SurfaceCard'

function relativeTime(dateStr: string): string {
  // parseAsUtc handles naive selfhosted timestamps that lack a Z suffix.
  const date = parseAsUtc(dateStr)
  const now = new Date()
  const diff = Math.max(0, now.getTime() - date.getTime())
  const minutes = Math.floor(diff / 60000)
  if (minutes < 1) return 'just now'
  if (minutes < 60) return `${minutes}m ago`
  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours}h ago`
  const days = Math.floor(hours / 24)
  if (days < 30) return `${days}d ago`
  return formatDate(date)
}

function contentTypeBadgeClass(contentType?: string): string {
  if (!contentType) return 'bg-muted text-muted-foreground'
  if (contentType.startsWith('image/'))
    return 'bg-purple-100 dark:bg-purple-950/40 text-purple-700 dark:text-purple-400'
  if (contentType.startsWith('video/'))
    return 'bg-pink-100 dark:bg-pink-950/40 text-pink-700 dark:text-pink-400'
  if (contentType.startsWith('audio/'))
    return 'bg-yellow-100 dark:bg-yellow-950/40 text-yellow-700 dark:text-yellow-400'
  if (contentType === 'application/pdf')
    return 'bg-red-100 dark:bg-red-950/40 text-red-700 dark:text-red-400'
  if (contentType.startsWith('text/'))
    return 'bg-blue-100 dark:bg-blue-950/40 text-blue-700 dark:text-blue-400'
  return 'bg-green-100 dark:bg-green-950/40 text-green-700 dark:text-green-400'
}

function contentTypeLabel(contentType?: string): string {
  if (!contentType) return 'Unknown'
  const parts = contentType.split('/')
  return parts[parts.length - 1].toUpperCase()
}

const EXTRACTION_STATUS_LABELS: Record<number, string> = {
  0: 'Not Started',
  1: 'Processing',
  2: 'Completed',
  3: 'Failed',
}

function extractionStatusBadgeClass(status?: number): string {
  switch (status) {
    case 2: return 'bg-green-100 dark:bg-green-950/40 text-green-700 dark:text-green-400'
    case 1: return 'bg-yellow-100 dark:bg-yellow-950/40 text-yellow-700 dark:text-yellow-400'
    case 3: return 'bg-red-100 dark:bg-red-950/40 text-red-700 dark:text-red-400'
    default: return 'bg-muted text-muted-foreground'
  }
}

const TRUNCATE_LENGTH = 200

function TruncatedText({ text, label, icon }: { text: string; label: string; icon: React.ReactNode }) {
  const [expanded, setExpanded] = useState(false)
  const needsTruncation = text.length > TRUNCATE_LENGTH

  return (
    <div>
      <div className="flex items-center gap-1.5 mb-1 text-xs font-medium text-muted-foreground">
        {icon}
        {label}
      </div>
      <p className="text-sm text-foreground whitespace-pre-wrap break-words">
        {needsTruncation && !expanded ? text.slice(0, TRUNCATE_LENGTH) + '...' : text}
      </p>
      {needsTruncation && (
        <button
          onClick={(e) => { e.stopPropagation(); setExpanded(!expanded) }}
          className="text-xs text-blue-600 dark:text-blue-400 hover:underline mt-1"
        >
          {expanded ? 'Show less' : 'Show more'}
        </button>
      )}
    </div>
  )
}

/**
 * SH_OfflineAiHonesty R8 — the attachment-AI provider is rendered as a product
 * label, never as the internal id. The degraded `NoOp` provider has no label:
 * its badge is hidden entirely (the "No AI analysis available" copy already
 * carries the honest message). Unknown ids fall through to `null` so we never
 * invent a name for a provider we do not know.
 */
const ATTACHMENT_AI_PROVIDER_LABELS: Record<string, string> = {
  AzureOpenAI: 'Azure OpenAI',
  OpenAI: 'OpenAI',
  OpenAiCompatible: 'OpenAI-compatible',
  DocumentIntelligence: 'Document Intelligence',
  AzureVision: 'Azure Vision',
  Local: 'On-device',
}

export function attachmentAIProviderLabel(provider?: string | null): string | null {
  if (!provider || provider === 'NoOp') return null
  return ATTACHMENT_AI_PROVIDER_LABELS[provider] ?? null
}

function FileDetailPanel({ file }: { file: FileMetadataDto }) {
  const hasAiDetails = file.extractedText || file.transcriptionText || file.visionDescription
  const hasAssociations = file.knowledgeId || file.vaultId
  const extractionStatus = file.textExtractionStatus ?? 0
  const hasNoAIData = !hasAiDetails && !file.visionTagsJson && !file.visionObjectsJson && !file.visionExtractedText
  const providerLabel = attachmentAIProviderLabel(file.attachmentAIProvider)

  return (
    <div className="px-4 py-4 bg-muted/50">
      {/* Status badges row */}
      <div className="flex flex-wrap items-center gap-2 mb-4">
        {/* Extraction status badge */}
        <span
          data-testid="extraction-status-badge"
          className={`inline-flex items-center gap-1 px-2 py-0.5 rounded text-xs font-medium ${extractionStatusBadgeClass(extractionStatus)}`}
        >
          <FileSearch size={10} />
          Extraction: {EXTRACTION_STATUS_LABELS[extractionStatus] ?? 'Unknown'}
        </span>

        {/* Provider badge */}
        {providerLabel && (
          <span
            data-testid="provider-badge"
            className="inline-flex items-center gap-1 px-2 py-0.5 rounded text-xs font-medium bg-purple-100 dark:bg-purple-950/40 text-purple-700 dark:text-purple-400"
          >
            <Cpu size={10} />
            {providerLabel}
          </span>
        )}
      </div>

      {hasNoAIData && !hasAssociations ? (
        <div className="text-sm text-muted-foreground flex items-center gap-2" data-testid="no-ai-analysis-message">
          <AlertCircle size={14} className="shrink-0" />
          <span>
            No AI analysis available.
            {file.attachmentAIProvider === 'NoOp' && ' AI analysis is not configured for this deployment.'}
          </span>
        </div>
      ) : (
        <div className={`grid grid-cols-1 gap-6 ${hasAiDetails ? 'md:grid-cols-[1fr_2fr]' : ''}`}>
          {/* Left: Associations */}
          <div>
            <h4 className="text-xs font-semibold text-muted-foreground uppercase tracking-wider mb-3">
              Associations
            </h4>
            {hasAssociations ? (
              <div className="space-y-2">
                {file.knowledgeId && (
                  <div className="flex items-center gap-2 text-sm">
                    <BookOpen size={14} className="text-blue-500 shrink-0" />
                    <span className="text-muted-foreground">Knowledge:</span>
                    <Link
                      to={`/knowledge/${file.knowledgeId}`}
                      onClick={(e) => e.stopPropagation()}
                      className="text-blue-600 dark:text-blue-400 hover:underline truncate"
                    >
                      {file.knowledgeTitle || file.knowledgeId}
                    </Link>
                  </div>
                )}
                {file.vaultId && (
                  <div className="flex items-center gap-2 text-sm">
                    <Archive size={14} className="text-green-500 shrink-0" />
                    <span className="text-muted-foreground">Vault:</span>
                    <Link
                      to={`/vaults/${file.vaultId}`}
                      onClick={(e) => e.stopPropagation()}
                      className="text-green-600 dark:text-green-400 hover:underline truncate"
                    >
                      {file.vaultName || file.vaultId}
                    </Link>
                  </div>
                )}
              </div>
            ) : (
              <p className="text-sm text-muted-foreground">Not attached to any knowledge item.</p>
            )}
          </div>

          {/* Right: AI Details */}
          {hasAiDetails && (
            <div className="min-w-0">
              <h4 className="text-xs font-semibold text-muted-foreground uppercase tracking-wider mb-3">
                AI Details
              </h4>
              <div className="space-y-3">
                {file.extractedText && (
                  <TruncatedText
                    text={file.extractedText}
                    label="Extracted Text"
                    icon={<FileSearch size={12} />}
                  />
                )}
                {file.transcriptionText && (
                  <TruncatedText
                    text={file.transcriptionText}
                    label="Transcription"
                    icon={<Mic size={12} />}
                  />
                )}
                {file.visionDescription && (
                  <TruncatedText
                    text={file.visionDescription}
                    label="Vision Description"
                    icon={<Eye size={12} />}
                  />
                )}
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  )
}

export default function FilesPage() {
  const queryClient = useQueryClient()
  const fileInputRef = useRef<HTMLInputElement>(null)
  const [page, setPage] = useState(1)
  const [pageSize] = useState(20)
  const [search, setSearch] = useState('')
  const [searchInput, setSearchInput] = useState('')
  const [contentTypeFilter, setContentTypeFilter] = useState('')
  const [isDragging, setIsDragging] = useState(false)
  const [expandedFileId, setExpandedFileId] = useState<string | null>(null)

  const { data, isLoading } = useQuery({
    queryKey: ['files', page, pageSize, search, contentTypeFilter],
    queryFn: () => api.listFiles(page, pageSize, search || undefined, contentTypeFilter || undefined),
  })

  const uploadMutation = useMutation({
    mutationFn: (file: File) => api.uploadFile(file),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['files'] })
    },
  })

  const deleteMutation = useMutation({
    mutationFn: (id: string) => api.deleteFile(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['files'] })
    },
  })

  const handleSearch = (e: React.FormEvent) => {
    e.preventDefault()
    setSearch(searchInput)
    setPage(1)
  }

  const handleUploadClick = () => {
    fileInputRef.current?.click()
  }

  const handleFileChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const files = e.target.files
    if (files) {
      Array.from(files).forEach((file) => uploadMutation.mutate(file))
    }
    // Reset input so same file can be uploaded again
    if (fileInputRef.current) {
      fileInputRef.current.value = ''
    }
  }

  const handleDownload = async (id: string, fileName: string) => {
    try {
      const blob = await api.downloadFile(id)
      const url = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = url
      a.download = fileName
      document.body.appendChild(a)
      a.click()
      document.body.removeChild(a)
      URL.revokeObjectURL(url)
    } catch {
      // Error handling is minimal -- could add toast notifications
    }
  }

  const handleDragOver = useCallback((e: React.DragEvent) => {
    e.preventDefault()
    e.stopPropagation()
    setIsDragging(true)
  }, [])

  const handleDragLeave = useCallback((e: React.DragEvent) => {
    e.preventDefault()
    e.stopPropagation()
    setIsDragging(false)
  }, [])

  const handleDrop = useCallback(
    (e: React.DragEvent) => {
      e.preventDefault()
      e.stopPropagation()
      setIsDragging(false)
      const files = e.dataTransfer.files
      if (files) {
        Array.from(files).forEach((file) => uploadMutation.mutate(file))
      }
    },
    [uploadMutation],
  )

  const toggleExpand = (fileId: string) => {
    setExpandedFileId((prev) => (prev === fileId ? null : fileId))
  }

  const CONTENT_TYPE_OPTIONS = ['All', 'application/pdf', 'image/', 'text/', 'video/', 'audio/']
  const hasActiveFilters = Boolean(search || searchInput || contentTypeFilter)
  const clearFilters = () => {
    setSearch('')
    setSearchInput('')
    setContentTypeFilter('')
    setPage(1)
  }

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-end">
        <button
          type="button"
          onClick={handleUploadClick}
          className="inline-flex items-center gap-2 rounded-2xl bg-primary px-4 py-2 text-sm font-semibold text-primary-foreground shadow-sm shadow-primary/20 transition-all duration-200 hover:brightness-110"
        >
          <Upload size={16} />
          Upload
        </button>
      </div>

      {/* Drag and Drop Upload Area */}
      <SurfaceCard
        onDragOver={handleDragOver}
        onDragLeave={handleDragLeave}
        onDrop={handleDrop}
        className={`border-2 border-dashed p-8 text-center transition-colors ${
          isDragging
            ? 'border-blue-500 bg-blue-50 dark:bg-blue-950/20'
            : 'border-input hover:bg-card'
        }`}
      >
        <Upload size={32} className="mx-auto mb-2 text-muted-foreground" />
        <p className="text-sm text-muted-foreground">
          Drag and drop files here, or{' '}
          <button
            type="button"
            onClick={handleUploadClick}
            className="text-blue-600 dark:text-blue-400 hover:underline font-medium"
          >
            browse
          </button>
        </p>
        <input
          ref={fileInputRef}
          type="file"
          multiple
          onChange={handleFileChange}
          className="hidden"
        />
        {uploadMutation.isPending && (
          <div className="mt-2 flex items-center justify-center gap-2 text-sm text-muted-foreground">
            <Loader2 size={16} className="animate-spin" />
            Uploading...
          </div>
        )}
        {uploadMutation.error && (
          <p className="mt-2 text-sm text-red-600 dark:text-red-400">
            {toUserError(uploadMutation.error, 'Upload failed.')}
          </p>
        )}
      </SurfaceCard>

      {/* Search & Filter Bar */}
      <div className="sh-toolbar flex flex-wrap gap-2 items-center p-3">
        <form onSubmit={handleSearch} className="flex-1 flex gap-2">
          <div className="relative flex-1">
            <Search size={16} className="absolute left-3 top-1/2 -translate-y-1/2 text-muted-foreground" />
            <input
              type="text"
              value={searchInput}
              onChange={(e) => setSearchInput(e.target.value)}
              placeholder="Search files..."
              className="w-full pl-10 pr-3 py-2 border border-input rounded-md bg-card text-foreground focus:outline-none focus:ring-2 focus:ring-ring"
            />
          </div>
          <button
            type="submit"
            className="rounded-2xl bg-muted px-4 py-2.5 text-sm font-medium text-foreground transition-colors hover:bg-muted/80"
          >
            Search
          </button>
        </form>
        <select
          value={contentTypeFilter}
          onChange={(e) => {
            setContentTypeFilter(e.target.value)
            setPage(1)
          }}
          className="rounded-2xl border border-input bg-card px-3 py-2.5 text-sm text-foreground focus:outline-none focus:ring-2 focus:ring-ring"
        >
          {CONTENT_TYPE_OPTIONS.map((t) => (
            <option key={t} value={t === 'All' ? '' : t}>
              {t === 'All' ? 'All Types' : t}
            </option>
          ))}
        </select>
        {hasActiveFilters && (
          <button
            type="button"
            onClick={clearFilters}
            className="inline-flex items-center gap-2 rounded-2xl border border-border/70 bg-card/80 px-4 py-2.5 text-sm font-medium transition-colors hover:bg-card"
          >
            <X size={14} />
            Clear
          </button>
        )}
      </div>

      {/* File List */}
      {isLoading ? (
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
          {[1, 2, 3].map((i) => (
            <div key={i} className="sh-surface h-32 animate-pulse" />
          ))}
        </div>
      ) : data && data.items.length > 0 ? (
        <>
          {/* Desktop/tablet: table layout */}
          <SurfaceCard className="hidden overflow-hidden md:block">
            <table className="w-full text-sm table-fixed">
              <colgroup>
                <col className="w-8" />
                <col className="w-[30%]" />
                <col className="w-20" />
                <col className="w-20" />
                <col className="w-36" />
                <col className="w-28" />
                <col className="w-24" />
                <col className="w-20" />
              </colgroup>
              <thead>
                <tr className="border-b border-border/60 bg-muted/60 text-left text-xs font-medium uppercase tracking-wider text-muted-foreground">
                  <th className="px-3 py-3"></th>
                  <th className="px-3 py-3">Name</th>
                  <th className="px-3 py-3 text-center">Type</th>
                  <th className="px-3 py-3 text-right">Size</th>
                  <th className="px-3 py-3">Knowledge</th>
                  <th className="px-3 py-3">Vault</th>
                  <th className="px-3 py-3 text-right">Uploaded</th>
                  <th className="px-3 py-3 text-right">Actions</th>
                </tr>
              </thead>
              <tbody>
                {data.items.map((file) => (
                  <Fragment key={file.id}>
                    <tr
                      onClick={() => toggleExpand(file.id)}
                      className="cursor-pointer select-none border-b border-border/30 transition-colors hover:bg-muted/60"
                    >
                      <td className="px-3 py-3 text-muted-foreground">
                        {expandedFileId === file.id ? <ChevronUp size={14} /> : <ChevronDown size={14} />}
                      </td>
                      <td className="min-w-0 px-3 py-3">
                        <p className="truncate text-sm text-foreground" title={file.fileName}>{file.fileName}</p>
                      </td>
                      <td className="px-3 py-3 text-center">
                        <span
                          className={`inline-flex max-w-full truncate rounded px-2 py-0.5 text-xs font-medium ${contentTypeBadgeClass(file.contentType)}`}
                          title={file.contentType || 'Unknown'}
                        >
                          {contentTypeLabel(file.contentType)}
                        </span>
                      </td>
                      <td className="px-3 py-3 text-right text-sm text-muted-foreground">
                        {formatFileSize(file.sizeBytes)}
                      </td>
                      <td className="px-3 py-3 text-sm">
                        {file.knowledgeId ? (
                          <Link
                            to={`/knowledge/${file.knowledgeId}`}
                            onClick={(e) => e.stopPropagation()}
                            className="block truncate text-blue-600 hover:underline dark:text-blue-400"
                            title={file.knowledgeTitle || 'Untitled'}
                          >
                            {file.knowledgeTitle || 'Untitled'}
                          </Link>
                        ) : (
                          <span className="text-muted-foreground">--</span>
                        )}
                      </td>
                      <td className="px-3 py-3 text-sm">
                        {file.vaultId ? (
                          <Link
                            to={`/vaults/${file.vaultId}`}
                            onClick={(e) => e.stopPropagation()}
                            className="block truncate text-green-600 hover:underline dark:text-green-400"
                            title={file.vaultName || 'Unnamed'}
                          >
                            {file.vaultName || 'Unnamed'}
                          </Link>
                        ) : (
                          <span className="text-muted-foreground">--</span>
                        )}
                      </td>
                      <td className="px-3 py-3 text-right text-xs text-muted-foreground">
                        {relativeTime(file.createdAt)}
                      </td>
                      <td className="px-3 py-3">
                        <div className="flex items-center justify-end gap-1">
                          <button
                            onClick={(e) => { e.stopPropagation(); handleDownload(file.id, file.fileName) }}
                            className="rounded p-1.5 text-muted-foreground transition-colors hover:bg-muted hover:text-blue-600"
                            title="Download"
                          >
                            <Download size={14} />
                          </button>
                          <button
                            onClick={(e) => { e.stopPropagation(); deleteMutation.mutate(file.id) }}
                            className="rounded p-1.5 text-muted-foreground transition-colors hover:bg-muted hover:text-red-600"
                            title="Delete"
                          >
                            <Trash2 size={14} />
                          </button>
                        </div>
                      </td>
                    </tr>
                    {expandedFileId === file.id && (
                      <tr>
                        <td colSpan={8} className="p-0">
                          <FileDetailPanel file={file} />
                        </td>
                      </tr>
                    )}
                  </Fragment>
                ))}
              </tbody>
            </table>
          </SurfaceCard>

          {/* Mobile: card layout */}
          <div className="space-y-3 md:hidden">
            {data.items.map((file) => (
              <SurfaceCard key={file.id} className="overflow-hidden">
                <button
                  type="button"
                  onClick={() => toggleExpand(file.id)}
                  className="w-full p-4 text-left transition-colors hover:bg-muted/50"
                >
                  <div className="flex items-start justify-between gap-2">
                    <p className="min-w-0 flex-1 truncate text-sm font-medium text-foreground" title={file.fileName}>
                      {file.fileName}
                    </p>
                    <span className="shrink-0 text-muted-foreground">
                      {expandedFileId === file.id ? <ChevronUp size={14} /> : <ChevronDown size={14} />}
                    </span>
                  </div>
                  <div className="mt-2 flex flex-wrap items-center gap-2 text-xs">
                    <span
                      className={`inline-flex rounded px-2 py-0.5 font-medium ${contentTypeBadgeClass(file.contentType)}`}
                      title={file.contentType || 'Unknown'}
                    >
                      {contentTypeLabel(file.contentType)}
                    </span>
                    <span className="text-muted-foreground">{formatFileSize(file.sizeBytes)}</span>
                    <span className="text-muted-foreground">&middot;</span>
                    <span className="text-muted-foreground">{relativeTime(file.createdAt)}</span>
                  </div>
                  {(file.knowledgeId || file.vaultId) && (
                    <div className="mt-2 space-y-1 text-xs">
                      {file.knowledgeId && (
                        <p className="truncate">
                          <span className="text-muted-foreground">Knowledge: </span>
                          <Link
                            to={`/knowledge/${file.knowledgeId}`}
                            onClick={(e) => e.stopPropagation()}
                            className="text-blue-600 hover:underline dark:text-blue-400"
                          >
                            {file.knowledgeTitle || 'Untitled'}
                          </Link>
                        </p>
                      )}
                      {file.vaultId && (
                        <p className="truncate">
                          <span className="text-muted-foreground">Vault: </span>
                          <Link
                            to={`/vaults/${file.vaultId}`}
                            onClick={(e) => e.stopPropagation()}
                            className="text-green-600 hover:underline dark:text-green-400"
                          >
                            {file.vaultName || 'Unnamed'}
                          </Link>
                        </p>
                      )}
                    </div>
                  )}
                </button>
                <div className="flex items-center justify-end gap-1 border-t border-border/60 bg-muted/30 px-3 py-2">
                  <button
                    onClick={() => handleDownload(file.id, file.fileName)}
                    className="inline-flex items-center gap-1.5 rounded-lg px-2.5 py-1.5 text-xs text-muted-foreground transition-colors hover:bg-muted hover:text-blue-600"
                  >
                    <Download size={14} /> Download
                  </button>
                  <button
                    onClick={() => deleteMutation.mutate(file.id)}
                    className="inline-flex items-center gap-1.5 rounded-lg px-2.5 py-1.5 text-xs text-muted-foreground transition-colors hover:bg-muted hover:text-red-600"
                  >
                    <Trash2 size={14} /> Delete
                  </button>
                </div>
                {expandedFileId === file.id && <FileDetailPanel file={file} />}
              </SurfaceCard>
            ))}
          </div>
        </>
      ) : (
        <SurfaceCard className="p-12 text-center text-muted-foreground">
          <FileText size={48} className="mx-auto mb-4 opacity-50" />
          <p className="text-lg font-medium">No files</p>
          <p className="text-sm mt-1">Upload your first file using the area above.</p>
        </SurfaceCard>
      )}

      {/* Pagination */}
      {data && data.totalPages > 1 && (
        <div className="sh-toolbar flex items-center justify-between p-3">
          <p className="text-sm text-muted-foreground">
            Showing {(page - 1) * pageSize + 1}-{Math.min(page * pageSize, data.totalItems)} of{' '}
            {data.totalItems}
          </p>
          <div className="flex items-center gap-2">
            <button
              onClick={() => setPage((p) => Math.max(1, p - 1))}
              disabled={page <= 1}
              className="p-2 rounded hover:bg-muted disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
            >
              <ChevronLeft size={16} />
            </button>
            <span className="text-sm text-foreground">
              Page {page} of {data.totalPages}
            </span>
            <button
              onClick={() => setPage((p) => Math.min(data.totalPages, p + 1))}
              disabled={page >= data.totalPages}
              className="p-2 rounded hover:bg-muted disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
            >
              <ChevronRight size={16} />
            </button>
          </div>
        </div>
      )}
    </div>
  )
}
