import { useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from '../lib/api-client'
import { useAuth } from '../lib/auth'
import { UserRole } from '../lib/types'
import type { InboxItemDto, Vault } from '../lib/types'
import { parseAsUtc, formatDate } from '../lib/format-utils'
import {
  Inbox,
  Plus,
  Search,
  Trash2,
  Pencil,
  ArrowRightLeft,
  Check,
  X,
  ChevronLeft,
  ChevronRight,
  Loader2,
  Mail,
  FolderInput,
} from 'lucide-react'
import SurfaceCard from '../components/ui/SurfaceCard'

const TYPE_OPTIONS = ['All', 'Note', 'Link', 'File'] as const

export default function InboxPage() {
  const queryClient = useQueryClient()
  const { user } = useAuth()
  const isAdmin = user?.role === UserRole.Admin || user?.role === UserRole.SuperAdmin
  const [page, setPage] = useState(1)
  const [pageSize] = useState(20)
  const [search, setSearch] = useState('')
  const [searchInput, setSearchInput] = useState('')
  const [typeFilter, setTypeFilter] = useState<string>('All')
  const [showAllItems, setShowAllItems] = useState(true)
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set())
  const [quickCapture, setQuickCapture] = useState('')
  const [editingId, setEditingId] = useState<string | null>(null)
  const [editBody, setEditBody] = useState('')
  const [showConvertDialog, setShowConvertDialog] = useState(false)
  const [convertIds, setConvertIds] = useState<string[]>([])
  const [convertVaultId, setConvertVaultId] = useState<string>('')
  const [convertTags, setConvertTags] = useState('')
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false)
  const [isBulkDeleting, setIsBulkDeleting] = useState(false)
  const [bulkDeleteError, setBulkDeleteError] = useState('')

  const queryParams: Record<string, string | undefined> = {
    page: String(page),
    pageSize: String(pageSize),
    search: search || undefined,
    type: typeFilter !== 'All' ? typeFilter : undefined,
    userFilter: isAdmin && !showAllItems ? 'mine' : undefined,
  }

  const { data, isLoading } = useQuery({
    queryKey: ['inbox', page, search, typeFilter, showAllItems],
    queryFn: () => api.listInbox(queryParams),
  })

  const isPerUser = data?.inboxVisibilityScope === 'PerUser'

  const { data: vaultsData } = useQuery({
    queryKey: ['vaults'],
    queryFn: () => api.listVaults(false),
  })

  const createMutation = useMutation({
    mutationFn: (body: string) => api.createInboxItem(body),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['inbox'] })
      setQuickCapture('')
    },
  })

  const updateMutation = useMutation({
    mutationFn: ({ id, body }: { id: string; body: string }) => api.updateInboxItem(id, body),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['inbox'] })
      setEditingId(null)
    },
  })

  const deleteMutation = useMutation({
    mutationFn: (id: string) => api.deleteInboxItem(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['inbox'] })
      setSelectedIds((prev) => {
        const next = new Set(prev)
        next.delete(deleteMutation.variables!)
        return next
      })
    },
  })

  const convertMutation = useMutation({
    mutationFn: ({ id, vaultId, tags }: { id: string; vaultId?: string; tags?: string[] }) =>
      api.convertInboxItem(id, { vaultId, tags }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['inbox'] })
    },
  })

  const batchConvertMutation = useMutation({
    mutationFn: (data: { ids: string[]; vaultId?: string; tags?: string[] }) =>
      api.batchConvertInbox(data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['inbox'] })
      setSelectedIds(new Set())
      setShowConvertDialog(false)
    },
  })

  const handleBulkDelete = async () => {
    setIsBulkDeleting(true)
    setBulkDeleteError('')
    const ids = Array.from(selectedIds)
    const results = await Promise.allSettled(ids.map((id) => api.deleteInboxItem(id)))
    const failed = results.filter((r) => r.status === 'rejected').length
    if (failed > 0) {
      setBulkDeleteError(`${failed} of ${ids.length} deletes failed`)
    } else {
      setShowDeleteConfirm(false)
      setBulkDeleteError('')
    }
    setSelectedIds(new Set())
    setIsBulkDeleting(false)
    queryClient.invalidateQueries({ queryKey: ['inbox'] })
  }

  const handleSearch = (e: React.FormEvent) => {
    e.preventDefault()
    setSearch(searchInput)
    setPage(1)
  }

  const toggleSelect = (id: string) => {
    setSelectedIds((prev) => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }

  const toggleSelectAll = () => {
    if (!data) return
    if (selectedIds.size === data.items.length) {
      setSelectedIds(new Set())
    } else {
      setSelectedIds(new Set(data.items.map((i) => i.id)))
    }
  }

  const startEdit = (item: InboxItemDto) => {
    setEditingId(item.id)
    setEditBody(item.body)
  }

  const saveEdit = () => {
    if (editingId && editBody.trim()) {
      updateMutation.mutate({ id: editingId, body: editBody.trim() })
    }
  }

  const cancelEdit = () => {
    setEditingId(null)
    setEditBody('')
  }

  const openConvertDialog = (ids: string[]) => {
    setConvertIds(ids)
    setConvertVaultId('')
    setConvertTags('')
    setShowConvertDialog(true)
  }

  const handleConvert = () => {
    const tags = convertTags
      .split(',')
      .map((t) => t.trim())
      .filter(Boolean)
    if (convertIds.length === 1) {
      convertMutation.mutate(
        {
          id: convertIds[0],
          vaultId: convertVaultId || undefined,
          tags: tags.length > 0 ? tags : undefined,
        },
        {
          onSuccess: () => {
            setShowConvertDialog(false)
            setSelectedIds((prev) => {
              const next = new Set(prev)
              next.delete(convertIds[0])
              return next
            })
          },
        },
      )
    } else {
      batchConvertMutation.mutate({
        ids: convertIds,
        vaultId: convertVaultId || undefined,
        tags: tags.length > 0 ? tags : undefined,
      })
    }
  }

  const preview = (body: string) => (body.length > 80 ? body.slice(0, 80) + '...' : body)

  const relativeTime = (dateStr: string) => {
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

  const typeBadgeClass = (type: string) => {
    switch (type) {
      case 'Note':
        return 'bg-blue-100 dark:bg-blue-950/40 text-blue-700 dark:text-blue-400'
      case 'Link':
        return 'bg-green-100 dark:bg-green-950/40 text-green-700 dark:text-green-400'
      case 'File':
        return 'bg-purple-100 dark:bg-purple-950/40 text-purple-700 dark:text-purple-400'
      default:
        return 'bg-muted text-muted-foreground'
    }
  }

  const vaults: Vault[] = vaultsData?.vaults ?? []

  return (
    <div className="space-y-4">
      {/* Quick Capture */}
      <SurfaceCard className="p-4">
        <div className="flex gap-2 items-end">
        <textarea
          rows={2}
          value={quickCapture}
          onChange={(e) => setQuickCapture(e.target.value)}
          onKeyDown={(e) => {
            if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') {
              e.preventDefault()
              if (quickCapture.trim()) {
                createMutation.mutate(quickCapture.trim())
              }
            }
          }}
          placeholder="Quick capture: type a note... (Ctrl+Enter to save)"
          className="flex-1 px-3 py-2 border border-input rounded-md bg-card text-foreground focus:outline-none focus:ring-2 focus:ring-ring resize-y"
        />
        <button
          type="button"
          onClick={() => {
            if (quickCapture.trim()) {
              createMutation.mutate(quickCapture.trim())
            }
          }}
          disabled={!quickCapture.trim() || createMutation.isPending}
          className="flex items-center gap-2 px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 disabled:opacity-50 disabled:cursor-not-allowed self-end"
        >
          <Plus size={16} />
          Add
        </button>
        </div>
      </SurfaceCard>

      {/* Search & Filter Bar */}
      <div className="sh-toolbar flex gap-2 items-center flex-wrap p-3">
        <form onSubmit={handleSearch} className="flex-1 flex gap-2 min-w-0">
          <div className="relative flex-1">
            <Search size={16} className="absolute left-3 top-1/2 -translate-y-1/2 text-muted-foreground" />
            <input
              type="text"
              value={searchInput}
              onChange={(e) => setSearchInput(e.target.value)}
              placeholder="Search inbox..."
              className="w-full pl-10 pr-3 py-2 border border-input rounded-md bg-card text-foreground focus:outline-none focus:ring-2 focus:ring-ring"
            />
          </div>
          <button
            type="submit"
            className="px-4 py-2 bg-muted text-foreground rounded-md hover:bg-muted/80 transition-colors"
          >
            Search
          </button>
        </form>
        <select
          value={typeFilter}
          onChange={(e) => {
            setTypeFilter(e.target.value)
            setPage(1)
          }}
          className="px-3 py-2 border border-input rounded-md bg-card text-foreground focus:outline-none focus:ring-2 focus:ring-ring"
        >
          {TYPE_OPTIONS.map((t) => (
            <option key={t} value={t}>
              {t}
            </option>
          ))}
        </select>
        {/* Admin toggle: show when PerUser mode is active and user is admin */}
        {isPerUser && isAdmin && (
          <div className="flex items-center gap-1 text-sm">
            <span className="text-muted-foreground mr-1">View:</span>
            <button
              type="button"
              onClick={() => { setShowAllItems(true); setPage(1) }}
              className={`px-3 py-1.5 rounded-md text-sm font-medium transition-colors ${
                showAllItems
                  ? 'bg-primary text-primary-foreground'
                  : 'text-muted-foreground hover:bg-muted'
              }`}
            >
              All Items
            </button>
            <button
              type="button"
              onClick={() => { setShowAllItems(false); setPage(1) }}
              className={`px-3 py-1.5 rounded-md text-sm font-medium transition-colors ${
                !showAllItems
                  ? 'bg-primary text-primary-foreground'
                  : 'text-muted-foreground hover:bg-muted'
              }`}
            >
              My Items
            </button>
          </div>
        )}
      </div>

      {/* Batch Action Bar - placeholder for spacing; floating bar is at the bottom */}
      {selectedIds.size > 0 && <div className="h-1" />}

      {/* Items List */}
      {isLoading ? (
        <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
          {[1, 2, 3, 4].map((i) => (
            <div key={i} className="sh-surface h-28 animate-pulse" />
          ))}
        </div>
      ) : data && data.items.length > 0 ? (
        <SurfaceCard className="overflow-hidden">
          {/* Header */}
          <div className="flex items-center gap-3 border-b border-border/60 bg-muted/60 px-4 py-3 text-sm font-medium text-muted-foreground">
            <input
              type="checkbox"
              checked={selectedIds.size === data.items.length && data.items.length > 0}
              onChange={toggleSelectAll}
              className="rounded"
            />
            <span className="flex-1">Content</span>
            <span className="w-16 text-center">Type</span>
            <span className="w-24 text-right">Time</span>
            <span className="w-28 text-right">Actions</span>
          </div>

          {data.items.map((item) => (
            <div
              key={item.id}
              className="flex items-center gap-3 px-4 py-3 hover:bg-muted transition-colors"
            >
              <input
                type="checkbox"
                checked={selectedIds.has(item.id)}
                onChange={() => toggleSelect(item.id)}
                className="rounded"
              />
              <div className="flex-1 min-w-0">
                {editingId === item.id ? (
                  <div className="flex items-center gap-2">
                    <input
                      type="text"
                      value={editBody}
                      onChange={(e) => setEditBody(e.target.value)}
                      className="flex-1 px-2 py-1 border border-input rounded bg-card text-sm"
                      autoFocus
                      onKeyDown={(e) => {
                        if (e.key === 'Enter') saveEdit()
                        if (e.key === 'Escape') cancelEdit()
                      }}
                    />
                    <button
                      onClick={saveEdit}
                      className="p-1 text-green-600 hover:text-green-700"
                      title="Save"
                    >
                      <Check size={16} />
                    </button>
                    <button
                      onClick={cancelEdit}
                      className="p-1 text-muted-foreground hover:text-foreground transition-colors"
                      title="Cancel"
                    >
                      <X size={16} />
                    </button>
                  </div>
                ) : (
                  <div className="flex items-center gap-2 min-w-0">
                    <p className="text-sm text-foreground truncate">{preview(item.body)}</p>
                    {isPerUser && item.createdByUserId === null && (
                      <span className="inline-flex items-center gap-1 text-xs text-muted-foreground bg-muted px-1.5 py-0.5 rounded shrink-0">
                        <Mail size={10} />
                        System
                      </span>
                    )}
                  </div>
                )}
              </div>
              <span className="w-16 text-center">
                <span
                  className={`inline-flex px-2 py-0.5 rounded text-xs font-medium ${typeBadgeClass(item.type)}`}
                >
                  {item.type}
                </span>
              </span>
              <span className="w-24 text-right text-xs text-muted-foreground">
                {relativeTime(item.createdAt)}
              </span>
              <div className="w-28 flex items-center justify-end gap-1">
                <button
                  onClick={() => startEdit(item)}
                  className="p-1.5 text-muted-foreground hover:text-blue-600 rounded hover:bg-muted"
                  title="Edit"
                >
                  <Pencil size={14} />
                </button>
                <button
                  onClick={() => openConvertDialog([item.id])}
                  className="p-1.5 text-muted-foreground hover:text-green-600 rounded hover:bg-muted"
                  title="Convert to Knowledge"
                >
                  <ArrowRightLeft size={14} />
                </button>
                <button
                  onClick={() => deleteMutation.mutate(item.id)}
                  className="p-1.5 text-muted-foreground hover:text-red-600 rounded hover:bg-muted"
                  title="Delete"
                >
                  <Trash2 size={14} />
                </button>
              </div>
            </div>
          ))}
        </SurfaceCard>
      ) : (
        <SurfaceCard className="p-12 text-center text-muted-foreground">
          <Inbox size={48} className="mx-auto mb-4 opacity-50" />
          <p className="text-lg font-medium">No inbox items</p>
          <p className="text-sm mt-1">Use the quick capture above to add your first item.</p>
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

      {/* Floating action bar for bulk operations */}
      {selectedIds.size > 0 && (
        <div className="fixed bottom-6 left-1/2 -translate-x-1/2 z-40">
          <div className="flex items-center gap-3 px-5 py-3 bg-foreground text-background rounded-lg shadow-xl">
            <span className="text-sm font-medium whitespace-nowrap">
              {selectedIds.size} item{selectedIds.size !== 1 ? 's' : ''} selected
            </span>
            <div className="w-px h-5 bg-border" />
            <button
              onClick={() => openConvertDialog(Array.from(selectedIds))}
              disabled={isBulkDeleting}
              className="inline-flex items-center gap-1.5 px-3 py-1.5 text-sm bg-primary/80 hover:bg-primary/70 rounded disabled:opacity-50 transition-colors"
            >
              <FolderInput size={14} /> Convert to Knowledge
            </button>
            <button
              onClick={() => { setBulkDeleteError(''); setShowDeleteConfirm(true) }}
              disabled={isBulkDeleting}
              className="inline-flex items-center gap-1.5 px-3 py-1.5 text-sm bg-red-600 hover:bg-red-700 text-white rounded disabled:opacity-50 transition-colors"
            >
              <Trash2 size={14} /> Delete
            </button>
            <button
              onClick={() => setSelectedIds(new Set())}
              disabled={isBulkDeleting}
              className="inline-flex items-center gap-1 px-2 py-1.5 text-sm hover:bg-primary/70 rounded transition-colors"
              title="Deselect all"
            >
              <X size={14} />
            </button>
          </div>
        </div>
      )}

      {/* Bulk delete confirmation modal */}
      {showDeleteConfirm && (
        <div className="fixed inset-0 bg-black/50 z-50 flex items-center justify-center p-4">
          <div className="bg-card rounded-xl p-6 max-w-sm w-full space-y-4 shadow-sm">
            <h2 className="text-lg font-semibold">
              Delete {selectedIds.size} item{selectedIds.size !== 1 ? 's' : ''}?
            </h2>
            <p className="text-sm text-muted-foreground">
              Are you sure you want to delete {selectedIds.size} inbox item{selectedIds.size !== 1 ? 's' : ''}? This action cannot be undone.
            </p>
            {bulkDeleteError && (
              <p className="text-red-600 dark:text-red-400 text-sm">{bulkDeleteError}</p>
            )}
            <div className="flex gap-2 justify-end">
              <button
                onClick={() => { setShowDeleteConfirm(false); setBulkDeleteError('') }}
                disabled={isBulkDeleting}
                className="px-4 py-2 border border-input rounded-md text-sm transition-colors"
              >
                Cancel
              </button>
              <button
                onClick={handleBulkDelete}
                disabled={isBulkDeleting}
                className="inline-flex items-center gap-2 px-4 py-2 bg-red-600 text-white rounded-md text-sm font-medium disabled:opacity-50"
              >
                {isBulkDeleting ? (
                  <>
                    <Loader2 size={14} className="animate-spin" /> Deleting...
                  </>
                ) : (
                  'Delete'
                )}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Convert Dialog */}
      {showConvertDialog && (
        <div className="fixed inset-0 bg-black/50 z-50 flex items-center justify-center">
          <div className="bg-card rounded-xl shadow-xl p-6 w-full max-w-md mx-4">
            <h2 className="text-lg font-semibold mb-4">Convert to Knowledge</h2>
            <p className="text-sm text-muted-foreground mb-4">
              Converting {convertIds.length} item{convertIds.length > 1 ? 's' : ''} to knowledge.
            </p>

            <div className="space-y-3">
              <div>
                <label className="block text-sm font-medium mb-1">
                  Vault (optional)
                </label>
                <select
                  value={convertVaultId}
                  onChange={(e) => setConvertVaultId(e.target.value)}
                  className="w-full px-3 py-2 border border-input rounded-md bg-card"
                >
                  <option value="">No vault</option>
                  {vaults.map((v) => (
                    <option key={v.id} value={v.id}>
                      {v.name}
                    </option>
                  ))}
                </select>
              </div>

              <div>
                <label className="block text-sm font-medium mb-1">
                  Tags (optional, comma-separated)
                </label>
                <input
                  type="text"
                  value={convertTags}
                  onChange={(e) => setConvertTags(e.target.value)}
                  placeholder="tag1, tag2"
                  className="w-full px-3 py-2 border border-input rounded-md bg-card"
                />
              </div>
            </div>

            <div className="flex justify-end gap-2 mt-6">
              <button
                onClick={() => setShowConvertDialog(false)}
                className="px-4 py-2 text-sm text-muted-foreground hover:text-foreground transition-colors"
              >
                Cancel
              </button>
              <button
                onClick={handleConvert}
                disabled={convertMutation.isPending || batchConvertMutation.isPending}
                className="px-4 py-2 text-sm bg-blue-600 text-white rounded-md hover:bg-blue-700 disabled:opacity-50"
              >
                {convertMutation.isPending || batchConvertMutation.isPending
                  ? 'Converting...'
                  : 'Convert'}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
