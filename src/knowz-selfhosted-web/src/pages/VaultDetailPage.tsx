import { useState } from 'react'
import { useParams, Link, useNavigate } from 'react-router-dom'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { api, ApiError } from '../lib/api-client'
import { ArrowLeft, BookOpen, Pencil, Trash2, X, GitBranch, RefreshCw, Loader2, CheckCircle2, XCircle, Clock, AlertTriangle } from 'lucide-react'
import { useFormatters } from '../hooks/useFormatters'
import PageHeader from '../components/ui/PageHeader'
import SurfaceCard from '../components/ui/SurfaceCard'

export default function VaultDetailPage() {
  const { id } = useParams<{ id: string }>()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const fmt = useFormatters()

  const [showEdit, setShowEdit] = useState(false)
  const [editName, setEditName] = useState('')
  const [editDescription, setEditDescription] = useState('')
  const [editError, setEditError] = useState('')

  const [showDelete, setShowDelete] = useState(false)

  const { data: vault, isLoading: vaultLoading } = useQuery({
    queryKey: ['vault', id],
    queryFn: () => api.getVault(id!),
    enabled: !!id,
  })

  const { data: contents, isLoading: contentsLoading, error } = useQuery({
    queryKey: ['vault-contents', id],
    queryFn: () => api.getVaultContents(id!),
    enabled: !!id,
  })

  const updateMutation = useMutation({
    mutationFn: () =>
      api.updateVault(id!, {
        name: editName.trim() || undefined,
        description: editDescription.trim(),
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['vault', id] })
      queryClient.invalidateQueries({ queryKey: ['vaults'] })
      setShowEdit(false)
      setEditError('')
    },
    onError: (err) => {
      setEditError(err instanceof Error ? err.message : 'Failed to update vault')
    },
  })

  const deleteMutation = useMutation({
    mutationFn: () => api.deleteVault(id!),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['vaults'] })
      navigate('/vaults')
    },
  })

  const openEditModal = () => {
    setEditName(vault?.name ?? '')
    setEditDescription(vault?.description ?? '')
    setEditError('')
    setShowEdit(true)
  }

  const handleUpdate = (e: React.FormEvent) => {
    e.preventDefault()
    if (!editName.trim()) return
    setEditError('')
    updateMutation.mutate()
  }

  const isLoading = vaultLoading || contentsLoading

  if (isLoading) {
    return (
      <div className="space-y-4">
        <PageHeader
          eyebrow="Library"
          title="Vault details"
          titleAs="h2"
          description="Inspect vault contents, metadata, and connected sync behavior."
        />
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
          {[1, 2, 3].map((i) => (
            <div key={i} className="sh-surface h-28 animate-pulse" />
          ))}
        </div>
        <div className="sh-surface h-64 animate-pulse" />
      </div>
    )
  }

  if (error) {
    return (
      <div className="space-y-4">
        <Link to="/vaults" className="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground transition-colors">
          <ArrowLeft size={16} /> Back to Vaults
        </Link>
        <SurfaceCard className="border-red-200/90 bg-red-50/80 p-4 text-sm text-red-700 dark:border-red-900/60 dark:bg-red-950/20 dark:text-red-300">
          {error instanceof Error ? error.message : 'Failed to load vault contents'}
        </SurfaceCard>
      </div>
    )
  }

  return (
    <div className="space-y-6">
      <Link
        to="/vaults"
        className="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground transition-colors"
      >
        <ArrowLeft size={16} /> Back to Vaults
      </Link>

      <PageHeader
        eyebrow="Library"
        title={vault?.name ?? 'Vault contents'}
        titleAs="h2"
        description={vault?.description || 'Inspect the knowledge items grouped into this vault and manage sync or metadata for the collection.'}
        actions={
          <>
            <button
              onClick={openEditModal}
              className="inline-flex items-center gap-2 rounded-2xl border border-border/70 bg-card/80 px-4 py-2.5 text-sm font-medium shadow-sm transition-colors hover:bg-card"
            >
              <Pencil size={16} />
              Edit vault
            </button>
            {!vault?.isDefault && (
              <button
                onClick={() => setShowDelete(true)}
                className="inline-flex items-center gap-2 rounded-2xl border border-red-300/80 bg-red-50/80 px-4 py-2.5 text-sm font-medium text-red-700 transition-colors hover:bg-red-100 dark:border-red-800 dark:bg-red-950/20 dark:text-red-300 dark:hover:bg-red-950/30"
              >
                <Trash2 size={16} />
                Delete
              </button>
            )}
          </>
        }
        meta={
          <div className="grid grid-cols-1 gap-3 md:grid-cols-3">
            <div className="sh-stat">
              <p className="sh-kicker">Items</p>
              <p className="mt-2 text-sm font-semibold">{contents?.totalItems ?? 0} knowledge items</p>
              <p className="mt-2 text-xs text-muted-foreground">Currently visible within this vault.</p>
            </div>
            <div className="sh-stat">
              <p className="sh-kicker">Type</p>
              <p className="mt-2 text-sm font-semibold">{vault?.vaultType || 'General vault'}</p>
              <p className="mt-2 text-xs text-muted-foreground">The current collection shape for this vault.</p>
            </div>
            <div className="sh-stat">
              <p className="sh-kicker">Created</p>
              <p className="mt-2 text-sm font-semibold">{vault?.createdAt ? fmt.date(vault.createdAt) : 'Unknown date'}</p>
              <p className="mt-2 text-xs text-muted-foreground">Useful when reviewing older workspace structure.</p>
            </div>
          </div>
        }
      />

      {/* Edit Modal */}
      {showEdit && (
        <div className="fixed inset-0 bg-black/50 z-50 flex items-center justify-center p-4">
          <div className="bg-card rounded-xl shadow-xl w-full max-w-md">
            <div className="flex items-center justify-between px-6 py-4 border-b border-border/60">
              <h2 className="text-lg font-semibold">Edit Vault</h2>
              <button
                onClick={() => setShowEdit(false)}
                className="p-1 rounded hover:bg-muted transition-colors"
              >
                <X size={20} />
              </button>
            </div>
            <form onSubmit={handleUpdate} className="p-6 space-y-4">
              <div>
                <label className="block text-sm font-medium mb-1">Name *</label>
                <input
                  type="text"
                  value={editName}
                  onChange={(e) => setEditName(e.target.value)}
                  className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm focus:outline-none focus:ring-2 focus:ring-ring"
                  autoFocus
                  required
                />
              </div>
              <div>
                <label className="block text-sm font-medium mb-1">Description</label>
                <textarea
                  value={editDescription}
                  onChange={(e) => setEditDescription(e.target.value)}
                  rows={3}
                  className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm focus:outline-none focus:ring-2 focus:ring-ring resize-none"
                />
              </div>
              {editError && (
                <p className="text-sm text-red-600 dark:text-red-400">{editError}</p>
              )}
              <div className="flex justify-end gap-3 pt-2">
                <button
                  type="button"
                  onClick={() => setShowEdit(false)}
                  className="px-4 py-2 text-sm font-medium text-muted-foreground hover:bg-muted rounded-md transition-colors"
                >
                  Cancel
                </button>
                <button
                  type="submit"
                  disabled={!editName.trim() || updateMutation.isPending}
                  className="px-4 py-2 text-sm font-medium bg-primary text-primary-foreground rounded-md hover:opacity-90 disabled:opacity-50 transition-opacity"
                >
                  {updateMutation.isPending ? 'Saving...' : 'Save'}
                </button>
              </div>
            </form>
          </div>
        </div>
      )}

      {/* Delete Confirmation Modal */}
      {showDelete && (
        <div className="fixed inset-0 bg-black/50 z-50 flex items-center justify-center p-4">
          <div className="bg-card rounded-xl shadow-xl w-full max-w-sm">
            <div className="p-6 space-y-4">
              <h2 className="text-lg font-semibold">Delete Vault</h2>
              <p className="text-sm text-muted-foreground">
                Are you sure you want to delete <strong>{vault?.name}</strong>? This action cannot be undone.
                Knowledge items in this vault will not be deleted.
              </p>
              <div className="flex justify-end gap-3 pt-2">
                <button
                  type="button"
                  onClick={() => setShowDelete(false)}
                  className="px-4 py-2 text-sm font-medium text-muted-foreground hover:bg-muted rounded-md transition-colors"
                >
                  Cancel
                </button>
                <button
                  onClick={() => deleteMutation.mutate()}
                  disabled={deleteMutation.isPending}
                  className="px-4 py-2 text-sm font-medium bg-red-600 text-white rounded-md hover:bg-red-700 disabled:opacity-50 transition-colors"
                >
                  {deleteMutation.isPending ? 'Deleting...' : 'Delete'}
                </button>
              </div>
            </div>
          </div>
        </div>
      )}

      {/* Git Sync Section */}
      <GitSyncPanel vaultId={id!} />

      <SurfaceCard className="p-5">
        <div className="mb-4 flex items-center justify-between gap-3">
          <div>
            <p className="sh-kicker">Contents</p>
            <h3 className="mt-2 text-xl font-semibold tracking-tight">Knowledge in this vault</h3>
          </div>
          <span className="rounded-full border border-border/70 bg-background/70 px-3 py-1 text-xs text-muted-foreground">
            {contents?.totalItems ?? 0} item{(contents?.totalItems ?? 0) === 1 ? '' : 's'}
          </span>
        </div>
        <div className="space-y-2">
        {contents?.items.map((item) => (
          <Link
            key={item.id}
            to={`/knowledge/${item.id}`}
            className="flex items-center gap-3 rounded-[22px] border border-border/60 bg-background/70 p-4 transition-all duration-200 hover:-translate-y-0.5 hover:bg-card"
          >
            <BookOpen size={16} className="text-muted-foreground flex-shrink-0" />
            <div className="min-w-0 flex-1">
              <p className="font-medium truncate">{item.title}</p>
              {item.summary && (
                <p className="text-sm text-muted-foreground truncate">{item.summary}</p>
              )}
            </div>
            <span className="px-2 py-0.5 text-xs bg-muted rounded flex-shrink-0">
              {item.type}
            </span>
            <span className="text-xs text-muted-foreground flex-shrink-0">
              {fmt.date(item.createdAt)}
            </span>
          </Link>
        ))}
        {contents?.items.length === 0 && (
          <div className="rounded-[22px] border border-dashed border-border/70 bg-background/50 px-5 py-10 text-center text-sm text-muted-foreground">
            This vault is empty.
          </div>
        )}
        </div>
      </SurfaceCard>
    </div>
  )
}

// --- Git Sync Panel ---

function GitSyncStatusBadge({ status }: { status: string }) {
  const lower = status.toLowerCase()
  if (lower === 'synced' || lower === 'success') {
    return (
      <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium bg-green-50 dark:bg-green-950/30 text-green-700 dark:text-green-400">
        <CheckCircle2 size={12} /> Synced
      </span>
    )
  }
  if (lower === 'syncing' || lower === 'inprogress' || lower === 'in_progress') {
    return (
      <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium bg-blue-50 dark:bg-blue-950/30 text-blue-700 dark:text-blue-400">
        <Loader2 size={12} className="animate-spin" /> Syncing
      </span>
    )
  }
  if (lower === 'failed' || lower === 'error') {
    return (
      <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium bg-red-50 dark:bg-red-950/30 text-red-700 dark:text-red-400">
        <XCircle size={12} /> Failed
      </span>
    )
  }
  return (
    <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium bg-muted text-muted-foreground">
      <Clock size={12} /> {status || 'Not Synced'}
    </span>
  )
}

function GitSyncPanel({ vaultId }: { vaultId: string }) {
  const fmt = useFormatters()
  const queryClient = useQueryClient()

  const [showSetup, setShowSetup] = useState(false)
  const [repoUrl, setRepoUrl] = useState('')
  const [branch, setBranch] = useState('main')
  const [pat, setPat] = useState('')
  const [filePatterns, setFilePatterns] = useState('')
  const [trackCommitHistory, setTrackCommitHistory] = useState(false)
  const [commitHistoryDepth, setCommitHistoryDepth] = useState(500)
  const [showCommitCostConfirm, setShowCommitCostConfirm] = useState(false)
  const [commitError, setCommitError] = useState<string | null>(null)
  const [showRemoveConfirm, setShowRemoveConfirm] = useState(false)
  const [showHistory, setShowHistory] = useState(false)

  const {
    data: syncStatus,
    isLoading: statusLoading,
    error: statusError,
  } = useQuery({
    queryKey: ['git-sync-status', vaultId],
    queryFn: () => api.getGitSyncStatus(vaultId),
    retry: (failureCount, error) => {
      // Don't retry 404s — means not configured
      if (error instanceof ApiError && error.status === 404) return false
      return failureCount < 2
    },
  })

  const { data: syncHistory, isLoading: historyLoading } = useQuery({
    queryKey: ['git-sync-history', vaultId],
    queryFn: () => api.getGitSyncHistory(vaultId),
    enabled: showHistory && !!syncStatus,
  })

  const configureMut = useMutation({
    mutationFn: () =>
      api.configureGitSync(vaultId, {
        repositoryUrl: repoUrl,
        branch,
        pat: pat || undefined,
        filePatterns: filePatterns || undefined,
        trackCommitHistory: trackCommitHistory || undefined,
        commitHistoryDepth: trackCommitHistory ? commitHistoryDepth : undefined,
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['git-sync-status', vaultId] })
      setShowSetup(false)
      setRepoUrl('')
      setBranch('main')
      setPat('')
      setFilePatterns('')
      setTrackCommitHistory(false)
      setCommitHistoryDepth(500)
      setShowCommitCostConfirm(false)
      setCommitError(null)
    },
  })

  const triggerMut = useMutation({
    mutationFn: () => api.triggerGitSync(vaultId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['git-sync-status', vaultId] })
      queryClient.invalidateQueries({ queryKey: ['git-sync-history', vaultId] })
    },
  })

  const removeMut = useMutation({
    mutationFn: () => api.removeGitSync(vaultId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['git-sync-status', vaultId] })
      queryClient.invalidateQueries({ queryKey: ['git-sync-history', vaultId] })
      setShowRemoveConfirm(false)
      setShowHistory(false)
    },
  })

  // If 404 error, treat as "not configured"
  const isNotConfigured =
    statusError instanceof ApiError && statusError.status === 404
  const hasConfig = !!syncStatus && !isNotConfigured

  // Non-404 error
  const hasError = statusError && !isNotConfigured

  if (statusLoading) {
    return (
      <SurfaceCard className="p-4">
        <div className="flex items-center gap-2 mb-3">
          <GitBranch size={16} className="text-muted-foreground" />
          <h2 className="text-sm font-semibold">Git Sync</h2>
        </div>
        <div className="h-16 bg-muted rounded-lg animate-pulse" />
      </SurfaceCard>
    )
  }

  return (
    <SurfaceCard className="p-4 space-y-3">
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2">
          <GitBranch size={16} className="text-muted-foreground" />
          <h2 className="text-sm font-semibold">Git Sync</h2>
        </div>
        {hasConfig && (
          <div className="flex items-center gap-2">
            <button
              onClick={() => triggerMut.mutate()}
              disabled={triggerMut.isPending}
              className="inline-flex items-center gap-1 px-2 py-1 text-xs bg-primary text-primary-foreground rounded hover:opacity-90 disabled:opacity-50 transition-opacity"
            >
              {triggerMut.isPending ? (
                <Loader2 size={12} className="animate-spin" />
              ) : (
                <RefreshCw size={12} />
              )}
              Sync Now
            </button>
            <button
              onClick={() => setShowRemoveConfirm(true)}
              className="inline-flex items-center gap-1 px-2 py-1 text-xs border border-red-300 dark:border-red-700 text-red-600 dark:text-red-400 rounded hover:bg-red-50 dark:hover:bg-red-950/30 transition-colors"
            >
              <Trash2 size={12} /> Remove
            </button>
          </div>
        )}
      </div>

      {hasError && (
        <div className="flex items-center gap-2 text-sm text-red-600 dark:text-red-400">
          <AlertTriangle size={14} />
          {statusError instanceof Error ? statusError.message : 'Failed to load sync status'}
        </div>
      )}

      {hasConfig ? (
        <div className="space-y-3">
          {/* Status Card */}
          <div className="bg-muted/50 border border-border/60 rounded-lg p-3 space-y-2">
            <div className="flex items-center justify-between">
              <span className="text-xs font-medium text-muted-foreground uppercase tracking-wider">Status</span>
              <GitSyncStatusBadge status={syncStatus.status} />
            </div>
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-2 text-sm">
              <div>
                <span className="text-muted-foreground">Repository: </span>
                <span className="font-mono text-xs break-all">{syncStatus.repositoryUrl}</span>
              </div>
              <div>
                <span className="text-muted-foreground">Branch: </span>
                <span className="font-mono text-xs">{syncStatus.branch}</span>
              </div>
              {syncStatus.lastSyncAt && (
                <div>
                  <span className="text-muted-foreground">Last Sync: </span>
                  <span className="text-xs">{fmt.dateTime(syncStatus.lastSyncAt)}</span>
                </div>
              )}
              {syncStatus.lastSyncCommitSha && (
                <div>
                  <span className="text-muted-foreground">Commit: </span>
                  <span className="font-mono text-xs">{syncStatus.lastSyncCommitSha.slice(0, 8)}</span>
                </div>
              )}
              {syncStatus.filePatterns && (
                <div className="sm:col-span-2">
                  <span className="text-muted-foreground">Patterns: </span>
                  <span className="font-mono text-xs">{syncStatus.filePatterns}</span>
                </div>
              )}
            </div>
            {syncStatus.errorMessage && (
              <div className="flex items-start gap-2 mt-2 p-2 bg-red-50 dark:bg-red-950/30 rounded text-xs text-red-700 dark:text-red-400">
                <XCircle size={14} className="flex-shrink-0 mt-0.5" />
                {syncStatus.errorMessage}
              </div>
            )}
          </div>

          {triggerMut.error && (
            <p className="text-sm text-red-600 dark:text-red-400">
              {triggerMut.error instanceof Error ? triggerMut.error.message : 'Sync trigger failed'}
            </p>
          )}

          {/* Sync History Toggle */}
          <button
            onClick={() => setShowHistory(!showHistory)}
            className="text-xs text-muted-foreground hover:text-foreground transition-colors"
          >
            {showHistory ? 'Hide History' : 'Show Sync History'}
          </button>

          {showHistory && (
            <div className="space-y-1">
              {historyLoading ? (
                <div className="flex items-center gap-2 py-2 text-xs text-muted-foreground">
                  <Loader2 size={12} className="animate-spin" /> Loading history...
                </div>
              ) : syncHistory && syncHistory.length > 0 ? (
                <div className="border border-border/60 rounded-lg overflow-hidden">
                  <div className="divide-y divide-border">
                    {syncHistory.map((entry, i) => (
                      <div key={i} className="flex items-center justify-between px-3 py-2 text-xs">
                        <div className="flex items-center gap-2 min-w-0">
                          <span className="font-medium">{entry.action}</span>
                          {entry.details && (
                            <span className="text-muted-foreground truncate">{entry.details}</span>
                          )}
                        </div>
                        <span className="text-muted-foreground flex-shrink-0">
                          {fmt.dateTime(entry.timestamp)}
                        </span>
                      </div>
                    ))}
                  </div>
                </div>
              ) : (
                <p className="text-xs text-muted-foreground py-2">No sync history yet.</p>
              )}
            </div>
          )}
        </div>
      ) : (
        /* Setup Form or Setup Button */
        !showSetup ? (
          <div className="text-center py-4">
            <p className="text-sm text-muted-foreground mb-3">
              Connect a Git repository to automatically sync knowledge items.
            </p>
            <button
              onClick={() => setShowSetup(true)}
              className="inline-flex items-center gap-2 px-4 py-2 bg-primary text-primary-foreground rounded-md text-sm font-medium hover:opacity-90 transition-opacity"
            >
              <GitBranch size={14} /> Configure Git Sync
            </button>
          </div>
        ) : (
          <form
            onSubmit={(e) => {
              e.preventDefault()
              setCommitError(null)
              if (!repoUrl.trim()) return
              // Client-side depth validation — mirrors backend [1, 2000] range.
              if (trackCommitHistory && (commitHistoryDepth < 1 || commitHistoryDepth > 2000)) {
                setCommitError('Commit history depth must be between 1 and 2000.')
                return
              }
              // First-enable confirmation gate.
              if (trackCommitHistory && !showCommitCostConfirm) {
                setShowCommitCostConfirm(true)
                return
              }
              configureMut.mutate()
            }}
            className="space-y-3"
          >
            <div>
              <label className="block text-sm font-medium mb-1">Repository URL *</label>
              <input
                type="text"
                value={repoUrl}
                onChange={(e) => setRepoUrl(e.target.value)}
                placeholder="https://github.com/owner/repo.git"
                className="w-full px-3 py-2 text-sm border border-input rounded-md bg-card focus:outline-none focus:ring-2 focus:ring-ring"
                required
              />
            </div>
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
              <div>
                <label className="block text-sm font-medium mb-1">Branch</label>
                <input
                  type="text"
                  value={branch}
                  onChange={(e) => setBranch(e.target.value)}
                  placeholder="main"
                  className="w-full px-3 py-2 text-sm border border-input rounded-md bg-card focus:outline-none focus:ring-2 focus:ring-ring"
                />
              </div>
              <div>
                <label className="block text-sm font-medium mb-1">
                  Personal Access Token
                  <span className="text-xs text-muted-foreground ml-1">(optional)</span>
                </label>
                <input
                  type="password"
                  value={pat}
                  onChange={(e) => setPat(e.target.value)}
                  placeholder="ghp_..."
                  className="w-full px-3 py-2 text-sm border border-input rounded-md bg-card focus:outline-none focus:ring-2 focus:ring-ring"
                />
              </div>
            </div>
            <div>
              <label className="block text-sm font-medium mb-1">
                File Patterns
                <span className="text-xs text-muted-foreground ml-1">(optional, comma-separated)</span>
              </label>
              <input
                type="text"
                value={filePatterns}
                onChange={(e) => setFilePatterns(e.target.value)}
                placeholder="*.md, docs/**/*.txt"
                className="w-full px-3 py-2 text-sm border border-input rounded-md bg-card focus:outline-none focus:ring-2 focus:ring-ring"
              />
            </div>

            {/* Commit History (NODE-6) */}
            <div className="p-3 border border-border/60 rounded-md bg-muted/30">
              <div className="flex items-start gap-2">
                <input
                  type="checkbox"
                  id="selfhosted-track-commit-history"
                  data-testid="selfhosted-track-commit-history"
                  checked={trackCommitHistory}
                  onChange={(e) => setTrackCommitHistory(e.target.checked)}
                  className="mt-1 rounded border-input"
                />
                <label htmlFor="selfhosted-track-commit-history" className="text-sm cursor-pointer">
                  Track commit history (Advanced)
                  <span className="block text-xs text-muted-foreground font-normal">
                    Build a running, AI-elaborated record of commits. Each commit becomes a
                    searchable knowledge entry linked to the files it touched. Increases
                    first-sync time and LLM cost.
                  </span>
                </label>
              </div>

              {trackCommitHistory && (
                <div className="mt-3 space-y-2">
                  <label htmlFor="selfhosted-commit-history-depth" className="block text-xs font-medium">
                    Commit history depth
                  </label>
                  <input
                    id="selfhosted-commit-history-depth"
                    data-testid="selfhosted-commit-history-depth"
                    type="number"
                    min={1}
                    max={2000}
                    value={commitHistoryDepth}
                    onChange={(e) => setCommitHistoryDepth(parseInt(e.target.value) || 0)}
                    className="w-full px-3 py-2 text-sm border border-input rounded-md bg-card focus:outline-none focus:ring-2 focus:ring-ring"
                  />
                  <p className="text-xs text-muted-foreground">
                    Default 500. Platform ceiling 2000.
                  </p>
                  <div className="p-2 bg-amber-50 dark:bg-amber-900/20 border border-amber-200 dark:border-amber-800 rounded text-xs text-amber-800 dark:text-amber-200">
                    If the platform AI is unavailable, commits will be recorded without AI elaboration.
                  </div>
                </div>
              )}
            </div>

            {commitError && (
              <p className="text-sm text-red-600 dark:text-red-400">{commitError}</p>
            )}

            {configureMut.error && (
              <p className="text-sm text-red-600 dark:text-red-400">
                {configureMut.error instanceof Error ? configureMut.error.message : 'Configuration failed'}
              </p>
            )}

            <div className="flex gap-2 pt-1">
              <button
                type="submit"
                disabled={!repoUrl.trim() || configureMut.isPending}
                className="inline-flex items-center gap-2 px-4 py-2 bg-primary text-primary-foreground rounded-md text-sm font-medium hover:opacity-90 disabled:opacity-50 transition-opacity"
              >
                {configureMut.isPending ? (
                  <Loader2 size={14} className="animate-spin" />
                ) : (
                  <GitBranch size={14} />
                )}
                {configureMut.isPending ? 'Saving...' : 'Save Configuration'}
              </button>
              <button
                type="button"
                onClick={() => setShowSetup(false)}
                className="px-4 py-2 text-sm text-muted-foreground hover:text-foreground transition-colors"
              >
                Cancel
              </button>
            </div>
          </form>
        )
      )}

      {/* Commit History Cost Confirmation Modal (NODE-6) */}
      {showCommitCostConfirm && (
        <div
          className="fixed inset-0 bg-black/50 z-50 flex items-center justify-center p-4"
          data-testid="selfhosted-commit-history-cost-confirm"
        >
          <div className="bg-card rounded-xl p-6 max-w-md w-full space-y-3 shadow-sm">
            <h2 className="text-lg font-semibold">Confirm commit-history backfill</h2>
            <p className="text-sm text-muted-foreground">
              Enabling commit-history will elaborate up to {commitHistoryDepth} commits with AI.
              Estimated cost: approximately {commitHistoryDepth * 1200} tokens
              (~${(commitHistoryDepth * 0.0007).toFixed(2)}).
            </p>
            <p className="text-sm text-muted-foreground">
              If the platform AI is unavailable, commits will be recorded without AI elaboration.
              This is a one-time backfill; subsequent syncs process only new commits.
            </p>
            <div className="flex gap-2 justify-end pt-1">
              <button
                type="button"
                onClick={() => {
                  setShowCommitCostConfirm(false)
                  setTrackCommitHistory(false)
                }}
                className="px-4 py-2 border border-input rounded-md text-sm transition-colors"
              >
                Cancel
              </button>
              <button
                type="button"
                onClick={() => {
                  setShowCommitCostConfirm(false)
                  configureMut.mutate()
                }}
                disabled={configureMut.isPending}
                className="px-4 py-2 bg-primary text-primary-foreground rounded-md text-sm font-medium disabled:opacity-50"
              >
                Confirm &amp; Enable
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Remove Confirmation Modal */}
      {showRemoveConfirm && (
        <div className="fixed inset-0 bg-black/50 z-50 flex items-center justify-center p-4">
          <div className="bg-card rounded-xl p-6 max-w-sm w-full space-y-4 shadow-sm">
            <h2 className="text-lg font-semibold">Remove Git Sync?</h2>
            <p className="text-sm text-muted-foreground">
              This will disconnect the Git repository from this vault. Existing knowledge items will not be deleted.
            </p>
            {removeMut.error && (
              <p className="text-red-600 dark:text-red-400 text-sm">
                {removeMut.error instanceof Error ? removeMut.error.message : 'Remove failed'}
              </p>
            )}
            <div className="flex gap-2 justify-end">
              <button
                onClick={() => setShowRemoveConfirm(false)}
                className="px-4 py-2 border border-input rounded-md text-sm transition-colors"
              >
                Cancel
              </button>
              <button
                onClick={() => removeMut.mutate()}
                disabled={removeMut.isPending}
                className="px-4 py-2 bg-red-600 text-white rounded-md text-sm font-medium disabled:opacity-50"
              >
                {removeMut.isPending ? 'Removing...' : 'Remove'}
              </button>
            </div>
          </div>
        </div>
      )}
    </SurfaceCard>
  )
}
