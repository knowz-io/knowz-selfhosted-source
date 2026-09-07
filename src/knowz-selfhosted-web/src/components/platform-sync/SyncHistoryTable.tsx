import { Fragment, useState } from 'react'
import {
  History,
  RefreshCw,
  Loader2,
  ChevronDown,
  ChevronRight,
  CheckCircle2,
  XCircle,
  AlertCircle,
} from 'lucide-react'
import type {
  PlatformSyncRunDto,
  PlatformSyncRunStatus,
  VaultSyncStatusDto,
} from '../../lib/types'
import { parseAsUtc } from '../../lib/format-utils'
import { useFormatters } from '../../hooks/useFormatters'

interface SyncHistoryTableProps {
  history: PlatformSyncRunDto[]
  isLoading: boolean
  isFetching: boolean
  onRefresh: () => void
  limit: number
  limitOptions: readonly number[]
  onLimitChange: (limit: number) => void
  linkFilter: string
  onLinkFilterChange: (linkId: string) => void
  links: VaultSyncStatusDto[]
}

export default function SyncHistoryTable({
  history,
  isLoading,
  isFetching,
  onRefresh,
  limit,
  limitOptions,
  onLimitChange,
  linkFilter,
  onLinkFilterChange,
  links,
}: SyncHistoryTableProps) {
  const fmt = useFormatters()
  const [expanded, setExpanded] = useState<Set<string>>(new Set())

  const toggleExpand = (id: string) => {
    setExpanded((prev) => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }

  return (
    <div className="bg-card border border-border/60 rounded-xl shadow-sm overflow-hidden">
      <div className="flex items-center justify-between px-5 py-4 border-b border-border/60 bg-muted/30 gap-3 flex-wrap">
        <div className="flex items-center gap-3">
          <History size={20} className="text-muted-foreground" />
          <h2 className="text-lg font-semibold">Sync History</h2>
        </div>
        <div className="flex items-center gap-2 flex-wrap">
          <label className="flex items-center gap-1.5 text-xs text-muted-foreground">
            Vault
            <select
              value={linkFilter}
              onChange={(e) => onLinkFilterChange(e.target.value)}
              className="px-2 py-1 text-xs border border-input rounded bg-card focus:outline-none focus:ring-1 focus:ring-ring max-w-[180px]"
            >
              <option value="">All</option>
              {links.map((link) => (
                <option key={link.linkId} value={link.linkId}>
                  {link.localVaultName}
                </option>
              ))}
            </select>
          </label>
          <label className="flex items-center gap-1.5 text-xs text-muted-foreground">
            Show
            <select
              value={limit}
              onChange={(e) => onLimitChange(Number(e.target.value))}
              className="px-2 py-1 text-xs border border-input rounded bg-card focus:outline-none focus:ring-1 focus:ring-ring"
            >
              {limitOptions.map((n) => (
                <option key={n} value={n}>
                  {n}
                </option>
              ))}
            </select>
          </label>
          <button
            onClick={onRefresh}
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
      </div>

      {isLoading ? (
        <div className="p-8 space-y-3">
          {[1, 2, 3, 4].map((i) => (
            <div key={i} className="h-10 bg-muted rounded animate-pulse" />
          ))}
        </div>
      ) : history.length === 0 ? (
        <div className="p-12 text-center">
          <History size={36} className="mx-auto text-muted-foreground mb-3" />
          <p className="text-sm text-muted-foreground">No sync history yet.</p>
          <p className="text-xs text-muted-foreground mt-1">
            Knowz Cloud sync operations will be logged here.
          </p>
        </div>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-border/60 bg-muted/20">
                <th className="w-8" />
                <th className="text-left px-4 py-2.5 font-medium text-muted-foreground">Operation</th>
                <th className="text-left px-4 py-2.5 font-medium text-muted-foreground">Direction</th>
                <th className="text-left px-4 py-2.5 font-medium text-muted-foreground">Status</th>
                <th className="text-left px-4 py-2.5 font-medium text-muted-foreground">Items</th>
                <th className="text-left px-4 py-2.5 font-medium text-muted-foreground">Started</th>
                <th className="text-left px-4 py-2.5 font-medium text-muted-foreground">Duration</th>
                <th className="text-left px-4 py-2.5 font-medium text-muted-foreground">User</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-border">
              {history.map((run) => {
                const isExpanded = expanded.has(run.id)
                const hasError = !!run.errorMessage
                const duration =
                  run.completedAt && run.startedAt
                    ? Math.round(
                        (parseAsUtc(run.completedAt).getTime() -
                          parseAsUtc(run.startedAt).getTime()) /
                          1000,
                      )
                    : null
                return (
                  <Fragment key={run.id}>
                    <tr
                      className={`hover:bg-muted/20 transition-colors ${hasError ? 'cursor-pointer' : ''}`}
                      onClick={() => hasError && toggleExpand(run.id)}
                    >
                      <td className="px-2 py-2 text-center">
                        {hasError ? (
                          isExpanded ? (
                            <ChevronDown size={14} className="text-muted-foreground" />
                          ) : (
                            <ChevronRight size={14} className="text-muted-foreground" />
                          )
                        ) : null}
                      </td>
                      <td className="px-4 py-2 font-medium">{run.operation}</td>
                      <td className="px-4 py-2 text-muted-foreground">
                        {run.direction === 'None' ? '-' : run.direction}
                      </td>
                      <td className="px-4 py-2">
                        <StatusBadge status={run.status} />
                      </td>
                      <td className="px-4 py-2 text-muted-foreground">{run.itemCount}</td>
                      <td className="px-4 py-2 text-muted-foreground text-xs whitespace-nowrap">
                        {fmt.dateTime(run.startedAt)}
                      </td>
                      <td className="px-4 py-2 text-muted-foreground text-xs">
                        {duration != null ? `${duration}s` : '-'}
                      </td>
                      <td className="px-4 py-2 text-muted-foreground text-xs truncate max-w-[160px]">
                        {run.userEmail || run.userId.slice(0, 8)}
                      </td>
                    </tr>
                    {isExpanded && hasError && (
                      <tr key={`${run.id}-error`} className="bg-red-50/50 dark:bg-red-950/10">
                        <td />
                        <td colSpan={7} className="px-4 py-3">
                          <div className="flex items-start gap-2 text-sm text-red-800 dark:text-red-300">
                            <AlertCircle size={14} className="mt-0.5 shrink-0" />
                            <div className="flex-1 break-words whitespace-pre-wrap font-mono text-xs">
                              {run.errorMessage}
                            </div>
                          </div>
                        </td>
                      </tr>
                    )}
                  </Fragment>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}

function StatusBadge({ status }: { status: PlatformSyncRunStatus }) {
  const config: Record<
    PlatformSyncRunStatus,
    { classes: string; icon: React.ReactNode; label: string }
  > = {
    InProgress: {
      classes: 'bg-blue-50 dark:bg-blue-950/30 text-blue-700 dark:text-blue-400',
      icon: <Loader2 size={11} className="animate-spin" />,
      label: 'In Progress',
    },
    Succeeded: {
      classes: 'bg-green-50 dark:bg-green-950/30 text-green-700 dark:text-green-400',
      icon: <CheckCircle2 size={11} />,
      label: 'Succeeded',
    },
    Failed: {
      classes: 'bg-red-50 dark:bg-red-950/30 text-red-700 dark:text-red-400',
      icon: <XCircle size={11} />,
      label: 'Failed',
    },
    Partial: {
      classes: 'bg-amber-50 dark:bg-amber-950/30 text-amber-700 dark:text-amber-400',
      icon: <AlertCircle size={11} />,
      label: 'Partial',
    },
  }
  const c = config[status]
  return (
    <span
      className={`inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium ${c.classes}`}
    >
      {c.icon}
      {c.label}
    </span>
  )
}
