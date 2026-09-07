import { useState, useEffect, useCallback } from 'react'
import { toUserError } from '../lib/toUserError'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { useSearchParams, useNavigate, Link } from 'react-router-dom'
import { api } from '../lib/api-client'
import { parseAsUtc } from '../lib/format-utils'
import { useFormatters } from '../hooks/useFormatters'
import SurfaceCard from '../components/ui/SurfaceCard'
import { ViewModeToggle } from '../components/ViewModeToggle'
import { useViewMode } from '../contexts/ViewModeContext'
import {
  Plus, ChevronLeft, ChevronRight, Trash2, FolderInput, X, Loader2, RefreshCw,
  Search, StickyNote, FileText, Mail, Image, AudioLines, Video, Code2, Link2,
  Archive, PackageOpen, Filter,
} from 'lucide-react'

const KNOWLEDGE_TYPES = ['Note', 'Document', 'Email', 'Image', 'Audio', 'Video', 'Code', 'Link'] as const

const TYPE_ICONS: Record<string, typeof StickyNote> = {
  Note: StickyNote,
  Document: FileText,
  Email: Mail,
  Image: Image,
  Audio: AudioLines,
  Video: Video,
  Code: Code2,
  Link: Link2,
}

const PAGE_SIZES = [10, 20, 50, 100] as const

function relativeTime(dateStr: string): string {
  const now = Date.now()
  // parseAsUtc handles naive selfhosted timestamps that lack a Z suffix.
  const date = parseAsUtc(dateStr).getTime()
  const diffMs = Math.max(0, now - date)
  const diffMin = Math.floor(diffMs / 60000)
  if (diffMin < 1) return 'just now'
  if (diffMin < 60) return `${diffMin}m ago`
  const diffH = Math.floor(diffMin / 60)
  if (diffH < 24) return `${diffH}h ago`
  const diffD = Math.floor(diffH / 24)
  if (diffD < 30) return `${diffD}d ago`
  const diffMo = Math.floor(diffD / 30)
  if (diffMo < 12) return `${diffMo}mo ago`
  return `${Math.floor(diffMo / 12)}y ago`
}

export default function KnowledgeListPage() {
  const [searchParams, setSearchParams] = useSearchParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const fmt = useFormatters()
  const { viewMode } = useViewMode('knowledge')

  const page = Number(searchParams.get('page') || '1')
  const pageSize = Number(searchParams.get('pageSize') || '20')
  const type = searchParams.get('type') || ''
  const title = searchParams.get('title') || ''
  const vaultId = searchParams.get('vaultId') || ''
  const createdByUserId = searchParams.get('createdByUserId') || ''
  const tag = searchParams.get('tag') || ''
  const sort = searchParams.get('sort') || 'created'
  const sortDir = searchParams.get('sortDir') || 'desc'

  // Debounced title search
  const [titleInput, setTitleInput] = useState(title)
  useEffect(() => {
    const timer = setTimeout(() => {
      if (titleInput !== title) {
        updateParam('title', titleInput)
      }
    }, 300)
    return () => clearTimeout(timer)
  }, [titleInput]) // eslint-disable-line react-hooks/exhaustive-deps

  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set())
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false)
  const [showMoveToVault, setShowMoveToVault] = useState(false)
  const [batchError, setBatchError] = useState('')

  const activeFilterCount = [type, vaultId, createdByUserId, title, tag].filter(Boolean).length

  const { data, isLoading, error } = useQuery({
    queryKey: ['knowledge', page, pageSize, type, title, vaultId, createdByUserId, tag, sort, sortDir],
    queryFn: () =>
      api.listKnowledge({
        page: String(page),
        pageSize: String(pageSize),
        type: type || undefined,
        title: title || undefined,
        vaultId: vaultId || undefined,
        createdByUserId: createdByUserId || undefined,
        tag: tag || undefined,
        sort,
        sortDir,
      }),
  })

  // Vault list for filter dropdown
  const vaultsQuery = useQuery({
    queryKey: ['vaults', 'filter'],
    queryFn: () => api.listVaults(false),
  })

  // Creators list for filter dropdown
  const creatorsQuery = useQuery({
    queryKey: ['knowledge-creators'],
    queryFn: () => api.getKnowledgeCreators(),
  })

  // Vault list for bulk move modal
  const moveVaults = useQuery({
    queryKey: ['vaults', 'bulk-move'],
    queryFn: () => api.listVaults(false),
    enabled: showMoveToVault,
  })

  const batchDeleteMut = useMutation({
    mutationFn: async (ids: string[]) => {
      const results = await Promise.allSettled(ids.map((id) => api.deleteKnowledge(id)))
      const failed = results.filter((r) => r.status === 'rejected').length
      if (failed > 0) throw new Error(`${failed} of ${ids.length} deletes failed`)
    },
    onSuccess: () => {
      setSelectedIds(new Set())
      setShowDeleteConfirm(false)
      setBatchError('')
      queryClient.invalidateQueries({ queryKey: ['knowledge'] })
    },
    onError: (err) => {
      setBatchError(toUserError(err, 'Batch delete failed.'))
      queryClient.invalidateQueries({ queryKey: ['knowledge'] })
    },
  })

  const batchReprocessMut = useMutation({
    mutationFn: async (ids: string[]) => {
      const results = await Promise.allSettled(ids.map((id) => api.reprocessKnowledge(id)))
      const failed = results.filter((r) => r.status === 'rejected').length
      if (failed > 0) throw new Error(`${failed} of ${ids.length} reprocesses failed`)
    },
    onSuccess: () => {
      setSelectedIds(new Set())
      setBatchError('')
      queryClient.invalidateQueries({ queryKey: ['knowledge'] })
    },
    onError: (err) => {
      setBatchError(toUserError(err, 'Batch reprocess failed.'))
      queryClient.invalidateQueries({ queryKey: ['knowledge'] })
    },
  })

  const batchMoveMut = useMutation({
    mutationFn: async ({ ids, vaultId: vid }: { ids: string[]; vaultId: string }) => {
      const results = await Promise.allSettled(
        ids.map((id) => api.updateKnowledge(id, { vaultId: vid })),
      )
      const failed = results.filter((r) => r.status === 'rejected').length
      if (failed > 0) throw new Error(`${failed} of ${ids.length} moves failed`)
    },
    onSuccess: () => {
      setSelectedIds(new Set())
      setShowMoveToVault(false)
      setBatchError('')
      queryClient.invalidateQueries({ queryKey: ['knowledge'] })
    },
    onError: (err) => {
      setBatchError(toUserError(err, 'Batch move failed.'))
      queryClient.invalidateQueries({ queryKey: ['knowledge'] })
    },
  })

  const isBatchOperating = batchDeleteMut.isPending || batchMoveMut.isPending || batchReprocessMut.isPending

  const updateParam = useCallback((key: string, value: string) => {
    setSearchParams((prev) => {
      const params = new URLSearchParams(prev)
      if (value) {
        params.set(key, value)
      } else {
        params.delete(key)
      }
      if (key !== 'page') params.set('page', '1')
      return params
    })
  }, [setSearchParams])

  const clearFilters = () => {
    setTitleInput('')
    setSearchParams((prev) => {
      const params = new URLSearchParams(prev)
      params.delete('title')
      params.delete('type')
      params.delete('vaultId')
      params.delete('createdByUserId')
      params.delete('tag')
      params.set('page', '1')
      return params
    })
  }

  const clearTagFilter = () => {
    setSearchParams((prev) => {
      const params = new URLSearchParams(prev)
      params.delete('tag')
      params.set('page', '1')
      return params
    })
  }

  const toggleSort = (field: string) => {
    if (sort === field) {
      updateParam('sortDir', sortDir === 'asc' ? 'desc' : 'asc')
    } else {
      setSearchParams((prev) => {
        const params = new URLSearchParams(prev)
        params.set('sort', field)
        params.set('sortDir', 'desc')
        params.set('page', '1')
        return params
      })
    }
  }

  const sortIndicator = (field: string) => {
    if (sort !== field) return ''
    return sortDir === 'asc' ? ' \u2191' : ' \u2193'
  }

  const currentItems = data?.items || []
  const allSelected = currentItems.length > 0 && currentItems.every((item) => selectedIds.has(item.id))

  const toggleSelectAll = () => {
    if (allSelected) {
      setSelectedIds(new Set())
    } else {
      setSelectedIds(new Set(currentItems.map((item) => item.id)))
    }
  }

  const toggleSelect = (id: string) => {
    setSelectedIds((prev) => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }

  // Pagination: generate visible page numbers
  const totalPages = data?.totalPages || 0
  const pageNumbers: number[] = []
  if (totalPages > 0) {
    const maxVisible = 5
    let start = Math.max(1, page - Math.floor(maxVisible / 2))
    let end = Math.min(totalPages, start + maxVisible - 1)
    if (end - start < maxVisible - 1) start = Math.max(1, end - maxVisible + 1)
    for (let i = start; i <= end; i++) pageNumbers.push(i)
  }

  function renderEmpty() {
    return (
      <div className="flex flex-col items-center gap-3 py-16">
        <PackageOpen size={40} className="text-muted-foreground/40" />
        <p className="text-muted-foreground text-sm">
          {activeFilterCount > 0 ? 'No items match the current filters.' : 'No knowledge items yet.'}
        </p>
        {activeFilterCount > 0 ? (
          <button onClick={clearFilters} className="text-sm text-muted-foreground hover:text-foreground underline transition-colors">
            Clear all filters
          </button>
        ) : (
          <Link
            to="/knowledge/new"
            data-testid="knowledge-empty-cta"
            className="inline-flex items-center gap-1.5 px-4 py-2 bg-primary text-primary-foreground rounded-lg t-label font-medium transition-colors"
          >
            <Plus size={14} /> New Knowledge
          </Link>
        )}
      </div>
    )
  }

  return (
    <div className="space-y-4">
      {/* Filter bar */}
      <div className="sh-toolbar flex flex-wrap items-center gap-3 p-4 relative">
        <Link
          to="/knowledge/new"
          className="inline-flex items-center gap-2 rounded-2xl bg-primary px-4 py-2 text-sm font-semibold text-primary-foreground shadow-sm shadow-primary/20 transition-all duration-200 hover:brightness-110"
        >
          <Plus size={16} /> New
        </Link>
        <div className="relative">
          <Search size={14} className="absolute left-3 top-1/2 -translate-y-1/2 text-muted-foreground" />
          <input
            type="text"
            placeholder="Search by title..."
            value={titleInput}
            onChange={(e) => setTitleInput(e.target.value)}
            className="w-56 rounded-2xl border border-input bg-background/80 pl-9 pr-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-ring/20"
          />
        </div>
        <select
          value={type}
          onChange={(e) => updateParam('type', e.target.value)}
          className="rounded-2xl border border-input bg-background/80 px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-ring/20"
        >
          <option value="">All types</option>
          {KNOWLEDGE_TYPES.map((t) => (
            <option key={t} value={t}>{t}</option>
          ))}
        </select>
        <select
          value={vaultId}
          onChange={(e) => updateParam('vaultId', e.target.value)}
          className="rounded-2xl border border-input bg-background/80 px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-ring/20"
        >
          <option value="">All vaults</option>
          {vaultsQuery.data?.vaults.map((v) => (
            <option key={v.id} value={v.id}>{v.name}</option>
          ))}
        </select>
        <select
          value={createdByUserId}
          onChange={(e) => updateParam('createdByUserId', e.target.value)}
          className="rounded-2xl border border-input bg-background/80 px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-ring/20"
        >
          <option value="">All creators</option>
          {creatorsQuery.data?.map((c) => (
            <option key={c.id} value={c.id}>{c.name}</option>
          ))}
        </select>

        {activeFilterCount > 0 && (
          <button
            onClick={clearFilters}
            className="inline-flex items-center gap-1.5 rounded-2xl border border-input px-3 py-2 text-sm text-muted-foreground transition-all duration-200 hover:bg-muted/50 hover:text-foreground"
          >
            <Filter size={14} />
            <span className="inline-flex items-center justify-center w-4 h-4 text-[10px] font-bold bg-primary text-primary-foreground rounded-full">
              {activeFilterCount}
            </span>
            Clear
          </button>
        )}
        <div className="ml-auto">
          <ViewModeToggle pageKey="knowledge" />
        </div>
      </div>

      {tag && (
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-xs text-muted-foreground">Filtered by tag:</span>
          <button
            type="button"
            onClick={clearTagFilter}
            className="inline-flex items-center gap-1.5 rounded-full border border-border/70 bg-card px-3 py-1 text-xs font-medium text-foreground transition-colors hover:bg-muted"
            title="Remove tag filter"
          >
            #{tag}
            <X size={12} />
          </button>
        </div>
      )}

      {/* Error */}
      {error && (
        <SurfaceCard className="p-5">
          <p className="text-red-600 dark:text-red-400">
            {toUserError(error, 'We could not load your knowledge.')}
          </p>
        </SurfaceCard>
      )}

      {/* Knowledge list — view-mode branched */}
      {isLoading ? (
        <SurfaceCard className="overflow-x-auto">
          <table className="w-full text-sm text-left">
            <thead>
              <tr className="border-b border-border/60">
                <th className="py-2.5 px-3 w-10"><div className="w-4 h-4 bg-muted rounded" /></th>
                <th className="py-2.5 px-3"><div className="w-32 h-3 bg-muted rounded" /></th>
                <th className="py-2.5 px-3 w-24"><div className="w-12 h-3 bg-muted rounded" /></th>
                <th className="py-2.5 px-3 w-28"><div className="w-16 h-3 bg-muted rounded" /></th>
                <th className="py-2.5 px-3 w-20"><div className="w-12 h-3 bg-muted rounded" /></th>
                <th className="py-2.5 px-3 w-28"><div className="w-16 h-3 bg-muted rounded" /></th>
                <th className="py-2.5 px-3 w-24"><div className="w-12 h-3 bg-muted rounded" /></th>
              </tr>
            </thead>
            <tbody>
              {Array.from({ length: 5 }).map((_, i) => (
                <tr key={i} className="border-b border-border/40">
                  <td className="py-3 px-3"><div className="w-4 h-4 bg-muted rounded animate-pulse" /></td>
                  <td className="py-3 px-3">
                    <div className="w-48 h-4 bg-muted rounded animate-pulse mb-1" />
                    <div className="w-72 h-3 bg-muted rounded animate-pulse" />
                  </td>
                  <td className="py-3 px-3"><div className="w-14 h-5 bg-muted rounded animate-pulse" /></td>
                  <td className="py-3 px-3"><div className="w-20 h-5 bg-muted rounded animate-pulse" /></td>
                  <td className="py-3 px-3"><div className="w-14 h-5 bg-muted rounded animate-pulse" /></td>
                  <td className="py-3 px-3"><div className="w-16 h-3 bg-muted rounded animate-pulse" /></td>
                  <td className="py-3 px-3"><div className="w-12 h-3 bg-muted rounded animate-pulse" /></td>
                </tr>
              ))}
            </tbody>
          </table>
        </SurfaceCard>
      ) : (
        <>
          {/* Grid view */}
          {viewMode === 'grid' && (
            <div data-testid="knowledge-list-view-grid">
              {currentItems.length === 0 ? (
                renderEmpty()
              ) : (
                <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
                  {currentItems.map((item) => {
                    const TypeIcon = TYPE_ICONS[item.type] || StickyNote
                    return (
                      <div
                        key={item.id}
                        onClick={() => navigate(`/knowledge/${item.id}`)}
                        className={`sh-surface rounded-xl p-4 cursor-pointer hover:bg-muted/50 transition-colors border border-border/60 space-y-2 ${selectedIds.has(item.id) ? 'ring-2 ring-primary' : ''}`}
                      >
                        <div className="flex items-start justify-between gap-2">
                          <div className="flex items-center gap-1.5" onClick={(e) => e.stopPropagation()}>
                            <input
                              type="checkbox"
                              checked={selectedIds.has(item.id)}
                              onChange={() => toggleSelect(item.id)}
                              className="rounded border-input"
                            />
                          </div>
                          <span className="inline-flex items-center gap-1 px-2 py-0.5 text-xs bg-muted rounded">
                            <TypeIcon size={11} />
                            {item.type}
                          </span>
                        </div>
                        <p className="font-medium text-sm line-clamp-2">{item.title}</p>
                        {item.summary && (
                          <p className="text-xs text-muted-foreground line-clamp-2">{item.summary}</p>
                        )}
                        <div className="flex items-center justify-between pt-1">
                          {item.vaultName ? (
                            <span className="inline-flex items-center gap-1 text-xs text-muted-foreground">
                              <Archive size={10} />
                              {item.vaultName}
                            </span>
                          ) : (
                            <span />
                          )}
                          <span className="text-xs text-muted-foreground">{relativeTime(item.createdAt)}</span>
                        </div>
                      </div>
                    )
                  })}
                </div>
              )}
            </div>
          )}

          {/* Compact view */}
          {viewMode === 'compact' && (
            <div data-testid="knowledge-list-view-compact">
              {currentItems.length === 0 ? (
                renderEmpty()
              ) : (
                <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 2xl:grid-cols-5 gap-2">
                  {currentItems.map((item) => {
                    const TypeIcon = TYPE_ICONS[item.type] || StickyNote
                    return (
                      <div
                        key={item.id}
                        onClick={() => navigate(`/knowledge/${item.id}`)}
                        className={`sh-surface rounded-lg px-3 py-2 cursor-pointer hover:bg-muted/50 transition-colors border border-border/60 flex items-center gap-2 ${selectedIds.has(item.id) ? 'ring-2 ring-primary' : ''}`}
                      >
                        <div onClick={(e) => e.stopPropagation()}>
                          <input
                            type="checkbox"
                            checked={selectedIds.has(item.id)}
                            onChange={() => toggleSelect(item.id)}
                            className="rounded border-input"
                          />
                        </div>
                        <TypeIcon size={12} className="text-muted-foreground flex-shrink-0" />
                        <span className="text-xs font-medium truncate flex-1">{item.title}</span>
                        <span className="text-[10px] text-muted-foreground flex-shrink-0">{relativeTime(item.createdAt)}</span>
                      </div>
                    )
                  })}
                </div>
              )}
            </div>
          )}

          {/* List view */}
          {viewMode === 'list' && (
            <div data-testid="knowledge-list-view-list">
              <SurfaceCard className="overflow-hidden">
                <table className="w-full text-sm text-left table-fixed">
                  <thead>
                    <tr className="border-b border-border/60 text-[11px] uppercase tracking-wider text-muted-foreground bg-muted/30">
                      <th className="py-2.5 px-3 w-10">
                        <input
                          type="checkbox"
                          checked={allSelected}
                          onChange={toggleSelectAll}
                          disabled={currentItems.length === 0}
                          className="rounded border-input"
                        />
                      </th>
                      <th className="py-2.5 px-3 font-semibold cursor-pointer select-none" onClick={() => toggleSort('title')}>
                        Title{sortIndicator('title')}
                      </th>
                      <th className="py-2.5 px-3 font-semibold w-24">Type</th>
                      <th className="py-2.5 px-3 font-semibold w-28">Vault</th>
                      <th className="py-2.5 px-3 font-semibold w-20">Status</th>
                      <th className="py-2.5 px-3 font-semibold w-28">Creator</th>
                      <th className="py-2.5 px-3 font-semibold cursor-pointer select-none w-24" onClick={() => toggleSort('created')}>
                        Created{sortIndicator('created')}
                      </th>
                    </tr>
                  </thead>
                  <tbody>
                    {currentItems.map((item) => {
                      const TypeIcon = TYPE_ICONS[item.type] || StickyNote
                      return (
                        <tr
                          key={item.id}
                          className={`border-b border-border/30 hover:bg-muted/50 cursor-pointer transition-colors duration-150 ${selectedIds.has(item.id) ? 'bg-primary/5 dark:bg-primary/10' : ''}`}
                        >
                          <td className="py-2.5 px-3" onClick={(e) => e.stopPropagation()}>
                            <input
                              type="checkbox"
                              checked={selectedIds.has(item.id)}
                              onChange={() => toggleSelect(item.id)}
                              className="rounded border-input"
                            />
                          </td>
                          <td className="py-2.5 px-3" onClick={() => navigate(`/knowledge/${item.id}`)}>
                            <p className="font-medium truncate max-w-md">{item.title}</p>
                            {item.summary && (
                              <p className="text-xs text-muted-foreground truncate max-w-md mt-0.5">{item.summary}</p>
                            )}
                          </td>
                          <td className="py-2.5 px-3" onClick={() => navigate(`/knowledge/${item.id}`)}>
                            <span className="inline-flex items-center gap-1.5 px-2 py-0.5 text-xs bg-muted rounded">
                              <TypeIcon size={12} />
                              {item.type}
                            </span>
                          </td>
                          <td className="py-2.5 px-3" onClick={() => navigate(`/knowledge/${item.id}`)}>
                            {item.vaultName ? (
                              <span className="inline-flex items-center gap-1 px-2 py-0.5 text-xs bg-blue-50 dark:bg-blue-900/20 text-blue-700 dark:text-blue-300 rounded">
                                <Archive size={11} />
                                {item.vaultName}
                              </span>
                            ) : (
                              <span className="text-xs text-muted-foreground">&mdash;</span>
                            )}
                          </td>
                          <td className="py-2.5 px-3" onClick={() => navigate(`/knowledge/${item.id}`)}>
                            <span className="inline-flex items-center gap-1.5 text-xs">
                              <span className={`inline-block w-2 h-2 rounded-full ${item.isIndexed ? 'bg-green-500' : 'bg-muted-foreground/40'}`} />
                              {item.isIndexed ? 'Indexed' : 'Pending'}
                            </span>
                          </td>
                          <td className="py-2.5 px-3 text-xs text-muted-foreground" onClick={() => navigate(`/knowledge/${item.id}`)}>
                            {item.createdByUserName || <span className="text-muted-foreground">&mdash;</span>}
                          </td>
                          <td className="py-2.5 px-3 text-xs text-muted-foreground" onClick={() => navigate(`/knowledge/${item.id}`)} title={fmt.dateTime(item.createdAt)}>
                            {relativeTime(item.createdAt)}
                          </td>
                        </tr>
                      )
                    })}
                    {currentItems.length === 0 && (
                      <tr>
                        <td colSpan={7} className="py-16 text-center">
                          <EmptyState activeFilterCount={activeFilterCount} clearFilters={clearFilters} />
                        </td>
                      </tr>
                    )}
                  </tbody>
                </table>
              </SurfaceCard>
            </div>
          )}

          {/* Gallery view */}
          {viewMode === 'gallery' && (
            <div data-testid="knowledge-list-view-gallery">
              {currentItems.length === 0 ? (
                renderEmpty()
              ) : (
                <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-6">
                  {currentItems.map((item) => {
                    const TypeIcon = TYPE_ICONS[item.type] || StickyNote
                    return (
                      <div
                        key={item.id}
                        onClick={() => navigate(`/knowledge/${item.id}`)}
                        className={`sh-surface rounded-xl overflow-hidden cursor-pointer hover:shadow-md transition-shadow border border-border/60 ${selectedIds.has(item.id) ? 'ring-2 ring-primary' : ''}`}
                      >
                        <div className="h-32 bg-muted flex items-center justify-center relative">
                          <TypeIcon size={40} className="text-muted-foreground/30" />
                          <div className="absolute top-2 left-2" onClick={(e) => e.stopPropagation()}>
                            <input
                              type="checkbox"
                              checked={selectedIds.has(item.id)}
                              onChange={() => toggleSelect(item.id)}
                              className="rounded border-input"
                            />
                          </div>
                          <span className="absolute top-2 right-2 inline-flex items-center gap-1 px-2 py-0.5 text-xs bg-background/80 backdrop-blur-sm rounded border border-border/60">
                            <TypeIcon size={10} />
                            {item.type}
                          </span>
                        </div>
                        <div className="p-4 space-y-1">
                          <p className="font-semibold text-sm line-clamp-1">{item.title}</p>
                          {item.summary && (
                            <p className="text-xs text-muted-foreground line-clamp-2">{item.summary}</p>
                          )}
                          <div className="flex items-center justify-between pt-2">
                            {item.vaultName ? (
                              <span className="inline-flex items-center gap-1 text-xs text-muted-foreground">
                                <Archive size={10} />
                                {item.vaultName}
                              </span>
                            ) : (
                              <span />
                            )}
                            <span className="text-xs text-muted-foreground">{relativeTime(item.createdAt)}</span>
                          </div>
                        </div>
                      </div>
                    )
                  })}
                </div>
              )}
            </div>
          )}

          {/* Code view */}
          {viewMode === 'code' && (
            <div data-testid="knowledge-list-view-code">
              <SurfaceCard className="overflow-hidden font-mono">
                <div className="border-b border-border/60 bg-muted/30 px-4 py-2 flex items-center gap-2">
                  <input
                    type="checkbox"
                    checked={allSelected}
                    onChange={toggleSelectAll}
                    disabled={currentItems.length === 0}
                    className="rounded border-input"
                  />
                  <span className="text-xs text-muted-foreground uppercase tracking-wider">Name</span>
                  <span className="ml-auto text-xs text-muted-foreground uppercase tracking-wider">Modified</span>
                </div>
                {currentItems.length === 0 ? (
                  <div className="py-16 text-center">
                    <EmptyState activeFilterCount={activeFilterCount} clearFilters={clearFilters} />
                  </div>
                ) : currentItems.map((item) => {
                  const TypeIcon = TYPE_ICONS[item.type] || StickyNote
                  return (
                    <div
                      key={item.id}
                      className={`flex items-center gap-3 px-4 py-2 border-b border-border/30 hover:bg-muted/50 cursor-pointer transition-colors ${selectedIds.has(item.id) ? 'bg-primary/5' : ''}`}
                    >
                      <div onClick={(e) => e.stopPropagation()}>
                        <input
                          type="checkbox"
                          checked={selectedIds.has(item.id)}
                          onChange={() => toggleSelect(item.id)}
                          className="rounded border-input"
                        />
                      </div>
                      <TypeIcon size={14} className="text-muted-foreground flex-shrink-0" />
                      <span
                        className="text-sm flex-1 truncate"
                        onClick={() => navigate(`/knowledge/${item.id}`)}
                      >
                        {item.title}
                      </span>
                      {item.vaultName && (
                        <span className="text-xs text-muted-foreground hidden sm:inline">{item.vaultName}</span>
                      )}
                      <span
                        className="text-xs text-muted-foreground flex-shrink-0"
                        onClick={() => navigate(`/knowledge/${item.id}`)}
                        title={fmt.dateTime(item.createdAt)}
                      >
                        {relativeTime(item.createdAt)}
                      </span>
                    </div>
                  )
                })}
              </SurfaceCard>
            </div>
          )}

          {/* Default table fallback for safety (should never show — viewMode always matches one branch) */}
          {viewMode !== 'grid' && viewMode !== 'compact' && viewMode !== 'list' && viewMode !== 'gallery' && viewMode !== 'code' && (
            <SurfaceCard className="overflow-hidden">
              <table className="w-full text-sm text-left table-fixed">
                <thead>
                  <tr className="border-b border-border/60 text-[11px] uppercase tracking-wider text-muted-foreground bg-muted/30">
                    <th className="py-2.5 px-3 w-10">
                      <input type="checkbox" checked={allSelected} onChange={toggleSelectAll} disabled={currentItems.length === 0} className="rounded border-input" />
                    </th>
                    <th className="py-2.5 px-3 font-semibold cursor-pointer select-none" onClick={() => toggleSort('title')}>Title{sortIndicator('title')}</th>
                    <th className="py-2.5 px-3 font-semibold w-24">Type</th>
                    <th className="py-2.5 px-3 font-semibold w-28">Vault</th>
                    <th className="py-2.5 px-3 font-semibold w-20">Status</th>
                    <th className="py-2.5 px-3 font-semibold w-28">Creator</th>
                    <th className="py-2.5 px-3 font-semibold cursor-pointer select-none w-24" onClick={() => toggleSort('created')}>Created{sortIndicator('created')}</th>
                  </tr>
                </thead>
                <tbody>
                  {currentItems.length === 0 && (
                    <tr><td colSpan={7} className="py-16 text-center"><EmptyState activeFilterCount={activeFilterCount} clearFilters={clearFilters} /></td></tr>
                  )}
                </tbody>
              </table>
            </SurfaceCard>
          )}


          {/* Pagination */}
          {data && (data.totalPages > 1 || pageSize !== 20) && (
            <div className="flex flex-col gap-3 rounded-[22px] border border-border/60 bg-card/80 px-4 py-4 shadow-sm sm:flex-row sm:items-center sm:justify-between">
              <div className="flex items-center gap-3">
                <p className="text-sm text-muted-foreground">
                  Showing {Math.min((page - 1) * pageSize + 1, data.totalItems)}&ndash;{Math.min(page * pageSize, data.totalItems)} of {data.totalItems} items
                </p>
                <select
                  value={pageSize}
                  onChange={(e) => updateParam('pageSize', e.target.value)}
                  className="rounded-xl border border-input bg-background/80 px-2 py-1 text-xs"
                >
                  {PAGE_SIZES.map((s) => (
                    <option key={s} value={s}>{s} / page</option>
                  ))}
                </select>
              </div>
              <div className="flex items-center gap-1">
                <button
                  disabled={page <= 1}
                  onClick={() => updateParam('page', String(page - 1))}
                  className="rounded-xl border border-input p-1.5 text-sm transition-colors disabled:opacity-30"
                >
                  <ChevronLeft size={16} />
                </button>
                {pageNumbers.map((n) => (
                  <button
                    key={n}
                    onClick={() => updateParam('page', String(n))}
                    className={`rounded-xl border px-2.5 py-1 text-sm transition-colors ${
                      n === page
                        ? 'bg-primary text-primary-foreground border-primary'
                        : 'border-input hover:bg-muted'
                    }`}
                  >
                    {n}
                  </button>
                ))}
                <button
                  disabled={page >= totalPages}
                  onClick={() => updateParam('page', String(page + 1))}
                  className="rounded-xl border border-input p-1.5 text-sm transition-colors disabled:opacity-30"
                >
                  <ChevronRight size={16} />
                </button>
              </div>
            </div>
          )}
        </>
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
              onClick={() => setShowMoveToVault(true)}
              disabled={isBatchOperating}
              className="inline-flex items-center gap-1.5 px-3 py-1.5 text-sm bg-primary/80 hover:bg-primary/70 rounded disabled:opacity-50 transition-colors"
            >
              <FolderInput size={14} /> Move to Vault
            </button>
            <button
              onClick={() => { setBatchError(''); batchReprocessMut.mutate(Array.from(selectedIds)) }}
              disabled={isBatchOperating}
              className="inline-flex items-center gap-1.5 px-3 py-1.5 text-sm bg-primary/80 hover:bg-primary/70 rounded disabled:opacity-50 transition-colors"
            >
              <RefreshCw size={14} className={batchReprocessMut.isPending ? 'animate-spin' : ''} /> {batchReprocessMut.isPending ? 'Reprocessing...' : 'Reprocess'}
            </button>
            <button
              onClick={() => { setBatchError(''); setShowDeleteConfirm(true) }}
              disabled={isBatchOperating}
              className="inline-flex items-center gap-1.5 px-3 py-1.5 text-sm bg-red-600 hover:bg-red-700 text-white rounded disabled:opacity-50 transition-colors"
            >
              <Trash2 size={14} /> Delete
            </button>
            <button
              onClick={() => setSelectedIds(new Set())}
              disabled={isBatchOperating}
              className="inline-flex items-center gap-1 px-2 py-1.5 text-sm hover:bg-primary/70 rounded transition-colors"
              title="Deselect all"
            >
              <X size={14} />
            </button>
          </div>
        </div>
      )}

      {/* Batch delete confirmation modal */}
      {showDeleteConfirm && (
        <div className="fixed inset-0 bg-black/50 z-50 flex items-center justify-center p-4">
          <div className="bg-card rounded-xl p-6 max-w-sm w-full space-y-4 shadow-sm">
            <h2 className="text-lg font-semibold">Delete {selectedIds.size} item{selectedIds.size !== 1 ? 's' : ''}?</h2>
            <p className="text-sm text-muted-foreground">
              Are you sure you want to delete {selectedIds.size} knowledge item{selectedIds.size !== 1 ? 's' : ''}? This action cannot be undone.
            </p>
            {batchError && (
              <p className="text-red-600 dark:text-red-400 text-sm">{batchError}</p>
            )}
            <div className="flex gap-2 justify-end">
              <button
                onClick={() => { setShowDeleteConfirm(false); setBatchError('') }}
                disabled={batchDeleteMut.isPending}
                className="px-4 py-2 border border-input rounded-md text-sm transition-colors"
              >
                Cancel
              </button>
              <button
                onClick={() => batchDeleteMut.mutate(Array.from(selectedIds))}
                disabled={batchDeleteMut.isPending}
                className="inline-flex items-center gap-2 px-4 py-2 bg-red-600 text-white rounded-md text-sm font-medium disabled:opacity-50"
              >
                {batchDeleteMut.isPending ? (
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

      {/* Move to vault modal */}
      {showMoveToVault && (
        <div className="fixed inset-0 bg-black/50 z-50 flex items-center justify-center p-4">
          <div className="bg-card rounded-xl p-6 max-w-sm w-full space-y-4 shadow-sm">
            <h2 className="text-lg font-semibold">
              Move {selectedIds.size} item{selectedIds.size !== 1 ? 's' : ''} to vault
            </h2>
            <p className="text-sm text-muted-foreground">
              Select a vault to move the selected items to.
            </p>
            {batchError && (
              <p className="text-red-600 dark:text-red-400 text-sm">{batchError}</p>
            )}
            <div className="space-y-2">
              {moveVaults.isLoading ? (
                <div className="h-10 bg-muted rounded animate-pulse" />
              ) : (
                moveVaults.data?.vaults.map((vault) => (
                  <button
                    key={vault.id}
                    onClick={() => batchMoveMut.mutate({ ids: Array.from(selectedIds), vaultId: vault.id })}
                    disabled={batchMoveMut.isPending}
                    className="w-full text-left px-4 py-2.5 border border-border/60 rounded-md hover:bg-muted text-sm transition-colors disabled:opacity-50"
                  >
                    <span className="font-medium">{vault.name}</span>
                    {vault.description && (
                      <span className="text-muted-foreground ml-2">
                        {vault.description}
                      </span>
                    )}
                  </button>
                ))
              )}
            </div>
            {batchMoveMut.isPending && (
              <div className="flex items-center gap-2 text-sm text-muted-foreground">
                <Loader2 size={14} className="animate-spin" /> Moving items...
              </div>
            )}
            <div className="flex justify-end">
              <button
                onClick={() => { setShowMoveToVault(false); setBatchError('') }}
                disabled={batchMoveMut.isPending}
                className="px-4 py-2 border border-input rounded-md text-sm transition-colors"
              >
                Cancel
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}

function EmptyState({ activeFilterCount, clearFilters }: { activeFilterCount: number; clearFilters: () => void }) {
  return (
    <div className="flex flex-col items-center gap-3">
      <PackageOpen size={40} className="text-muted-foreground/40" />
      <p className="text-muted-foreground text-sm">
        {activeFilterCount > 0 ? 'No items match the current filters.' : 'No knowledge items yet.'}
      </p>
      {activeFilterCount > 0 ? (
        <button onClick={clearFilters} className="text-sm text-muted-foreground hover:text-foreground underline transition-colors">
          Clear all filters
        </button>
      ) : (
        <Link
          to="/knowledge/new"
          data-testid="knowledge-empty-cta"
          className="inline-flex items-center gap-1.5 px-4 py-2 bg-primary text-primary-foreground rounded-lg t-label font-medium transition-colors"
        >
          <Plus size={14} /> New Knowledge
        </Link>
      )}
    </div>
  )
}
