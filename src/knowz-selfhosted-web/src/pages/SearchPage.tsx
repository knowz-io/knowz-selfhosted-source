import { useState, useEffect } from 'react'
import { toUserError } from '../lib/toUserError'
import { useSearchParams, Link } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { api } from '../lib/api-client'
import { Search, SlidersHorizontal, X, MessageCircleQuestion } from 'lucide-react'
import AskPage from './AskPage'
import SurfaceCard from '../components/ui/SurfaceCard'

const KNOWLEDGE_TYPES = ['Note', 'Document', 'Email', 'Image', 'Audio', 'Video', 'Code', 'Link', 'MeetingMinutes']

export default function SearchPage() {
  const [searchParams, setSearchParams] = useSearchParams()
  const mode = searchParams.get('mode') === 'ask' ? 'ask' : 'search'
  const queryParam = searchParams.get('q') || ''
  const [inputValue, setInputValue] = useState(queryParam)
  const [showFilters, setShowFilters] = useState(() => {
    return !!(
      searchParams.get('vaultId') ||
      searchParams.get('type') ||
      searchParams.get('startDate') ||
      searchParams.get('endDate') ||
      searchParams.get('tags')
    )
  })

  const vaultId = searchParams.get('vaultId') || ''
  const typeFilter = searchParams.get('type') || ''
  const startDate = searchParams.get('startDate') || ''
  const endDate = searchParams.get('endDate') || ''
  const tagsFilter = searchParams.get('tags') || ''

  const hasActiveFilters = !!(vaultId || typeFilter || startDate || endDate || tagsFilter)

  const vaults = useQuery({
    queryKey: ['vaults', 'search-filters'],
    queryFn: () => api.listVaults(false),
    enabled: showFilters && mode === 'search',
  })

  useEffect(() => {
    if (mode !== 'search') return
    const timer = setTimeout(() => {
      const params = new URLSearchParams(searchParams)
      if (inputValue) {
        params.set('q', inputValue)
      } else {
        params.delete('q')
      }
      setSearchParams(params, { replace: true })
    }, 300)
    return () => clearTimeout(timer)
  }, [inputValue, searchParams, setSearchParams, mode])

  const updateFilter = (key: string, value: string) => {
    const params = new URLSearchParams(searchParams)
    if (value) {
      params.set(key, value)
    } else {
      params.delete(key)
    }
    setSearchParams(params, { replace: true })
  }

  const clearFilters = () => {
    const params = new URLSearchParams()
    if (queryParam) params.set('q', queryParam)
    setSearchParams(params, { replace: true })
  }

  const { data, isLoading, error } = useQuery({
    queryKey: ['search', queryParam, vaultId, typeFilter, startDate, endDate, tagsFilter],
    queryFn: () =>
      api.search(queryParam, {
        vaultId: vaultId || undefined,
        type: typeFilter || undefined,
        startDate: startDate || undefined,
        endDate: endDate || undefined,
        tags: tagsFilter || undefined,
      }),
    enabled: queryParam.length > 0 && mode === 'search',
  })

  const handleModeToggle = (newMode: 'search' | 'ask') => {
    const params = new URLSearchParams()
    if (newMode === 'ask') {
      params.set('mode', 'ask')
    }
    setSearchParams(params, { replace: true })
  }

  return (
    <div className="space-y-4">
      <div className="sh-toolbar flex flex-wrap items-center justify-between gap-3 p-2">
        <div className="flex rounded-[18px] bg-muted/60 p-1">
          <button
            onClick={() => handleModeToggle('search')}
            className={`inline-flex items-center gap-1.5 rounded-2xl px-4 py-2 text-sm font-medium transition-colors ${
              mode === 'search'
                ? 'bg-card text-foreground shadow-sm'
                : 'text-muted-foreground hover:text-foreground'
            }`}
          >
            <Search size={14} />
            Search
          </button>
          <button
            onClick={() => handleModeToggle('ask')}
            className={`inline-flex items-center gap-1.5 rounded-2xl px-4 py-2 text-sm font-medium transition-colors ${
              mode === 'ask'
                ? 'bg-card text-foreground shadow-sm'
                : 'text-muted-foreground hover:text-foreground'
            }`}
          >
            <MessageCircleQuestion size={14} />
            Ask
          </button>
        </div>
        <button
          onClick={() => setShowFilters(!showFilters)}
          className={`inline-flex items-center gap-2 rounded-2xl border px-4 py-2 text-sm font-medium transition-colors ${
            showFilters || hasActiveFilters
              ? 'border-primary/30 bg-primary/10 text-primary'
              : 'border-border/70 bg-card/70 text-muted-foreground hover:text-foreground'
          }`}
        >
          <SlidersHorizontal size={14} />
          {showFilters ? 'Hide filters' : 'Show filters'}
        </button>
      </div>

      {mode === 'ask' ? (
        <AskPage />
      ) : (
        <div className="space-y-4">
          <SurfaceCard className="p-5">
            <div className="relative">
              <Search size={18} className="absolute left-4 top-1/2 -translate-y-1/2 text-muted-foreground" />
              <input
                type="text"
                value={inputValue}
                onChange={(e) => setInputValue(e.target.value)}
                placeholder="Search knowledge, files, and summaries..."
                autoFocus
                className="w-full rounded-[22px] border border-input bg-background/80 py-3 pl-12 pr-12 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-ring/30"
              />
              <button
                onClick={() => setShowFilters(!showFilters)}
                className={`absolute right-3 top-1/2 -translate-y-1/2 rounded-2xl p-2 transition-colors ${
                  showFilters || hasActiveFilters
                    ? 'bg-primary/10 text-primary'
                    : 'text-muted-foreground hover:text-foreground'
                }`}
                title={showFilters ? 'Hide filters' : 'Show filters'}
              >
                <SlidersHorizontal size={16} />
              </button>
            </div>
          </SurfaceCard>

          {showFilters && (
            <SurfaceCard className="p-5">
              <div className="mb-4 flex items-center justify-between gap-3">
                <div>
                  <p className="sh-kicker">Filters</p>
                  <h3 className="mt-2 text-lg font-semibold">Narrow the result set</h3>
                </div>
                {hasActiveFilters && (
                  <button
                    onClick={clearFilters}
                    className="inline-flex items-center gap-1.5 text-sm text-muted-foreground transition-colors hover:text-foreground"
                  >
                    <X size={14} /> Clear filters
                  </button>
                )}
              </div>
              <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-4">
                <div>
                  <label className="mb-1 block text-xs font-medium text-muted-foreground">Vault</label>
                  <select
                    value={vaultId}
                    onChange={(e) => updateFilter('vaultId', e.target.value)}
                    className="w-full rounded-2xl border border-input bg-background/80 px-3 py-2 text-sm"
                  >
                    <option value="">All vaults</option>
                    {vaults.data?.vaults.map((v) => (
                      <option key={v.id} value={v.id}>
                        {v.name}
                      </option>
                    ))}
                  </select>
                </div>

                <div>
                  <label className="mb-1 block text-xs font-medium text-muted-foreground">Type</label>
                  <select
                    value={typeFilter}
                    onChange={(e) => updateFilter('type', e.target.value)}
                    className="w-full rounded-2xl border border-input bg-background/80 px-3 py-2 text-sm"
                  >
                    <option value="">All types</option>
                    {KNOWLEDGE_TYPES.map((t) => (
                      <option key={t} value={t}>
                        {t}
                      </option>
                    ))}
                  </select>
                </div>

                <div>
                  <label className="mb-1 block text-xs font-medium text-muted-foreground">From</label>
                  <input
                    type="date"
                    value={startDate}
                    onChange={(e) => updateFilter('startDate', e.target.value)}
                    className="w-full rounded-2xl border border-input bg-background/80 px-3 py-2 text-sm"
                  />
                </div>

                <div>
                  <label className="mb-1 block text-xs font-medium text-muted-foreground">To</label>
                  <input
                    type="date"
                    value={endDate}
                    onChange={(e) => updateFilter('endDate', e.target.value)}
                    className="w-full rounded-2xl border border-input bg-background/80 px-3 py-2 text-sm"
                  />
                </div>
              </div>

              <div className="mt-3">
                <label className="mb-1 block text-xs font-medium text-muted-foreground">Tags (comma-separated)</label>
                <input
                  type="text"
                  value={tagsFilter}
                  onChange={(e) => updateFilter('tags', e.target.value)}
                  placeholder="e.g. finance, quarterly"
                  className="w-full rounded-2xl border border-input bg-background/80 px-3 py-2 text-sm"
                />
              </div>
            </SurfaceCard>
          )}

          {error && (
            <SurfaceCard className="p-5">
              <p className="text-red-600 dark:text-red-400">
                {toUserError(error, 'Search failed.')}
              </p>
            </SurfaceCard>
          )}

          {isLoading && queryParam && (
            <div className="space-y-3">
              {Array.from({ length: 3 }).map((_, i) => (
                <div key={i} className="sh-surface h-24 animate-pulse" />
              ))}
            </div>
          )}

          {data && (
            <div className="space-y-3">
              <p className="text-sm text-muted-foreground">
                {data.totalResults} result{data.totalResults !== 1 ? 's' : ''}
                {hasActiveFilters && ' (filtered)'}
              </p>
              {data.items.map((result) => (
                <Link
                  key={result.knowledgeId}
                  to={`/knowledge/${result.knowledgeId}`}
                  className="block"
                >
                  <SurfaceCard className="p-5 transition-all duration-200 hover:-translate-y-0.5 hover:bg-card">
                    <div className="mb-2 flex items-center gap-2">
                      <h3 className="font-semibold">{result.title}</h3>
                      {result.knowledgeType && (
                        <span className="rounded-full bg-muted px-2 py-0.5 text-xs text-muted-foreground">
                          {result.knowledgeType}
                        </span>
                      )}
                      <span className="ml-auto text-xs text-muted-foreground">
                        {Math.round(result.score * 100)}%
                      </span>
                    </div>
                    <p className="line-clamp-2 text-sm text-muted-foreground">
                      {result.summary || result.content}
                    </p>
                    <div className="mt-3 flex flex-wrap gap-2">
                      {result.vaultName && (
                        <span className="text-xs text-muted-foreground">
                          {result.vaultName}
                        </span>
                      )}
                      {result.tags.map((tag) => (
                        <span
                          key={tag}
                          className="rounded-full bg-blue-50 px-2 py-0.5 text-xs text-blue-700 dark:bg-blue-900/30 dark:text-blue-300"
                        >
                          {tag}
                        </span>
                      ))}
                    </div>
                  </SurfaceCard>
                </Link>
              ))}
              {data.items.length === 0 && queryParam && (
                <SurfaceCard className="p-10 text-center">
                  <p className="text-muted-foreground">
                    No results found for &quot;{queryParam}&quot;.
                    {hasActiveFilters && ' Try adjusting your filters.'}
                  </p>
                </SurfaceCard>
              )}
            </div>
          )}

          {!queryParam && !isLoading && (
            <SurfaceCard className="p-10 text-center">
              <p className="text-muted-foreground">
                Type a query to search your knowledge base.
              </p>
            </SurfaceCard>
          )}
        </div>
      )}
    </div>
  )
}
