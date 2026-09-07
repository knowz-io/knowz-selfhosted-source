import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import {
  ClipboardList,
  RefreshCw,
  Loader2,
  ChevronLeft,
  ChevronRight,
  Search,
  Filter,
} from 'lucide-react'
import { api } from '../../lib/api-client'
import { useFormatters } from '../../hooks/useFormatters'

const ENTITY_TYPES = [
  { value: '', label: 'All Types' },
  { value: 'Knowledge', label: 'Knowledge' },
  { value: 'Vault', label: 'Vault' },
  { value: 'User', label: 'User' },
  { value: 'Tenant', label: 'Tenant' },
  { value: 'File', label: 'File' },
  { value: 'Tag', label: 'Tag' },
  { value: 'Comment', label: 'Comment' },
  { value: 'GitSync', label: 'Git Sync' },
]

const PAGE_SIZE = 25

export default function AuditLogPage() {
  const fmt = useFormatters()
  const [page, setPage] = useState(1)
  const [entityType, setEntityType] = useState('')
  const [entityIdSearch, setEntityIdSearch] = useState('')
  const [appliedEntityId, setAppliedEntityId] = useState('')

  const { data, isLoading, error, isFetching, refetch } = useQuery({
    queryKey: ['admin', 'audit-logs', page, entityType, appliedEntityId],
    queryFn: () =>
      api.getAuditLogs({
        page,
        pageSize: PAGE_SIZE,
        entityType: entityType || undefined,
        entityId: appliedEntityId || undefined,
      }),
  })

  const handleEntityTypeChange = (value: string) => {
    setEntityType(value)
    setPage(1)
  }

  const handleSearchSubmit = (e: React.FormEvent) => {
    e.preventDefault()
    setAppliedEntityId(entityIdSearch.trim())
    setPage(1)
  }

  const handleClearSearch = () => {
    setEntityIdSearch('')
    setAppliedEntityId('')
    setPage(1)
  }

  if (error) {
    return (
      <div className="text-center py-12">
        <p className="text-red-600 dark:text-red-400 mb-4">
          {error instanceof Error ? error.message : 'Failed to load audit logs'}
        </p>
        <button
          onClick={() => refetch()}
          className="inline-flex items-center gap-2 px-4 py-2 bg-primary text-primary-foreground rounded-md text-sm font-medium transition-colors"
        >
          <RefreshCw size={16} /> Retry
        </button>
      </div>
    )
  }

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-3">
          <ClipboardList size={24} className="text-muted-foreground" />
          <h1 className="text-2xl font-bold">Audit Logs</h1>
        </div>
        <button
          onClick={() => refetch()}
          disabled={isFetching}
          className="inline-flex items-center gap-2 px-3 py-1.5 border border-input rounded-md text-sm font-medium hover:bg-muted transition-colors disabled:opacity-50"
        >
          {isFetching ? (
            <Loader2 size={14} className="animate-spin" />
          ) : (
            <RefreshCw size={14} />
          )}
          Refresh
        </button>
      </div>

      {/* Filters */}
      <div className="flex flex-col sm:flex-row gap-3">
        <div className="flex items-center gap-2">
          <Filter size={14} className="text-muted-foreground" />
          <select
            value={entityType}
            onChange={(e) => handleEntityTypeChange(e.target.value)}
            className="px-3 py-2 text-sm border border-input rounded-md bg-card focus:outline-none focus:ring-2 focus:ring-ring"
          >
            {ENTITY_TYPES.map((t) => (
              <option key={t.value} value={t.value}>
                {t.label}
              </option>
            ))}
          </select>
        </div>

        <form onSubmit={handleSearchSubmit} className="flex items-center gap-2 flex-1">
          <div className="relative flex-1 max-w-xs">
            <Search size={14} className="absolute left-3 top-1/2 -translate-y-1/2 text-muted-foreground" />
            <input
              type="text"
              value={entityIdSearch}
              onChange={(e) => setEntityIdSearch(e.target.value)}
              placeholder="Search by Entity ID..."
              className="w-full pl-9 pr-3 py-2 text-sm border border-input rounded-md bg-card focus:outline-none focus:ring-2 focus:ring-ring"
            />
          </div>
          <button
            type="submit"
            className="px-3 py-2 text-sm font-medium bg-primary text-primary-foreground rounded-md hover:opacity-90 transition-opacity"
          >
            Search
          </button>
          {appliedEntityId && (
            <button
              type="button"
              onClick={handleClearSearch}
              className="px-3 py-2 text-sm text-muted-foreground hover:text-foreground transition-colors"
            >
              Clear
            </button>
          )}
        </form>
      </div>

      {/* Table */}
      {isLoading ? (
        <div className="space-y-4">
          {[1, 2, 3, 4, 5].map((i) => (
            <div key={i} className="h-14 bg-muted rounded-lg animate-pulse" />
          ))}
        </div>
      ) : data && data.items.length > 0 ? (
        <div className="bg-card border border-border/60 rounded-xl shadow-sm overflow-hidden">
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-border/60 bg-muted/50">
                  <th className="text-left px-4 py-3 font-medium text-muted-foreground">Timestamp</th>
                  <th className="text-left px-4 py-3 font-medium text-muted-foreground">Action</th>
                  <th className="text-left px-4 py-3 font-medium text-muted-foreground">Entity Type</th>
                  <th className="text-left px-4 py-3 font-medium text-muted-foreground">Entity ID</th>
                  <th className="text-left px-4 py-3 font-medium text-muted-foreground">User</th>
                  <th className="text-left px-4 py-3 font-medium text-muted-foreground">Details</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-border">
                {data.items.map((entry) => (
                  <tr key={entry.id} className="hover:bg-muted/30 transition-colors">
                    <td className="px-4 py-3 whitespace-nowrap text-muted-foreground">
                      {fmt.dateTime(entry.timestamp)}
                    </td>
                    <td className="px-4 py-3">
                      <ActionBadge action={entry.action} />
                    </td>
                    <td className="px-4 py-3">
                      <span className="px-2 py-0.5 text-xs bg-muted rounded">
                        {entry.entityType}
                      </span>
                    </td>
                    <td className="px-4 py-3 font-mono text-xs text-muted-foreground max-w-[180px] truncate" title={entry.entityId}>
                      {entry.entityId}
                    </td>
                    <td className="px-4 py-3 text-muted-foreground">
                      {entry.userEmail || entry.userId || '-'}
                    </td>
                    <td className="px-4 py-3 text-muted-foreground max-w-[250px] truncate" title={entry.details ?? undefined}>
                      {entry.details || '-'}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          {/* Pagination */}
          {data.totalPages > 1 && (
            <div className="flex items-center justify-between px-4 py-3 border-t border-border/60 bg-muted/30">
              <p className="text-sm text-muted-foreground">
                Showing {(page - 1) * PAGE_SIZE + 1}-{Math.min(page * PAGE_SIZE, data.totalItems)} of {data.totalItems}
              </p>
              <div className="flex items-center gap-2">
                <button
                  onClick={() => setPage((p) => Math.max(1, p - 1))}
                  disabled={page <= 1}
                  className="inline-flex items-center gap-1 px-3 py-1.5 text-sm border border-input rounded-md hover:bg-muted disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                >
                  <ChevronLeft size={14} /> Previous
                </button>
                <span className="text-sm text-muted-foreground px-2">
                  Page {page} of {data.totalPages}
                </span>
                <button
                  onClick={() => setPage((p) => Math.min(data.totalPages, p + 1))}
                  disabled={page >= data.totalPages}
                  className="inline-flex items-center gap-1 px-3 py-1.5 text-sm border border-input rounded-md hover:bg-muted disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                >
                  Next <ChevronRight size={14} />
                </button>
              </div>
            </div>
          )}
        </div>
      ) : (
        <div className="text-center py-12 bg-card border border-border/60 rounded-xl">
          <ClipboardList size={40} className="mx-auto text-muted-foreground mb-3" />
          <p className="text-muted-foreground">No audit log entries found.</p>
          {(entityType || appliedEntityId) && (
            <p className="text-sm text-muted-foreground mt-1">
              Try adjusting your filters.
            </p>
          )}
        </div>
      )}
    </div>
  )
}

function ActionBadge({ action }: { action: string }) {
  const lower = action.toLowerCase()
  let classes = 'bg-muted text-muted-foreground'

  if (lower.includes('create') || lower.includes('add')) {
    classes = 'bg-green-50 dark:bg-green-950/30 text-green-700 dark:text-green-400'
  } else if (lower.includes('update') || lower.includes('edit') || lower.includes('modify')) {
    classes = 'bg-blue-50 dark:bg-blue-950/30 text-blue-700 dark:text-blue-400'
  } else if (lower.includes('delete') || lower.includes('remove')) {
    classes = 'bg-red-50 dark:bg-red-950/30 text-red-700 dark:text-red-400'
  } else if (lower.includes('restore') || lower.includes('sync')) {
    classes = 'bg-amber-50 dark:bg-amber-950/30 text-amber-700 dark:text-amber-400'
  }

  return (
    <span className={`inline-flex px-2 py-0.5 rounded text-xs font-medium ${classes}`}>
      {action}
    </span>
  )
}
