import { useState, useEffect } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import {
  X,
  Archive,
  FileText,
  Loader2,
  Search,
  Eye,
  Download,
  AlertTriangle,
  CheckCircle2,
  XCircle,
  ChevronLeft,
  ChevronRight,
} from 'lucide-react'
import { api, ApiError } from '../../lib/api-client'
import { useDebounce } from '../../hooks/useDebounce'
import { useFormatters } from '../../hooks/useFormatters'
import type {
  PlatformVaultDto,
  PlatformKnowledgeSummaryDto,
  SyncItemResult,
  VaultSyncStatusDto,
  SyncConflictStrategy,
} from '../../lib/types'

interface BrowsePlatformModalProps {
  links: VaultSyncStatusDto[]
  onClose: () => void
}

interface ProgressEntry {
  knowledgeId: string
  title: string
  status: 'pending' | 'running' | 'success' | 'error'
  message?: string
  outcome?: string
}

const PAGE_SIZE = 25

export default function BrowsePlatformModal({ links, onClose }: BrowsePlatformModalProps) {
  const queryClient = useQueryClient()
  const [selectedVault, setSelectedVault] = useState<PlatformVaultDto | null>(null)
  const [selectedKnowledgeIds, setSelectedKnowledgeIds] = useState<Set<string>>(new Set())
  const [search, setSearch] = useState('')
  const [page, setPage] = useState(1)
  const [previewId, setPreviewId] = useState<string | null>(null)
  const [strategy, setStrategy] = useState<SyncConflictStrategy>('Skip')
  const [overwriteAcknowledged, setOverwriteAcknowledged] = useState(false)
  const [showOverwriteConfirm, setShowOverwriteConfirm] = useState(false)
  const [progress, setProgress] = useState<ProgressEntry[]>([])
  const [isPulling, setIsPulling] = useState(false)
  const [banner, setBanner] = useState<{ kind: 'error' | 'warning'; message: string } | null>(null)

  const debouncedSearch = useDebounce(search, 300)
  const isDebouncing = search !== debouncedSearch

  // Reset pagination when the debounced search value changes so page 1 is shown.
  useEffect(() => {
    setPage(1)
  }, [debouncedSearch])

  const vaultsQuery = useQuery({
    queryKey: ['platform-sync', 'browse', 'vaults'],
    queryFn: async () => (await api.listPlatformVaults()) ?? { vaults: [], totalCount: 0 },
  })

  const knowledgeQuery = useQuery({
    queryKey: [
      'platform-sync',
      'browse',
      'knowledge',
      selectedVault?.id,
      page,
      debouncedSearch.trim(),
    ],
    queryFn: async () =>
      (await api.listPlatformKnowledge(
        selectedVault!.id,
        page,
        PAGE_SIZE,
        debouncedSearch.trim() || undefined,
      )) ?? { items: [], page, pageSize: PAGE_SIZE, totalCount: 0 },
    enabled: !!selectedVault,
  })

  const previewQuery = useQuery({
    queryKey: ['platform-sync', 'browse', 'preview', previewId],
    queryFn: async () => (await api.getPlatformKnowledge(previewId!)) ?? null,
    enabled: !!previewId,
  })

  const items = knowledgeQuery.data?.items ?? []

  const resolveLinkForVault = (vaultId: string): VaultSyncStatusDto | undefined =>
    links.find((l) => l.remoteVaultId === vaultId)

  const handleVaultClick = (vault: PlatformVaultDto) => {
    setSelectedVault(vault)
    setSelectedKnowledgeIds(new Set())
    setPage(1)
    setPreviewId(null)
    setBanner(null)
    // V-SEC-11: reset overwrite opt-in when changing context.
    setStrategy('Skip')
    setOverwriteAcknowledged(false)
  }

  const toggleItemSelection = (knowledgeId: string) => {
    setSelectedKnowledgeIds((prev) => {
      const next = new Set(prev)
      if (next.has(knowledgeId)) next.delete(knowledgeId)
      else next.add(knowledgeId)
      return next
    })
  }

  const handlePullClick = () => {
    if (!selectedVault || selectedKnowledgeIds.size === 0) return
    const link = resolveLinkForVault(selectedVault.id)
    if (!link) {
      setBanner({
        kind: 'warning',
        message:
          'No sync link exists for this Knowz Cloud vault. Establish a link first from the Vault Links table.',
      })
      return
    }
    if (strategy === 'Overwrite') {
      if (!overwriteAcknowledged) {
        setBanner({
          kind: 'warning',
          message: 'Please check the acknowledgement box to confirm destructive overwrite.',
        })
        return
      }
      setShowOverwriteConfirm(true)
      return
    }
    void runPull(link.linkId, false)
  }

  const runPull = async (linkId: string, overwriteLocal: boolean) => {
    setShowOverwriteConfirm(false)
    setBanner(null)
    const selectedIds = Array.from(selectedKnowledgeIds)
    const entries: ProgressEntry[] = selectedIds.map((id) => {
      const item = items.find((i) => i.id === id)
      return { knowledgeId: id, title: item?.title ?? id, status: 'pending' }
    })
    setProgress(entries)
    setIsPulling(true)

    for (let i = 0; i < selectedIds.length; i++) {
      const id = selectedIds[i]
      setProgress((prev) =>
        prev.map((e, idx) => (idx === i ? { ...e, status: 'running' } : e)),
      )
      try {
        const result: SyncItemResult = await api.pullPlatformItem(linkId, id, overwriteLocal)
        setProgress((prev) =>
          prev.map((e, idx) =>
            idx === i
              ? {
                  ...e,
                  status: result.success ? 'success' : 'error',
                  outcome: result.outcome,
                  message: result.message ?? undefined,
                }
              : e,
          ),
        )
      } catch (err) {
        const message =
          err instanceof ApiError
            ? err.message
            : err instanceof Error
              ? err.message
              : 'Pull failed'
        setProgress((prev) =>
          prev.map((e, idx) =>
            idx === i ? { ...e, status: 'error', message } : e,
          ),
        )
      }
    }

    setIsPulling(false)
    queryClient.invalidateQueries({ queryKey: ['platform-sync', 'history'] })
    queryClient.invalidateQueries({ queryKey: ['platform-sync', 'links'] })
  }

  const totalPages = knowledgeQuery.data
    ? Math.max(1, Math.ceil(knowledgeQuery.data.totalCount / PAGE_SIZE))
    : 1

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
      <div className="fixed inset-0 bg-black/50" onClick={onClose} />
      <div className="relative bg-card border border-border/60 rounded-xl shadow-xl w-full max-w-6xl h-[85vh] flex flex-col overflow-hidden">
        {/* Header */}
        <div className="flex items-center justify-between px-5 py-4 border-b border-border/60 bg-muted/30 shrink-0">
          <h2 className="text-lg font-semibold">Browse Knowz Cloud</h2>
          <button
            onClick={onClose}
            disabled={isPulling}
            className="p-1.5 rounded hover:bg-muted text-muted-foreground disabled:opacity-50"
            aria-label="Close"
          >
            <X size={18} />
          </button>
        </div>

        {banner && (
          <div
            className={`mx-5 mt-3 px-3 py-2 rounded-md text-sm flex items-start gap-2 ${
              banner.kind === 'warning'
                ? 'bg-amber-50 dark:bg-amber-950/30 text-amber-800 dark:text-amber-300'
                : 'bg-red-50 dark:bg-red-950/30 text-red-800 dark:text-red-300'
            }`}
          >
            <AlertTriangle size={14} className="shrink-0 mt-0.5" />
            <span className="flex-1">{banner.message}</span>
            <button onClick={() => setBanner(null)}>
              <X size={14} />
            </button>
          </div>
        )}

        {/* Two-pane body */}
        <div className="flex-1 flex min-h-0">
          {/* Left pane: vaults */}
          <div className="w-64 border-r border-border/60 flex flex-col shrink-0">
            <div className="px-4 py-2 border-b border-border/60 bg-muted/20 text-xs font-semibold uppercase tracking-wider text-muted-foreground">
              Knowz Cloud Vaults
            </div>
            <div className="flex-1 overflow-y-auto">
              {vaultsQuery.isLoading && (
                <div className="p-4 text-sm text-muted-foreground flex items-center gap-2">
                  <Loader2 size={14} className="animate-spin" />
                  Loading vaults...
                </div>
              )}
              {vaultsQuery.error && (
                <div className="p-4 text-sm text-red-600 dark:text-red-400">
                  {vaultsQuery.error instanceof Error
                    ? vaultsQuery.error.message
                    : 'Failed to load vaults'}
                </div>
              )}
              {vaultsQuery.data?.vaults.map((vault) => (
                <button
                  key={vault.id}
                  onClick={() => handleVaultClick(vault)}
                  className={`w-full text-left px-4 py-3 border-b border-border/40 hover:bg-muted/50 transition-colors ${
                    selectedVault?.id === vault.id ? 'bg-primary/10' : ''
                  }`}
                >
                  <div className="flex items-start gap-2">
                    <Archive
                      size={14}
                      className={`mt-0.5 shrink-0 ${
                        selectedVault?.id === vault.id
                          ? 'text-primary'
                          : 'text-muted-foreground'
                      }`}
                    />
                    <div className="flex-1 min-w-0">
                      <div className="text-sm font-medium truncate">{vault.name}</div>
                      {vault.description && (
                        <div className="text-xs text-muted-foreground truncate">
                          {vault.description}
                        </div>
                      )}
                      <div className="text-xs text-muted-foreground mt-0.5">
                        {vault.knowledgeCount} item{vault.knowledgeCount === 1 ? '' : 's'}
                      </div>
                    </div>
                  </div>
                </button>
              ))}
              {vaultsQuery.data?.vaults.length === 0 && (
                <div className="p-4 text-sm text-muted-foreground text-center">
                  No vaults available
                </div>
              )}
            </div>
          </div>

          {/* Right pane: knowledge list + preview flyout */}
          <div className="flex-1 flex min-w-0">
            <div className="flex-1 flex flex-col min-w-0">
              {!selectedVault ? (
                <div className="flex-1 flex items-center justify-center text-sm text-muted-foreground">
                  Select a vault to browse its knowledge
                </div>
              ) : (
                <>
                  <div className="px-4 py-3 border-b border-border/60 shrink-0 space-y-2">
                    <div className="flex items-center gap-2">
                      <div className="relative flex-1 max-w-sm">
                        <Search
                          size={14}
                          className="absolute left-3 top-1/2 -translate-y-1/2 text-muted-foreground"
                        />
                        <input
                          type="text"
                          value={search}
                          onChange={(e) => setSearch(e.target.value)}
                          placeholder="Search items..."
                          className="w-full pl-9 pr-3 py-1.5 text-sm border border-input rounded-md bg-card focus:outline-none focus:ring-2 focus:ring-ring"
                        />
                        {isDebouncing && (
                          <Loader2
                            size={14}
                            className="absolute right-3 top-1/2 -translate-y-1/2 text-muted-foreground animate-spin"
                          />
                        )}
                      </div>
                      {isDebouncing && (
                        <span className="text-xs text-muted-foreground">Searching…</span>
                      )}
                      <div className="text-xs text-muted-foreground ml-auto">
                        {selectedKnowledgeIds.size} selected
                      </div>
                    </div>
                  </div>

                  <div className="flex-1 overflow-y-auto">
                    {knowledgeQuery.isLoading && (
                      <div className="p-4 text-sm text-muted-foreground flex items-center gap-2">
                        <Loader2 size={14} className="animate-spin" />
                        Loading knowledge...
                      </div>
                    )}
                    {knowledgeQuery.error && (
                      <div className="p-4 text-sm text-red-600 dark:text-red-400">
                        {knowledgeQuery.error instanceof Error
                          ? knowledgeQuery.error.message
                          : 'Failed to load knowledge'}
                      </div>
                    )}
                    {items.map((item) => (
                      <KnowledgeRow
                        key={item.id}
                        item={item}
                        selected={selectedKnowledgeIds.has(item.id)}
                        onToggle={() => toggleItemSelection(item.id)}
                        onPreview={() => setPreviewId(item.id)}
                        isPreviewing={previewId === item.id}
                      />
                    ))}
                    {knowledgeQuery.data && items.length === 0 && (
                      <div className="p-8 text-center text-sm text-muted-foreground">
                        No items found
                      </div>
                    )}
                  </div>

                  {/* Pull controls */}
                  <div className="border-t border-border/60 px-4 py-3 shrink-0 space-y-2">
                    {totalPages > 1 && (
                      <div className="flex items-center justify-between text-xs text-muted-foreground">
                        <span>
                          Page {page} of {totalPages}
                        </span>
                        <div className="flex items-center gap-1">
                          <button
                            onClick={() => setPage((p) => Math.max(1, p - 1))}
                            disabled={page <= 1 || isPulling}
                            className="p-1 rounded hover:bg-muted disabled:opacity-50"
                          >
                            <ChevronLeft size={14} />
                          </button>
                          <button
                            onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
                            disabled={page >= totalPages || isPulling}
                            className="p-1 rounded hover:bg-muted disabled:opacity-50"
                          >
                            <ChevronRight size={14} />
                          </button>
                        </div>
                      </div>
                    )}

                    <div className="flex items-center gap-2 flex-wrap">
                      <label className="text-xs text-muted-foreground">Strategy:</label>
                      <select
                        value={strategy}
                        onChange={(e) => {
                          const val = e.target.value as SyncConflictStrategy
                          setStrategy(val)
                          if (val === 'Skip') setOverwriteAcknowledged(false)
                        }}
                        disabled={isPulling}
                        className="px-2 py-1 text-xs border border-input rounded bg-card focus:outline-none focus:ring-1 focus:ring-ring disabled:opacity-50"
                      >
                        <option value="Skip">Skip existing (safe)</option>
                        <option value="Overwrite">Overwrite (destructive)</option>
                      </select>
                    </div>

                    {strategy === 'Overwrite' && (
                      <label className="flex items-start gap-2 text-xs text-amber-700 dark:text-amber-400">
                        <input
                          type="checkbox"
                          checked={overwriteAcknowledged}
                          onChange={(e) => setOverwriteAcknowledged(e.target.checked)}
                          className="mt-0.5"
                        />
                        <span>
                          I understand this will replace local data. Existing local copies of
                          selected items will be overwritten.
                        </span>
                      </label>
                    )}

                    <div className="flex items-center gap-2">
                      <button
                        onClick={handlePullClick}
                        disabled={isPulling || selectedKnowledgeIds.size === 0}
                        className="inline-flex items-center gap-2 px-4 py-1.5 bg-primary text-primary-foreground rounded-md text-sm font-medium hover:opacity-90 disabled:opacity-50 disabled:cursor-not-allowed"
                      >
                        {isPulling ? (
                          <Loader2 size={14} className="animate-spin" />
                        ) : (
                          <Download size={14} />
                        )}
                        Pull Selected ({selectedKnowledgeIds.size})
                      </button>
                    </div>

                    {progress.length > 0 && (
                      <div className="mt-2 max-h-32 overflow-y-auto border border-border/40 rounded-md bg-muted/20">
                        {progress.map((entry) => (
                          <div
                            key={entry.knowledgeId}
                            className="flex items-center gap-2 px-3 py-1.5 text-xs border-b border-border/30 last:border-b-0"
                          >
                            <ProgressIcon status={entry.status} />
                            <span className="flex-1 truncate">{entry.title}</span>
                            {entry.outcome && (
                              <span className="text-muted-foreground">{entry.outcome}</span>
                            )}
                            {entry.message && entry.status === 'error' && (
                              <span
                                className="text-red-600 dark:text-red-400 truncate max-w-[240px]"
                                title={entry.message}
                              >
                                {entry.message}
                              </span>
                            )}
                          </div>
                        ))}
                      </div>
                    )}
                  </div>
                </>
              )}
            </div>

            {/* Preview flyout */}
            {previewId && (
              <div className="w-80 border-l border-border/60 flex flex-col shrink-0">
                <div className="flex items-center justify-between px-4 py-2 border-b border-border/60 bg-muted/20 shrink-0">
                  <span className="text-xs font-semibold uppercase tracking-wider text-muted-foreground">
                    Preview
                  </span>
                  <button
                    onClick={() => setPreviewId(null)}
                    className="p-0.5 rounded hover:bg-muted text-muted-foreground"
                  >
                    <X size={14} />
                  </button>
                </div>
                <div className="flex-1 overflow-y-auto p-4 space-y-3">
                  {previewQuery.isLoading && (
                    <div className="text-sm text-muted-foreground flex items-center gap-2">
                      <Loader2 size={14} className="animate-spin" />
                      Loading preview...
                    </div>
                  )}
                  {previewQuery.error && (
                    <div className="text-sm text-red-600 dark:text-red-400">
                      {previewQuery.error instanceof Error
                        ? previewQuery.error.message
                        : 'Failed to load preview'}
                    </div>
                  )}
                  {previewQuery.data && (
                    <>
                      <h3 className="text-sm font-semibold">{previewQuery.data.title}</h3>
                      {previewQuery.data.summary && (
                        <p className="text-xs text-muted-foreground">
                          {previewQuery.data.summary}
                        </p>
                      )}
                      {previewQuery.data.content && (
                        <div className="text-xs whitespace-pre-wrap text-foreground/90 border-t border-border/40 pt-3">
                          {previewQuery.data.content.slice(0, 2000)}
                          {previewQuery.data.content.length > 2000 && '…'}
                        </div>
                      )}
                    </>
                  )}
                </div>
              </div>
            )}
          </div>
        </div>
      </div>

      {/* Overwrite confirmation modal */}
      {showOverwriteConfirm && selectedVault && (
        <OverwriteConfirmModal
          count={selectedKnowledgeIds.size}
          onCancel={() => setShowOverwriteConfirm(false)}
          onConfirm={() => {
            const link = resolveLinkForVault(selectedVault.id)
            if (link) void runPull(link.linkId, true)
          }}
        />
      )}
    </div>
  )
}

function ProgressIcon({ status }: { status: ProgressEntry['status'] }) {
  if (status === 'pending') return <span className="w-3 h-3 rounded-full bg-muted" />
  if (status === 'running') return <Loader2 size={12} className="animate-spin text-blue-500" />
  if (status === 'success')
    return <CheckCircle2 size={12} className="text-green-600 dark:text-green-400" />
  return <XCircle size={12} className="text-red-600 dark:text-red-400" />
}

function KnowledgeRow({
  item,
  selected,
  onToggle,
  onPreview,
  isPreviewing,
}: {
  item: PlatformKnowledgeSummaryDto
  selected: boolean
  onToggle: () => void
  onPreview: () => void
  isPreviewing: boolean
}) {
  const fmt = useFormatters()
  return (
    <div
      className={`flex items-start gap-2 px-4 py-3 border-b border-border/40 hover:bg-muted/30 ${
        isPreviewing ? 'bg-muted/30' : ''
      }`}
    >
      <input
        type="checkbox"
        checked={selected}
        onChange={onToggle}
        className="mt-1 shrink-0"
      />
      <FileText size={14} className="mt-1 shrink-0 text-muted-foreground" />
      <div className="flex-1 min-w-0">
        <div className="text-sm font-medium truncate">{item.title}</div>
        {item.summary && (
          <div className="text-xs text-muted-foreground line-clamp-2">{item.summary}</div>
        )}
        <div className="text-[10px] text-muted-foreground mt-0.5 flex items-center gap-2">
          {item.createdBy && <span>{item.createdBy}</span>}
          {item.updatedAt && <span>{fmt.date(item.updatedAt)}</span>}
        </div>
      </div>
      <button
        onClick={onPreview}
        className="p-1 rounded hover:bg-muted text-muted-foreground"
        aria-label="Preview"
      >
        <Eye size={14} />
      </button>
    </div>
  )
}

function OverwriteConfirmModal({
  count,
  onCancel,
  onConfirm,
}: {
  count: number
  onCancel: () => void
  onConfirm: () => void
}) {
  return (
    <div className="fixed inset-0 z-[60] flex items-center justify-center p-4">
      <div className="fixed inset-0 bg-black/60" onClick={onCancel} />
      <div className="relative bg-card border border-red-200 dark:border-red-900 rounded-xl shadow-xl w-full max-w-md p-6">
        <div className="flex items-center gap-3 mb-4">
          <div className="flex items-center justify-center w-10 h-10 rounded-full bg-red-50 dark:bg-red-950/40">
            <AlertTriangle size={20} className="text-red-600 dark:text-red-400" />
          </div>
          <h3 className="text-lg font-semibold">Confirm Overwrite</h3>
        </div>
        <p className="text-sm text-muted-foreground mb-6">
          This will replace {count} item{count === 1 ? '' : 's'} on this instance with the Knowz Cloud
          copy. This action cannot be undone.
        </p>
        <div className="flex justify-end gap-3">
          <button
            onClick={onCancel}
            className="px-4 py-2 border border-input rounded-md text-sm font-medium hover:bg-muted"
          >
            Cancel
          </button>
          <button
            onClick={onConfirm}
            className="px-4 py-2 bg-red-600 hover:bg-red-700 text-white rounded-md text-sm font-medium"
          >
            Yes, overwrite {count} item{count === 1 ? '' : 's'}
          </button>
        </div>
      </div>
    </div>
  )
}
