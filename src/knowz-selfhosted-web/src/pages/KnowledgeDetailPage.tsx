import { useState, useRef, useMemo, useEffect, useCallback } from 'react'
import { useParams, useNavigate, Link } from 'react-router-dom'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { api, ApiError } from '../lib/api-client'
import {
  ArrowLeft, Pencil, Trash2, Save, X, Paperclip, Download, Upload,
  Loader2, RefreshCw, History, RotateCcw, ChevronDown, ChevronRight,
  Sparkles, FileText, PanelRightClose, PanelRightOpen, Eye,
  MessageCircle, Send, Maximize2, Minimize2, GitCommit,
} from 'lucide-react'
import CommentSection from '../components/CommentSection'
import MarkdownContent from '../components/MarkdownContent'
import ContentTabs from '../components/ContentTabs'
import DetailSidebar from '../components/DetailSidebar'
import EnrichmentBanner from '../components/EnrichmentBanner'
import AttachmentViewer from '../components/AttachmentViewer'
import type { TabId } from '../components/ContentTabs'
import type { FileMetadataDto, KnowledgeVersion, CommitHistoryEntry, CommitHistoryResponse } from '../lib/types'
import { formatFileSize } from '../lib/format-utils'
import { useFormatters } from '../hooks/useFormatters'
import { looksLikeEncryptedEnvelope } from '../lib/encrypted-envelope'

export default function KnowledgeDetailPage() {
  const { id } = useParams<{ id: string }>()
  const navigate = useNavigate()
  const queryClient = useQueryClient()

  const [editing, setEditing] = useState(false)
  const [editTitle, setEditTitle] = useState('')
  const [editContent, setEditContent] = useState('')
  const [editTags, setEditTags] = useState('')
  const [editSource, setEditSource] = useState('')
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false)
  const [reprocessMsg, setReprocessMsg] = useState<{ type: 'success' | 'error'; text: string } | null>(null)
  const [editTab, setEditTab] = useState<'write' | 'preview'>('write')
  const [showAttachPicker, setShowAttachPicker] = useState(false)
  const [activeTab, setActiveTab] = useState<TabId>('summary')
  // SH_RightPanelMemory: persist collapsed state across knowledge items.
  const [sidebarOpen, setSidebarOpen] = useState<boolean>(() => {
    if (typeof window === 'undefined') return true
    return window.localStorage.getItem('selfhosted-knowledge-detail-rightpanel-collapsed') !== 'true'
  })
  useEffect(() => {
    if (typeof window === 'undefined') return
    // Storage key stores collapsed state (inverse of sidebarOpen)
    window.localStorage.setItem(
      'selfhosted-knowledge-detail-rightpanel-collapsed',
      String(!sidebarOpen),
    )
  }, [sidebarOpen])
  const [expandedVersion, setExpandedVersion] = useState<number | null>(null)
  const [showRestoreConfirm, setShowRestoreConfirm] = useState<number | null>(null)
  const [viewingAttachment, setViewingAttachment] = useState<FileMetadataDto | null>(null)
  const [chatOpen, setChatOpen] = useState(false)
  const [showRefinementModal, setShowRefinementModal] = useState(false)
  const [refinementGuidance, setRefinementGuidance] = useState('')
  const [commitPage, setCommitPage] = useState(1)
  const attachFileInputRef = useRef<HTMLInputElement>(null)

  const { data, isLoading, error } = useQuery({
    queryKey: ['knowledge', id],
    queryFn: () => api.getKnowledge(id!),
    enabled: !!id,
  })

  const { data: attachments, isLoading: attachmentsLoading } = useQuery({
    queryKey: ['knowledge-attachments', id],
    queryFn: () => api.getKnowledgeAttachments(id!),
    enabled: !!id,
  })

  const { data: availableFiles } = useQuery({
    queryKey: ['files-for-attach'],
    queryFn: () => api.listFiles(1, 50),
    enabled: showAttachPicker,
  })

  const { data: versions, isLoading: versionsLoading, error: versionsError } = useQuery({
    queryKey: ['knowledge-versions', id],
    queryFn: () => api.getVersionHistory(id!),
    enabled: !!id && activeTab === 'history',
  })

  // Commit history — lazy fetch when the Commits tab is active, only for Code-type items
  const primaryVaultId = data?.vaults?.find((v) => v.isPrimary)?.id ?? data?.vaults?.[0]?.id
  const {
    data: commitHistory,
    isLoading: commitsLoading,
    error: commitsError,
  } = useQuery<CommitHistoryResponse>({
    queryKey: ['knowledge-commit-history', id, primaryVaultId, commitPage],
    queryFn: () => api.getKnowledgeCommitHistory(primaryVaultId!, id!, commitPage, 20),
    enabled:
      !!id &&
      !!primaryVaultId &&
      activeTab === 'commit-history' &&
      data?.type === 'Code',
    staleTime: 30_000,
  })

  // Reset paging when the user navigates away from the Commits tab
  useEffect(() => {
    if (activeTab !== 'commit-history') setCommitPage(1)
  }, [activeTab])

  // Enrichment status polling — resilient to 404 if endpoint not yet deployed
  const { data: enrichmentStatus } = useQuery({
    queryKey: ['enrichment-status', id],
    queryFn: async () => {
      try {
        return await api.getEnrichmentStatus(id!)
      } catch (err) {
        if (err instanceof ApiError && err.status === 404) {
          return null
        }
        throw err
      }
    },
    refetchInterval: (query) => {
      const status = query.state.data?.status
      return status === 'pending' || status === 'processing' ? 3000 : false
    },
    enabled: !!id,
    retry: false,
  })

  // When enrichment completes, reload knowledge data to get fresh AI summary
  const enrichmentStatusValue = enrichmentStatus?.status
  const prevEnrichmentRef = useRef<string | undefined>(undefined)
  if (
    prevEnrichmentRef.current &&
    (prevEnrichmentRef.current === 'pending' || prevEnrichmentRef.current === 'processing') &&
    enrichmentStatusValue === 'completed'
  ) {
    queryClient.invalidateQueries({ queryKey: ['knowledge', id] })
  }
  prevEnrichmentRef.current = enrichmentStatusValue ?? undefined

  const restoreMut = useMutation({
    mutationFn: (versionNumber: number) => api.restoreVersion(id!, versionNumber),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['knowledge', id] })
      queryClient.invalidateQueries({ queryKey: ['knowledge-versions', id] })
      setShowRestoreConfirm(null)
    },
  })

  const updateMut = useMutation({
    mutationFn: () =>
      api.updateKnowledge(id!, {
        title: editTitle,
        content: editContent,
        source: editSource || undefined,
        tags: editTags
          .split(',')
          .map((t) => t.trim())
          .filter(Boolean),
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['knowledge', id] })
      queryClient.invalidateQueries({ queryKey: ['enrichment-status', id] })
      setEditing(false)
    },
  })

  const reprocessMut = useMutation({
    mutationFn: () => api.reprocessKnowledge(id!),
    onSuccess: (data) => {
      queryClient.invalidateQueries({ queryKey: ['knowledge', id] })
      setReprocessMsg(
        data.reprocessed
          ? { type: 'success', text: 'Reprocessing complete. Item has been re-indexed and queued for enrichment.' }
          : { type: 'error', text: 'Reprocessing failed. Check that AI services are configured.' }
      )
    },
    onError: (err) => {
      setReprocessMsg({ type: 'error', text: err instanceof Error ? err.message : 'Reprocess failed' })
    },
  })

  const refinementMut = useMutation({
    mutationFn: () => api.updateKnowledge(id!, { summaryRefinementGuidance: refinementGuidance }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['knowledge', id] })
      queryClient.invalidateQueries({ queryKey: ['enrichment-status', id] })
      setShowRefinementModal(false)
      reprocessMut.mutate()
    },
  })

  const deleteMut = useMutation({
    mutationFn: () => api.deleteKnowledge(id!),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['knowledge'] })
      navigate('/knowledge')
    },
  })

  const attachMut = useMutation({
    mutationFn: (fileRecordId: string) => api.attachFileToKnowledge(id!, fileRecordId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['knowledge-attachments', id] })
      queryClient.invalidateQueries({ queryKey: ['knowledge', id] })
      queryClient.invalidateQueries({ queryKey: ['enrichment-status', id] })
      setShowAttachPicker(false)
    },
  })

  // WorkGroupID: kc-feat-file-delete-orphan-policy-20260616-140729 — FEAT_SelfHostedFileCleanupPolicy (N6).
  // Config-backed cleanup policy. Cached for the component lifetime (a single GET). Tells us whether
  // to prompt the user on detach (PromptUser) or proceed silently (AutoCleanup / PreserveAlways).
  const { data: cleanupPolicy } = useQuery({
    queryKey: ['file-cleanup-policy'],
    queryFn: () => api.getFileCleanupPolicy(),
    staleTime: 5 * 60 * 1000,
  })

  const detachMut = useMutation({
    mutationFn: ({ fileRecordId, deleteFiles }: { fileRecordId: string; deleteFiles?: boolean }) =>
      api.detachFileFromKnowledge(id!, fileRecordId, deleteFiles),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['knowledge-attachments', id] })
      queryClient.invalidateQueries({ queryKey: ['knowledge', id] })
      queryClient.invalidateQueries({ queryKey: ['enrichment-status', id] })
    },
  })

  // Resolve the effective detach: for PromptUser, confirm whether to ALSO delete the underlying file
  // (self-hosted deletes the blob IMMEDIATELY — no recovery window). For AutoCleanup/PreserveAlways
  // the server resolves the behavior, so no param is sent.
  const handleDetach = (fileRecordId: string, fileName: string) => {
    if (cleanupPolicy?.mode === 'PromptUser') {
      const alsoDelete = window.confirm(
        `Remove "${fileName}" from this item?\n\n` +
          'Click OK to ALSO permanently delete the file itself (deleted immediately, ' +
          'cannot be undone; files used by other items are always kept).\n\n' +
          'Click Cancel to keep the file and only remove it from this item.'
      )
      detachMut.mutate({ fileRecordId, deleteFiles: alsoDelete })
    } else {
      // AutoCleanup / PreserveAlways — server resolves; no deleteFiles param.
      detachMut.mutate({ fileRecordId })
    }
  }

  const uploadAndAttachMut = useMutation({
    mutationFn: async (file: File) => {
      const result = await api.uploadFile(file)
      await api.attachFileToKnowledge(id!, result.fileRecordId)
      return result
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['knowledge-attachments', id] })
      queryClient.invalidateQueries({ queryKey: ['knowledge', id] })
      queryClient.invalidateQueries({ queryKey: ['enrichment-status', id] })
      queryClient.invalidateQueries({ queryKey: ['files'] })
    },
  })

  const handleAttachUpload = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const files = e.target.files
    if (files) {
      for (const file of Array.from(files)) {
        try {
          await uploadAndAttachMut.mutateAsync(file)
        } catch {
          // Error is captured in uploadAndAttachMut.error for display
        }
      }
    }
    if (attachFileInputRef.current) {
      attachFileInputRef.current.value = ''
    }
  }

  const handleDownloadAttachment = async (fileId: string, fileName: string) => {
    try {
      const blob = await api.downloadFile(fileId)
      const url = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = url
      a.download = fileName
      document.body.appendChild(a)
      a.click()
      document.body.removeChild(a)
      URL.revokeObjectURL(url)
    } catch {
      // Minimal error handling
    }
  }

  const startEditing = () => {
    if (!data) return
    setEditTitle(data.title)
    setEditContent(data.content)
    setEditTags(data.tags.join(', '))
    setEditSource(data.source || '')
    setEditTab('write')
    setEditing(true)
  }

  const attachmentCount = attachments?.length ?? 0

  useEffect(() => {
    setRefinementGuidance(data?.summaryRefinementGuidance ?? '')
  }, [data?.summaryRefinementGuidance])

  const contentTabs = useMemo(() => {
    const tabs = [
      { id: 'summary' as TabId, label: 'AI Summary', icon: Sparkles },
      { id: 'original' as TabId, label: 'Original', icon: FileText },
      { id: 'attachments' as TabId, label: 'Attachments', icon: Paperclip, count: attachmentCount },
      { id: 'history' as TabId, label: 'History', icon: History },
    ]
    if (data?.type === 'Code' && (data.vaults?.length ?? 0) > 0) {
      tabs.push({ id: 'commit-history' as TabId, label: 'Commits', icon: GitCommit })
    }
    return tabs
  }, [attachmentCount, data?.type, data?.vaults?.length])

  if (isLoading) {
    return (
      <div className="space-y-4">
        <div className="h-8 w-48 bg-muted rounded animate-pulse" />
        <div className="h-64 bg-muted rounded animate-pulse" />
      </div>
    )
  }

  if (error) {
    return (
      <div className="space-y-4">
        <Link to="/knowledge" className="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground transition-colors">
          <ArrowLeft size={16} /> Back to Knowledge
        </Link>
        <p className="text-red-600 dark:text-red-400">
          {error instanceof Error ? error.message : 'Failed to load item'}
        </p>
      </div>
    )
  }

  if (!data) return null

  return (
    <div className="mx-auto max-w-4xl space-y-4">
      <Link
        to="/knowledge"
        className="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground transition-colors"
      >
        <ArrowLeft size={16} /> Back to Knowledge
      </Link>

      {editing ? (
        /* --- EDIT MODE (unchanged) --- */
        <div className="space-y-4">
          <input
            type="text"
            value={editTitle}
            onChange={(e) => setEditTitle(e.target.value)}
            className="w-full px-3 py-2 border border-input rounded-md bg-card text-lg font-semibold"
          />
          <div className="border border-input rounded-md overflow-hidden">
            <div className="flex border-b border-input bg-muted">
              <button
                type="button"
                onClick={() => setEditTab('write')}
                className={`px-4 py-2 text-sm font-medium transition-colors ${
                  editTab === 'write'
                    ? 'text-foreground bg-card border-b-2 border-primary'
                    : 'text-muted-foreground hover:text-foreground'
                }`}
              >
                Write
              </button>
              <button
                type="button"
                onClick={() => setEditTab('preview')}
                className={`px-4 py-2 text-sm font-medium transition-colors ${
                  editTab === 'preview'
                    ? 'text-foreground bg-card border-b-2 border-primary'
                    : 'text-muted-foreground hover:text-foreground'
                }`}
              >
                Preview
              </button>
            </div>
            {editTab === 'write' ? (
              <textarea
                value={editContent}
                onChange={(e) => setEditContent(e.target.value)}
                rows={16}
                className="w-full px-3 py-2 bg-card text-sm font-mono border-0 focus:ring-0 focus:outline-none"
              />
            ) : (
              <div className="px-3 py-2 bg-card min-h-[384px]">
                {editContent.trim() ? (
                  <MarkdownContent content={editContent} />
                ) : (
                  <p className="text-muted-foreground text-sm italic">
                    Nothing to preview
                  </p>
                )}
              </div>
            )}
          </div>
          <input
            type="text"
            value={editSource}
            onChange={(e) => setEditSource(e.target.value)}
            placeholder="Source (optional)"
            className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm"
          />
          <input
            type="text"
            value={editTags}
            onChange={(e) => setEditTags(e.target.value)}
            placeholder="Tags (comma-separated)"
            className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm"
          />
          {updateMut.error && (
            <p className="text-red-600 dark:text-red-400 text-sm">
              {updateMut.error instanceof Error ? updateMut.error.message : 'Update failed'}
            </p>
          )}
          <div className="flex gap-2">
            <button
              onClick={() => updateMut.mutate()}
              disabled={updateMut.isPending}
              className="inline-flex items-center gap-2 px-4 py-2 bg-primary text-primary-foreground rounded-md text-sm font-medium disabled:opacity-50 transition-colors"
            >
              <Save size={16} /> {updateMut.isPending ? 'Saving...' : 'Save'}
            </button>
            <button
              onClick={() => setEditing(false)}
              className="inline-flex items-center gap-2 px-4 py-2 border border-input rounded-md text-sm font-medium transition-colors"
            >
              <X size={16} /> Cancel
            </button>
          </div>
        </div>
      ) : (
        /* --- VIEW MODE: Two-column layout --- */
        <div className="space-y-4">
          {/* Header: Title + Actions */}
          <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between sm:gap-4">
            <h1 className="min-w-0 break-words text-2xl font-bold">{data.title}</h1>
            <div className="flex flex-wrap gap-2 sm:flex-shrink-0 sm:justify-end">
              <button
                onClick={startEditing}
                className="inline-flex items-center gap-1.5 px-3 py-1.5 border border-input rounded-md text-sm hover:bg-muted transition-colors"
              >
                <Pencil size={14} /> Edit
              </button>
              <button
                onClick={() => { setReprocessMsg(null); reprocessMut.mutate() }}
                disabled={reprocessMut.isPending}
                className="inline-flex items-center gap-1.5 px-3 py-1.5 border border-input rounded-md text-sm disabled:opacity-50 hover:bg-muted transition-colors"
              >
                <RefreshCw size={14} className={reprocessMut.isPending ? 'animate-spin' : ''} />
                {reprocessMut.isPending ? 'Reprocessing...' : 'Reprocess'}
              </button>
              <button
                onClick={() => setSidebarOpen(!sidebarOpen)}
                className="hidden lg:inline-flex items-center gap-1.5 px-2 py-1.5 border border-input rounded-md text-sm hover:bg-muted transition-colors"
                title={sidebarOpen ? 'Hide sidebar' : 'Show sidebar'}
              >
                {sidebarOpen ? <PanelRightClose size={14} /> : <PanelRightOpen size={14} />}
              </button>
              <button
                onClick={() => { setChatOpen(false); setShowDeleteConfirm(true) }}
                className="inline-flex items-center gap-1.5 px-3 py-1.5 border border-red-300 dark:border-red-700 text-red-600 dark:text-red-400 rounded-md text-sm hover:bg-red-50 dark:hover:bg-red-950/30 transition-colors"
              >
                <Trash2 size={14} /> Delete
              </button>
            </div>
          </div>

          {reprocessMsg && (
            <p className={`text-sm ${reprocessMsg.type === 'success' ? 'text-green-600 dark:text-green-400' : 'text-red-600 dark:text-red-400'}`}>
              {reprocessMsg.text}
            </p>
          )}

          <EnrichmentBanner status={enrichmentStatus?.status ?? null} />

          {/* Two-column grid */}
          <div className={`grid gap-6 ${sidebarOpen ? 'grid-cols-1 lg:grid-cols-[1fr_300px]' : 'grid-cols-1'}`}>
            {/* Left column: Content with tabs */}
            <div className="min-w-0 space-y-4">
              <ContentTabs
                activeTab={activeTab}
                onTabChange={setActiveTab}
                tabs={contentTabs}
              />

              <div className="animate-fade-in">
                {activeTab === 'summary' && (
                  <SummaryTabContent
                    summary={data.summary}
                    reprocessMut={reprocessMut}
                    onRefine={() => { setChatOpen(false); setShowRefinementModal(true) }}
                  />
                )}

                {activeTab === 'original' && (
                  <div className="bg-card border border-border/60 rounded-xl p-5">
                    {looksLikeEncryptedEnvelope(data.content) ? (
                      <div data-testid="encrypted-lock" className="space-y-2">
                        <p className="text-sm font-medium">Encrypted — unlock with your key</p>
                        <p className="text-sm text-muted-foreground">
                          This item was pulled from Knowz as ciphertext. It is not shown as markdown.
                          Open Destinations after unlocking locally with your key file.
                        </p>
                        <Link to="/unlock" className="text-sm text-primary underline">
                          Unlock locally
                        </Link>
                      </div>
                    ) : (
                      <MarkdownContent content={data.content} />
                    )}
                  </div>
                )}

                {activeTab === 'attachments' && (
                  <AttachmentsTabContent
                    attachments={attachments}
                    attachmentsLoading={attachmentsLoading}
                    attachFileInputRef={attachFileInputRef}
                    handleAttachUpload={handleAttachUpload}
                    handleDownloadAttachment={handleDownloadAttachment}
                    uploadAndAttachMut={uploadAndAttachMut}
                    onDetach={handleDetach}
                    detachPending={detachMut.isPending}
                    attachMut={attachMut}
                    showAttachPicker={showAttachPicker}
                    setShowAttachPicker={setShowAttachPicker}
                    availableFiles={availableFiles}
                    onViewAttachment={setViewingAttachment}
                  />
                )}

                {activeTab === 'history' && (
                  <VersionHistoryPanel
                    versions={versions}
                    isLoading={versionsLoading}
                    error={versionsError}
                    expandedVersion={expandedVersion}
                    onToggleExpand={(vn) => setExpandedVersion(expandedVersion === vn ? null : vn)}
                    showRestoreConfirm={showRestoreConfirm}
                    onShowRestoreConfirm={setShowRestoreConfirm}
                    restoreMut={restoreMut}
                  />
                )}

                {activeTab === 'commit-history' && (
                  <CommitHistoryPanel
                    entries={commitHistory?.items ?? []}
                    total={commitHistory?.total ?? 0}
                    page={commitPage}
                    pageSize={20}
                    isLoading={commitsLoading}
                    error={commitsError as Error | null}
                    onPageChange={setCommitPage}
                  />
                )}
              </div>

              {/* Contributions Section - always visible below tabs */}
              <CommentSection knowledgeId={id!} />
            </div>

            {/* Right column: Sidebar */}
            {sidebarOpen && (
              <aside className="animate-fade-in">
                <DetailSidebar
                  briefSummary={data.briefSummary}
                  tags={data.tags}
                  type={data.type}
                  vaults={data.vaults}
                  source={data.source}
                  createdAt={data.createdAt}
                  updatedAt={data.updatedAt}
                  isIndexed={data.isIndexed}
                  indexedAt={data.indexedAt}
                  attachmentCount={attachmentCount}
                />
              </aside>
            )}
          </div>
        </div>
      )}

      {/* Delete Confirmation Modal */}
      {showDeleteConfirm && (
        <div className="fixed inset-0 z-[60] flex items-center justify-center bg-black/50 p-4">
          <div className="max-h-[calc(100dvh-2rem)] w-full max-w-sm space-y-4 overflow-y-auto rounded-xl bg-card p-5 shadow-sm sm:p-6">
            <h2 className="text-lg font-semibold">Delete Knowledge Item?</h2>
            <p className="text-sm text-muted-foreground">
              This will permanently delete &quot;{data.title}&quot;. This action cannot be undone.
            </p>
            {deleteMut.error && (
              <p className="text-red-600 dark:text-red-400 text-sm">
                {deleteMut.error instanceof Error ? deleteMut.error.message : 'Delete failed'}
              </p>
            )}
            <div className="flex gap-2 justify-end">
              <button
                onClick={() => setShowDeleteConfirm(false)}
                className="px-4 py-2 border border-input rounded-md text-sm transition-colors"
              >
                Cancel
              </button>
              <button
                onClick={() => deleteMut.mutate()}
                disabled={deleteMut.isPending}
                className="px-4 py-2 bg-red-600 text-white rounded-md text-sm font-medium disabled:opacity-50"
              >
                {deleteMut.isPending ? 'Deleting...' : 'Delete'}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Refine Summary Modal */}
      {showRefinementModal && (
        <div className="fixed inset-0 z-[60] flex items-center justify-center bg-black/50 p-4">
          <div className="max-h-[calc(100dvh-2rem)] w-full max-w-md space-y-4 overflow-y-auto rounded-xl bg-card p-5 shadow-sm sm:p-6">
            <h2 className="text-lg font-semibold flex items-center gap-2">
              <Sparkles size={18} className="text-primary" /> Refine Summary
            </h2>
            <p className="text-sm text-muted-foreground">
              Provide guidance for how the AI should summarise this item. Leave empty to use the default.
            </p>
            <textarea
              value={refinementGuidance}
              onChange={(e) => setRefinementGuidance(e.target.value)}
              maxLength={1000}
              rows={4}
              placeholder="e.g. Use bullet points. Focus on dates and people. Keep under 3 sentences."
              className="w-full rounded-md border border-input bg-background px-3 py-2 text-sm resize-none focus:outline-none focus:ring-2 focus:ring-ring"
            />
            <div className="text-xs text-muted-foreground text-right">{refinementGuidance.length}/1000</div>
            <div className="flex gap-2 justify-end">
              <button
                onClick={() => setShowRefinementModal(false)}
                className="px-4 py-2 border border-input rounded-md text-sm transition-colors"
              >
                Cancel
              </button>
              <button
                onClick={() => refinementMut.mutate()}
                disabled={refinementMut.isPending}
                className="px-4 py-2 bg-primary text-primary-foreground rounded-md text-sm font-medium disabled:opacity-50 hover:opacity-90 transition-opacity"
              >
                {refinementMut.isPending ? 'Saving...' : 'Save & Regenerate'}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Attachment Viewer Modal */}
      {viewingAttachment && (
        <AttachmentViewer
          file={viewingAttachment}
          onClose={() => setViewingAttachment(null)}
          onDownload={handleDownloadAttachment}
        />
      )}

      {/* Floating Chat Button */}
      {!editing && (
        <button
          onClick={() => setChatOpen(!chatOpen)}
          className="fixed bottom-4 right-4 z-40 flex h-12 w-12 items-center justify-center rounded-full bg-primary text-primary-foreground shadow-lg transition-all hover:opacity-90 sm:bottom-6 sm:right-6 sm:h-14 sm:w-14"
          title="Chat with this knowledge item"
        >
          {chatOpen ? <X size={22} /> : <MessageCircle size={22} />}
        </button>
      )}

      {/* Chat Panel */}
      {chatOpen && !editing && (
        <KnowledgeChatPanel
          knowledgeId={id!}
          knowledgeTitle={data.title}
          onClose={() => setChatOpen(false)}
        />
      )}
    </div>
  )
}

// --- Summary Tab ---

function SummaryTabContent({
  summary,
  reprocessMut,
  onRefine,
}: {
  summary?: string
  reprocessMut: { mutate: () => void; isPending: boolean }
  onRefine?: () => void
}) {
  if (!summary) {
    return (
      <div className="text-center py-12 space-y-3">
        <Sparkles size={32} className="mx-auto text-muted-foreground" />
        <p className="text-muted-foreground text-sm">No AI summary available yet.</p>
        <button
          onClick={() => reprocessMut.mutate()}
          disabled={reprocessMut.isPending}
          className="inline-flex items-center gap-1.5 px-4 py-2 bg-primary text-primary-foreground rounded-md text-sm font-medium disabled:opacity-50 hover:opacity-90 transition-opacity"
        >
          <RefreshCw size={14} className={reprocessMut.isPending ? 'animate-spin' : ''} />
          {reprocessMut.isPending ? 'Processing...' : 'Generate Summary'}
        </button>
      </div>
    )
  }

  return (
    <div className="bg-card border border-border/60 rounded-xl p-5">
      {onRefine && (
        <div className="mb-3">
          <button
            type="button"
            onClick={onRefine}
            className="flex items-center gap-1 rounded-md border border-primary/30 bg-primary/5 px-2.5 py-1 text-[10px] font-medium text-primary hover:bg-primary/10 transition-colors"
          >
            <Sparkles size={12} /> Refine summary
          </button>
        </div>
      )}
      <MarkdownContent content={summary} />
    </div>
  )
}

// --- Attachments Tab ---

function AttachmentsTabContent({
  attachments,
  attachmentsLoading,
  attachFileInputRef,
  handleAttachUpload,
  handleDownloadAttachment,
  uploadAndAttachMut,
  onDetach,
  detachPending,
  attachMut,
  showAttachPicker,
  setShowAttachPicker,
  availableFiles,
  onViewAttachment,
}: {
  attachments: FileMetadataDto[] | undefined
  attachmentsLoading: boolean
  attachFileInputRef: React.RefObject<HTMLInputElement | null>
  handleAttachUpload: (e: React.ChangeEvent<HTMLInputElement>) => void
  handleDownloadAttachment: (fileId: string, fileName: string) => void
  uploadAndAttachMut: { isPending: boolean; error: Error | null }
  onDetach: (fileRecordId: string, fileName: string) => void
  detachPending: boolean
  attachMut: { mutate: (id: string) => void; isPending: boolean }
  showAttachPicker: boolean
  setShowAttachPicker: (v: boolean) => void
  availableFiles: { items: FileMetadataDto[] } | undefined
  onViewAttachment: (file: FileMetadataDto) => void
}) {
  return (
    <div className="space-y-4">
      {/* Upload actions */}
      <div className="flex gap-2">
        <button
          type="button"
          onClick={() => attachFileInputRef.current?.click()}
          className="inline-flex items-center gap-1.5 px-3 py-1.5 text-sm bg-primary text-primary-foreground rounded-md hover:opacity-90 transition-opacity"
        >
          <Upload size={14} />
          Upload & Attach
        </button>
        <input
          ref={attachFileInputRef}
          type="file"
          multiple
          onChange={handleAttachUpload}
          className="hidden"
        />
        <button
          type="button"
          onClick={() => setShowAttachPicker(!showAttachPicker)}
          className="inline-flex items-center gap-1.5 px-3 py-1.5 text-sm border border-input rounded-md hover:bg-muted transition-colors"
        >
          <Paperclip size={14} />
          Attach Existing
        </button>
      </div>

      {uploadAndAttachMut.isPending && (
        <div className="flex items-center gap-2 text-xs text-muted-foreground">
          <Loader2 size={14} className="animate-spin" />
          Uploading and attaching...
        </div>
      )}

      {uploadAndAttachMut.error && (
        <p className="text-xs text-red-600 dark:text-red-400">
          {uploadAndAttachMut.error instanceof Error ? uploadAndAttachMut.error.message : 'Upload failed'}
        </p>
      )}

      {/* Attach from existing files picker */}
      {showAttachPicker && (
        <div className="border border-border/60 rounded-lg p-3 space-y-2 bg-card">
          <p className="text-xs font-medium text-muted-foreground">
            Select an existing file to attach:
          </p>
          {availableFiles && availableFiles.items.length > 0 ? (
            <div className="max-h-40 overflow-y-auto space-y-1">
              {availableFiles.items
                .filter(
                  (f) => !attachments?.some((a: FileMetadataDto) => a.id === f.id),
                )
                .map((file) => (
                  <button
                    key={file.id}
                    onClick={() => attachMut.mutate(file.id)}
                    disabled={attachMut.isPending}
                    className="w-full flex items-center gap-2 px-2 py-1.5 text-left text-sm rounded hover:bg-muted disabled:opacity-50 transition-colors"
                  >
                    <Paperclip size={12} className="text-muted-foreground" />
                    <span className="truncate">{file.fileName}</span>
                    <span className="text-xs text-muted-foreground ml-auto">
                      {formatFileSize(file.sizeBytes)}
                    </span>
                  </button>
                ))}
            </div>
          ) : (
            <p className="text-xs text-muted-foreground">No files available.</p>
          )}
          <button
            type="button"
            onClick={() => setShowAttachPicker(false)}
            className="text-xs text-muted-foreground hover:text-foreground transition-colors"
          >
            Close
          </button>
        </div>
      )}

      {/* File list */}
      {attachmentsLoading ? (
        <div className="flex items-center justify-center py-8">
          <Loader2 size={16} className="animate-spin text-muted-foreground" />
        </div>
      ) : attachments && attachments.length > 0 ? (
        <div className="border border-border/60 rounded-lg divide-y divide-border/40">
          {attachments.map((file: FileMetadataDto) => (
            <div
              key={file.id}
              className="flex items-center justify-between py-2.5 px-3 hover:bg-muted/30 transition-colors"
            >
              <div className="flex items-center gap-2.5 min-w-0">
                <Paperclip size={14} className="text-muted-foreground flex-shrink-0" />
                <div className="min-w-0">
                  <span className="text-sm truncate block">{file.fileName}</span>
                  <span className="text-[10px] text-muted-foreground">
                    {formatFileSize(file.sizeBytes)}
                    {file.contentType && ` \u00b7 ${file.contentType}`}
                  </span>
                </div>
              </div>
              <div className="flex items-center gap-1 flex-shrink-0">
                <button
                  onClick={() => onViewAttachment(file)}
                  className="p-1.5 text-muted-foreground hover:text-purple-600 rounded hover:bg-muted transition-colors"
                  title="View"
                >
                  <Eye size={14} />
                </button>
                <button
                  onClick={() => handleDownloadAttachment(file.id, file.fileName)}
                  className="p-1.5 text-muted-foreground hover:text-blue-600 rounded hover:bg-muted transition-colors"
                  title="Download"
                >
                  <Download size={14} />
                </button>
                <button
                  onClick={() => onDetach(file.id, file.fileName)}
                  disabled={detachPending}
                  className="p-1.5 text-muted-foreground hover:text-red-600 rounded hover:bg-muted transition-colors disabled:opacity-50"
                  title="Detach"
                >
                  <X size={14} />
                </button>
              </div>
            </div>
          ))}
        </div>
      ) : (
        <div className="text-center py-8">
          <Paperclip size={32} className="mx-auto text-muted-foreground mb-3" />
          <p className="text-muted-foreground text-sm">No attachments yet.</p>
          <p className="text-xs text-muted-foreground mt-1">
            Upload a file or attach an existing one.
          </p>
        </div>
      )}
    </div>
  )
}

// --- Version History Panel ---

interface DiffLine {
  type: 'added' | 'removed' | 'unchanged'
  text: string
}

function computeSimpleDiff(oldText: string, newText: string): DiffLine[] {
  const oldLines = oldText.split('\n')
  const newLines = newText.split('\n')
  const result: DiffLine[] = []

  let oi = 0
  let ni = 0

  while (oi < oldLines.length || ni < newLines.length) {
    if (oi >= oldLines.length) {
      result.push({ type: 'added', text: newLines[ni] })
      ni++
    } else if (ni >= newLines.length) {
      result.push({ type: 'removed', text: oldLines[oi] })
      oi++
    } else if (oldLines[oi] === newLines[ni]) {
      result.push({ type: 'unchanged', text: oldLines[oi] })
      oi++
      ni++
    } else {
      let foundInNew = -1
      let foundInOld = -1
      const maxLen = Math.max(oldLines.length, newLines.length)
      const lookAhead = Math.min(5, maxLen)

      for (let k = 1; k <= lookAhead && ni + k < newLines.length; k++) {
        if (oldLines[oi] === newLines[ni + k]) {
          foundInNew = ni + k
          break
        }
      }
      for (let k = 1; k <= lookAhead && oi + k < oldLines.length; k++) {
        if (oldLines[oi + k] === newLines[ni]) {
          foundInOld = oi + k
          break
        }
      }

      if (foundInNew >= 0 && (foundInOld < 0 || (foundInNew - ni) <= (foundInOld - oi))) {
        while (ni < foundInNew) {
          result.push({ type: 'added', text: newLines[ni] })
          ni++
        }
      } else if (foundInOld >= 0) {
        while (oi < foundInOld) {
          result.push({ type: 'removed', text: oldLines[oi] })
          oi++
        }
      } else {
        result.push({ type: 'removed', text: oldLines[oi] })
        result.push({ type: 'added', text: newLines[ni] })
        oi++
        ni++
      }
    }
  }

  return result
}

function VersionHistoryPanel({
  versions,
  isLoading,
  error,
  expandedVersion,
  onToggleExpand,
  showRestoreConfirm,
  onShowRestoreConfirm,
  restoreMut,
}: {
  versions: KnowledgeVersion[] | undefined
  isLoading: boolean
  error: Error | null
  expandedVersion: number | null
  onToggleExpand: (vn: number) => void
  showRestoreConfirm: number | null
  onShowRestoreConfirm: (vn: number | null) => void
  restoreMut: { mutate: (vn: number) => void; isPending: boolean; error: Error | null }
}) {
  const fmt = useFormatters()
  const [diffVersionNum, setDiffVersionNum] = useState<number | null>(null)

  if (isLoading) {
    return (
      <div className="space-y-3">
        {[1, 2, 3].map((i) => (
          <div key={i} className="h-16 bg-muted rounded-lg animate-pulse" />
        ))}
      </div>
    )
  }

  if (error) {
    const is404 = error instanceof ApiError && error.status === 404
    return (
      <div className="text-center py-8">
        <History size={32} className="mx-auto text-muted-foreground mb-3" />
        <p className="text-muted-foreground">
          {is404
            ? 'Version history is not available for this item.'
            : error.message || 'Failed to load version history.'}
        </p>
      </div>
    )
  }

  if (!versions || versions.length === 0) {
    return (
      <div className="text-center py-8">
        <History size={32} className="mx-auto text-muted-foreground mb-3" />
        <p className="text-muted-foreground">No version history available yet.</p>
        <p className="text-sm text-muted-foreground mt-1">
          Versions are created each time the item is edited.
        </p>
      </div>
    )
  }

  const sortedVersions = [...versions].sort((a, b) => b.versionNumber - a.versionNumber)

  return (
    <div className="space-y-3">
      {sortedVersions.map((version, idx) => {
        const isExpanded = expandedVersion === version.versionNumber
        const previousVersion = idx < sortedVersions.length - 1 ? sortedVersions[idx + 1] : null
        const showDiff = diffVersionNum === version.versionNumber

        return (
          <div
            key={version.id}
            className="border border-border/60 rounded-xl shadow-sm overflow-hidden"
          >
            <button
              onClick={() => onToggleExpand(version.versionNumber)}
              className="w-full flex items-center justify-between px-4 py-3 hover:bg-muted/30 transition-colors text-left"
            >
              <div className="flex items-center gap-3 min-w-0">
                {isExpanded ? (
                  <ChevronDown size={16} className="text-muted-foreground flex-shrink-0" />
                ) : (
                  <ChevronRight size={16} className="text-muted-foreground flex-shrink-0" />
                )}
                <div className="min-w-0">
                  <div className="flex items-center gap-2">
                    <span className="text-sm font-medium">
                      Version {version.versionNumber}
                    </span>
                    {idx === 0 && (
                      <span className="px-1.5 py-0.5 text-[10px] font-medium bg-green-50 dark:bg-green-950/30 text-green-700 dark:text-green-400 rounded">
                        Latest
                      </span>
                    )}
                  </div>
                  <p className="text-xs text-muted-foreground">
                    {fmt.dateTime(version.createdAt)}
                    {version.changeDescription && ` - ${version.changeDescription}`}
                  </p>
                </div>
              </div>
              {idx > 0 && (
                <button
                  onClick={(e) => {
                    e.stopPropagation()
                    onShowRestoreConfirm(version.versionNumber)
                  }}
                  className="inline-flex items-center gap-1 px-2 py-1 text-xs border border-input rounded hover:bg-muted transition-colors flex-shrink-0"
                  title="Restore this version"
                >
                  <RotateCcw size={12} /> Restore
                </button>
              )}
            </button>

            {isExpanded && (
              <div className="border-t border-border/60 px-4 py-3 space-y-3">
                <div className="flex items-center gap-2">
                  <h4 className="text-sm font-medium text-muted-foreground">Title:</h4>
                  <span className="text-sm">{version.title}</span>
                </div>

                {previousVersion && (
                  <button
                    onClick={() => setDiffVersionNum(showDiff ? null : version.versionNumber)}
                    className="inline-flex items-center gap-1 px-2 py-1 text-xs border border-input rounded hover:bg-muted transition-colors"
                  >
                    {showDiff ? 'Hide Changes' : 'Show Changes'}
                  </button>
                )}

                {showDiff && previousVersion ? (
                  <DiffView
                    oldContent={previousVersion.content}
                    newContent={version.content}
                  />
                ) : (
                  <div className="whitespace-pre-wrap text-sm bg-muted border border-border/60 rounded-lg p-3 max-h-80 overflow-y-auto font-mono">
                    {version.content}
                  </div>
                )}
              </div>
            )}
          </div>
        )
      })}

      {/* Restore Confirmation Modal */}
      {showRestoreConfirm !== null && (
        <div className="fixed inset-0 z-[60] flex items-center justify-center bg-black/50 p-4">
          <div className="max-h-[calc(100dvh-2rem)] w-full max-w-sm space-y-4 overflow-y-auto rounded-xl bg-card p-5 shadow-sm sm:p-6">
            <h2 className="text-lg font-semibold">Restore Version {showRestoreConfirm}?</h2>
            <p className="text-sm text-muted-foreground">
              This will replace the current content with the content from version {showRestoreConfirm}. A new version will be created with the current content.
            </p>
            {restoreMut.error && (
              <p className="text-red-600 dark:text-red-400 text-sm">
                {restoreMut.error instanceof Error ? restoreMut.error.message : 'Restore failed'}
              </p>
            )}
            <div className="flex gap-2 justify-end">
              <button
                onClick={() => onShowRestoreConfirm(null)}
                className="px-4 py-2 border border-input rounded-md text-sm transition-colors"
              >
                Cancel
              </button>
              <button
                onClick={() => restoreMut.mutate(showRestoreConfirm)}
                disabled={restoreMut.isPending}
                className="px-4 py-2 bg-primary text-primary-foreground rounded-md text-sm font-medium disabled:opacity-50"
              >
                {restoreMut.isPending ? 'Restoring...' : 'Restore'}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}

// --- Commit History Panel ---

function CommitHistoryPanel({
  entries,
  total,
  page,
  pageSize,
  isLoading,
  error,
  onPageChange,
}: {
  entries: CommitHistoryEntry[]
  total: number
  page: number
  pageSize: number
  isLoading: boolean
  error: Error | null
  onPageChange: (page: number) => void
}) {
  const fmt = useFormatters()
  const [expandedSha, setExpandedSha] = useState<string | null>(null)

  if (isLoading) {
    return (
      <div className="space-y-3">
        {[1, 2, 3].map((i) => (
          <div key={i} className="h-16 bg-muted rounded-lg animate-pulse" />
        ))}
      </div>
    )
  }

  if (error) {
    return (
      <div className="rounded-lg border border-red-300 bg-red-50 p-3 text-sm text-red-700 dark:bg-red-950/30 dark:border-red-700 dark:text-red-400">
        {error instanceof Error ? error.message : 'Failed to load commit history'}
      </div>
    )
  }

  if (entries.length === 0) {
    return (
      <div className="text-center py-8">
        <GitCommit size={32} className="mx-auto text-muted-foreground mb-3" />
        <p className="text-muted-foreground">No commit history yet.</p>
        <p className="text-sm text-muted-foreground mt-1">
          This file has not been linked to any commits in this vault&apos;s git repository.
        </p>
      </div>
    )
  }

  const totalPages = Math.max(1, Math.ceil(total / pageSize))
  const fromIdx = (page - 1) * pageSize + 1
  const toIdx = Math.min(page * pageSize, total)
  const showPagination = total > pageSize

  return (
    <div className="space-y-3">
      <ul className="space-y-3">
        {entries.map((entry) => {
          const isExpanded = expandedSha === entry.sha
          return (
            <li
              key={entry.sha}
              className="border border-border/60 rounded-xl shadow-sm overflow-hidden"
            >
              <button
                type="button"
                onClick={() => setExpandedSha(isExpanded ? null : entry.sha)}
                className="w-full flex items-start justify-between gap-3 px-4 py-3 hover:bg-muted/30 transition-colors text-left"
              >
                <div className="flex items-start gap-3 min-w-0 flex-1">
                  {isExpanded ? (
                    <ChevronDown size={16} className="text-muted-foreground flex-shrink-0 mt-0.5" />
                  ) : (
                    <ChevronRight size={16} className="text-muted-foreground flex-shrink-0 mt-0.5" />
                  )}
                  <GitCommit size={16} className="text-muted-foreground flex-shrink-0 mt-0.5" />
                  <div className="min-w-0 flex-1 space-y-1">
                    <div className="flex items-center justify-between gap-2">
                      <span className="text-sm font-medium truncate">{entry.title}</span>
                      <time
                        dateTime={entry.committedAt}
                        title={new Date(entry.committedAt).toISOString()}
                        className="text-xs text-muted-foreground flex-shrink-0"
                      >
                        {fmt.relative(entry.committedAt)}
                      </time>
                    </div>
                    <div className="flex items-center gap-2 text-xs text-muted-foreground flex-wrap">
                      <span>{entry.authorName}</span>
                      <span>&middot;</span>
                      <span className="font-mono">{entry.shortSha}</span>
                      <span>&middot;</span>
                      <span>
                        {entry.changedFileCount} {entry.changedFileCount === 1 ? 'file' : 'files'}
                      </span>
                      <span>&middot;</span>
                      <span className="text-green-600 dark:text-green-400">+{entry.linesAdded}</span>
                      <span className="text-red-600 dark:text-red-400">-{entry.linesDeleted}</span>
                    </div>
                  </div>
                </div>
              </button>

              {isExpanded && entry.content && (
                <div className="border-t border-border/60 px-4 py-3">
                  <div className="whitespace-pre-wrap text-sm bg-muted border border-border/60 rounded-lg p-3 max-h-80 overflow-y-auto font-mono">
                    {entry.content}
                  </div>
                </div>
              )}
            </li>
          )
        })}
      </ul>

      {showPagination && (
        <div className="flex items-center justify-between pt-2 border-t border-border/60">
          <p className="text-xs text-muted-foreground">
            Showing {fromIdx}-{toIdx} of {total} commits
          </p>
          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={() => onPageChange(Math.max(1, page - 1))}
              disabled={page <= 1}
              className="inline-flex items-center gap-1 px-3 py-1.5 text-xs border border-input rounded-md disabled:opacity-40 disabled:cursor-not-allowed hover:bg-muted transition-colors"
            >
              Previous
            </button>
            <span className="text-xs text-muted-foreground">
              Page {page} of {totalPages}
            </span>
            <button
              type="button"
              onClick={() => onPageChange(page + 1)}
              disabled={page >= totalPages}
              className="inline-flex items-center gap-1 px-3 py-1.5 text-xs border border-input rounded-md disabled:opacity-40 disabled:cursor-not-allowed hover:bg-muted transition-colors"
            >
              Next
            </button>
          </div>
        </div>
      )}
    </div>
  )
}

// --- Knowledge Chat Panel ---

type DetailLevel = 'concise' | 'balanced' | 'detailed'

const DETAIL_LEVEL_CONFIG: Record<DetailLevel, { label: string; placeholder: string }> = {
  concise: { label: 'Concise', placeholder: 'Ask a quick question...' },
  balanced: { label: 'Balanced', placeholder: 'Ask about this knowledge...' },
  detailed: { label: 'Detailed', placeholder: 'Ask for a thorough analysis...' },
}

interface ChatMsg {
  role: 'user' | 'assistant'
  content: string
}

function KnowledgeChatPanel({
  knowledgeId,
  knowledgeTitle,
  onClose,
}: {
  knowledgeId: string
  knowledgeTitle: string
  onClose: () => void
}) {
  const [messages, setMessages] = useState<ChatMsg[]>([])
  const [input, setInput] = useState('')
  const [loading, setLoading] = useState(false)
  const [detailLevel, setDetailLevel] = useState<DetailLevel>('concise')
  const [maximized, setMaximized] = useState(false)
  const messagesEndRef = useRef<HTMLDivElement>(null)

  const scrollToBottom = useCallback(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [])

  useEffect(() => {
    scrollToBottom()
  }, [messages, scrollToBottom])

  const handleSend = async () => {
    const question = input.trim()
    if (!question || loading) return

    const userMsg: ChatMsg = { role: 'user', content: question }
    setMessages((prev) => [...prev, userMsg])
    setInput('')
    setLoading(true)

    try {
      const history = messages.map((m) => ({ role: m.role, content: m.content }))
      const prefixedQuestion = `[Detail level: ${detailLevel}] ${question}`
      const response = await api.chat({
        question: prefixedQuestion,
        knowledgeId,
        conversationHistory: history,
      })
      setMessages((prev) => [...prev, { role: 'assistant', content: response.answer }])
    } catch (err) {
      const errMsg = err instanceof Error ? err.message : 'Failed to get response'
      setMessages((prev) => [...prev, { role: 'assistant', content: `Error: ${errMsg}` }])
    } finally {
      setLoading(false)
    }
  }

  const handleKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault()
      handleSend()
    }
  }

  const truncatedTitle = knowledgeTitle.length > 30 ? knowledgeTitle.slice(0, 30) + '...' : knowledgeTitle

  const panelSize = maximized
    ? 'top-4 bottom-4 sm:top-auto sm:bottom-24 sm:h-[80vh] sm:w-[700px]'
    : 'bottom-20 h-[min(500px,calc(100dvh-6rem))] sm:bottom-24 sm:h-[500px] sm:w-[400px]'

  return (
    <div className={`fixed inset-x-4 sm:left-auto sm:right-6 z-40 max-h-[calc(100dvh-2rem)] w-auto ${panelSize} bg-card border border-border rounded-xl shadow-xl flex flex-col overflow-hidden animate-fade-in transition-all duration-200`}>
      {/* Header */}
      <div className="flex items-center justify-between px-4 py-3 border-b border-border bg-muted/30">
        <div className="flex items-center gap-2 min-w-0">
          <MessageCircle size={16} className="text-primary flex-shrink-0" />
          <span className="text-sm font-medium truncate" title={`Chat with: ${knowledgeTitle}`}>
            Chat with: {truncatedTitle}
          </span>
        </div>
        <div className="flex items-center gap-1 flex-shrink-0">
          <button
            onClick={() => setMaximized(!maximized)}
            className="p-1 text-muted-foreground hover:text-foreground rounded hover:bg-muted transition-colors"
            aria-label={maximized ? 'Minimize chat' : 'Maximize chat'}
          >
            {maximized ? <Minimize2 size={16} /> : <Maximize2 size={16} />}
          </button>
          <button
            onClick={onClose}
            className="p-1 text-muted-foreground hover:text-foreground rounded hover:bg-muted transition-colors"
            aria-label="Close chat"
          >
            <X size={16} />
          </button>
        </div>
      </div>

      {/* Detail Level Picker */}
      <div className="flex items-center gap-1 px-4 py-2 border-b border-border/60 bg-muted/10">
        {(Object.entries(DETAIL_LEVEL_CONFIG) as [DetailLevel, typeof DETAIL_LEVEL_CONFIG[DetailLevel]][]).map(
          ([level, config]) => (
            <button
              key={level}
              onClick={() => setDetailLevel(level)}
              className={`px-2.5 py-1 text-xs font-medium rounded-full transition-colors ${
                detailLevel === level
                  ? 'bg-primary text-primary-foreground'
                  : 'bg-muted text-muted-foreground hover:text-foreground hover:bg-muted/80'
              }`}
            >
              {config.label}
            </button>
          ),
        )}
      </div>

      {/* Messages */}
      <div className="flex-1 overflow-y-auto px-4 py-3 space-y-3">
        {messages.length === 0 && (
          <div className="text-center py-12 space-y-2">
            <MessageCircle size={28} className="mx-auto text-muted-foreground" />
            <p className="text-sm text-muted-foreground">Ask anything about this knowledge item</p>
          </div>
        )}
        {messages.map((msg, i) => (
          <div key={i} className={`flex ${msg.role === 'user' ? 'justify-end' : 'justify-start'}`}>
            <div
              className={`max-w-[85%] rounded-lg px-3 py-2 text-sm ${
                msg.role === 'user'
                  ? 'bg-primary text-primary-foreground'
                  : 'bg-muted border border-border/60'
              }`}
            >
              {msg.role === 'assistant' ? (
                <MarkdownContent content={msg.content} compact />
              ) : (
                <span className="whitespace-pre-wrap">{msg.content}</span>
              )}
            </div>
          </div>
        ))}
        {loading && (
          <div className="flex justify-start">
            <div className="bg-muted border border-border/60 rounded-lg px-3 py-2 flex items-center gap-2">
              <Loader2 size={14} className="animate-spin text-muted-foreground" />
              <span className="text-sm text-muted-foreground">Thinking...</span>
            </div>
          </div>
        )}
        <div ref={messagesEndRef} />
      </div>

      {/* Input */}
      <div className="border-t border-border px-3 py-2">
        <div className="flex items-end gap-2">
          <textarea
            value={input}
            onChange={(e) => setInput(e.target.value)}
            onKeyDown={handleKeyDown}
            placeholder={DETAIL_LEVEL_CONFIG[detailLevel].placeholder}
            rows={1}
            className="flex-1 px-3 py-2 text-sm border border-input rounded-lg bg-card focus:outline-none focus:ring-1 focus:ring-ring transition-colors resize-none"
          />
          <button
            onClick={handleSend}
            disabled={!input.trim() || loading}
            className="p-2 rounded-lg bg-primary text-primary-foreground disabled:opacity-50 hover:opacity-90 transition-opacity flex-shrink-0"
            aria-label="Send message"
          >
            <Send size={16} />
          </button>
        </div>
      </div>
    </div>
  )
}

function DiffView({ oldContent, newContent }: { oldContent: string; newContent: string }) {
  const diffLines = useMemo(() => computeSimpleDiff(oldContent, newContent), [oldContent, newContent])

  return (
    <div className="border border-border/60 rounded-lg overflow-hidden max-h-80 overflow-y-auto">
      <div className="font-mono text-xs">
        {diffLines.map((line, i) => (
          <div
            key={i}
            className={`px-3 py-0.5 whitespace-pre-wrap ${
              line.type === 'added'
                ? 'bg-green-50 dark:bg-green-950/30 text-green-800 dark:text-green-300'
                : line.type === 'removed'
                  ? 'bg-red-50 dark:bg-red-950/30 text-red-800 dark:text-red-300'
                  : 'text-muted-foreground'
            }`}
          >
            <span className="select-none mr-2 text-muted-foreground">
              {line.type === 'added' ? '+' : line.type === 'removed' ? '-' : ' '}
            </span>
            {line.text}
          </div>
        ))}
      </div>
    </div>
  )
}
