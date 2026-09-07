import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import {
  Link2,
  ArrowDownToLine,
  ArrowUpFromLine,
  RefreshCw,
  Trash2,
  Loader2,
  AlertTriangle,
  CheckCircle2,
  XCircle,
  HelpCircle,
} from 'lucide-react'
import { api, ApiError } from '../../lib/api-client'
import type { VaultSyncStatusDto } from '../../lib/types'
import { useFormatters } from '../../hooks/useFormatters'

type SyncDirection = 'Full' | 'PullOnly' | 'PushOnly'

interface VaultLinksTableProps {
  links: VaultSyncStatusDto[]
  isLoading: boolean
  onBrowsePlatform: () => void
}

export default function VaultLinksTable({
  links,
  isLoading,
  onBrowsePlatform,
}: VaultLinksTableProps) {
  const queryClient = useQueryClient()
  const fmt = useFormatters()
  const [confirmDeleteId, setConfirmDeleteId] = useState<string | null>(null)
  const [busyLinkId, setBusyLinkId] = useState<string | null>(null)
  const [busyDirection, setBusyDirection] = useState<SyncDirection | null>(null)
  const [error, setError] = useState<string | null>(null)

  const runMutation = useMutation({
    mutationFn: ({ localVaultId, direction }: { localVaultId: string; direction: SyncDirection }) =>
      api.runSyncLink(localVaultId, direction),
    onMutate: ({ localVaultId, direction }) => {
      setBusyLinkId(localVaultId)
      setBusyDirection(direction)
      setError(null)
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['platform-sync', 'links'] })
      queryClient.invalidateQueries({ queryKey: ['platform-sync', 'history'] })
    },
    onError: (err) => {
      setError(err instanceof ApiError || err instanceof Error ? err.message : 'Run sync failed')
    },
    onSettled: () => {
      setBusyLinkId(null)
      setBusyDirection(null)
    },
  })

  const deleteMutation = useMutation({
    mutationFn: (localVaultId: string) => api.removeSyncLink(localVaultId),
    onMutate: (id) => {
      setBusyLinkId(id)
      setError(null)
    },
    onSuccess: () => {
      setConfirmDeleteId(null)
      queryClient.invalidateQueries({ queryKey: ['platform-sync', 'links'] })
      queryClient.invalidateQueries({ queryKey: ['platform-sync', 'history'] })
    },
    onError: (err) => {
      setError(err instanceof ApiError || err instanceof Error ? err.message : 'Remove link failed')
    },
    onSettled: () => {
      setBusyLinkId(null)
    },
  })

  return (
    <div className="bg-card border border-border/60 rounded-xl shadow-sm overflow-hidden">
      <div className="flex items-center justify-between px-5 py-4 border-b border-border/60 bg-muted/30">
        <div className="flex items-center gap-3">
          <Link2 size={20} className="text-muted-foreground" />
          <h2 className="text-lg font-semibold">Vault Sync Links</h2>
        </div>
        <button
          onClick={onBrowsePlatform}
          className="inline-flex items-center gap-2 px-3 py-1.5 border border-input rounded-md text-sm font-medium hover:bg-muted transition-colors"
        >
          Browse Knowz Cloud
        </button>
      </div>

      {error && (
        <div className="mx-5 mt-3 px-3 py-2 rounded-md bg-red-50 dark:bg-red-950/30 text-red-800 dark:text-red-300 text-sm flex items-start gap-2">
          <AlertTriangle size={14} className="mt-0.5 shrink-0" />
          <span className="flex-1">{error}</span>
        </div>
      )}

      {isLoading ? (
        <div className="p-8 space-y-3">
          {[1, 2, 3].map((i) => (
            <div key={i} className="h-10 bg-muted rounded animate-pulse" />
          ))}
        </div>
      ) : links.length === 0 ? (
        <div className="p-12 text-center">
          <Link2 size={36} className="mx-auto text-muted-foreground mb-3" />
          <p className="text-sm text-muted-foreground">No vault sync links configured.</p>
          <p className="text-xs text-muted-foreground mt-1">
            Browse Knowz Cloud to connect a vault on this instance to a remote one.
          </p>
        </div>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-border/60 bg-muted/20">
                <th className="text-left px-4 py-2.5 font-medium text-muted-foreground">Local Vault</th>
                <th className="text-left px-4 py-2.5 font-medium text-muted-foreground">Knowz Cloud Vault</th>
                <th className="text-left px-4 py-2.5 font-medium text-muted-foreground">Status</th>
                <th className="text-left px-4 py-2.5 font-medium text-muted-foreground">Last Sync</th>
                <th className="text-right px-4 py-2.5 font-medium text-muted-foreground">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-border">
              {links.map((link) => {
                const isBusy = busyLinkId === link.localVaultId
                return (
                  <tr key={link.linkId} className="hover:bg-muted/20 transition-colors">
                    <td className="px-4 py-2.5 font-medium">{link.localVaultName}</td>
                    <td className="px-4 py-2.5 font-mono text-xs text-muted-foreground">
                      {link.remoteVaultId.slice(0, 8)}…
                    </td>
                    <td className="px-4 py-2.5">
                      <StatusBadge status={link.status} enabled={link.syncEnabled} />
                      {link.lastSyncError && (
                        <div
                          className="text-xs text-red-600 dark:text-red-400 mt-1 max-w-[220px] truncate"
                          title={link.lastSyncError}
                        >
                          {link.lastSyncError}
                        </div>
                      )}
                    </td>
                    <td className="px-4 py-2.5 text-muted-foreground text-xs">
                      {link.lastSyncCompletedAt
                        ? fmt.dateTime(link.lastSyncCompletedAt)
                        : 'Never'}
                    </td>
                    <td className="px-4 py-2.5">
                      <div className="flex items-center justify-end gap-1.5">
                        <div className="inline-flex items-stretch rounded border border-input overflow-hidden">
                          <button
                            onClick={() =>
                              runMutation.mutate({
                                localVaultId: link.localVaultId,
                                direction: 'PullOnly',
                              })
                            }
                            disabled={isBusy}
                            className="inline-flex items-center gap-1 px-2 py-1 text-xs font-medium hover:bg-muted disabled:opacity-50 border-r border-input"
                            title="Pull from platform (one-way: remote → local)"
                          >
                            {isBusy && busyDirection === 'PullOnly' ? (
                              <Loader2 size={12} className="animate-spin" />
                            ) : (
                              <ArrowDownToLine size={12} />
                            )}
                            Pull
                          </button>
                          <button
                            onClick={() =>
                              runMutation.mutate({
                                localVaultId: link.localVaultId,
                                direction: 'PushOnly',
                              })
                            }
                            disabled={isBusy}
                            className="inline-flex items-center gap-1 px-2 py-1 text-xs font-medium hover:bg-muted disabled:opacity-50 border-r border-input"
                            title="Push to platform (one-way: local → remote)"
                          >
                            {isBusy && busyDirection === 'PushOnly' ? (
                              <Loader2 size={12} className="animate-spin" />
                            ) : (
                              <ArrowUpFromLine size={12} />
                            )}
                            Push
                          </button>
                          <button
                            onClick={() =>
                              runMutation.mutate({
                                localVaultId: link.localVaultId,
                                direction: 'Full',
                              })
                            }
                            disabled={isBusy}
                            className="inline-flex items-center gap-1 px-2 py-1 text-xs font-medium hover:bg-muted disabled:opacity-50"
                            title="Full bidirectional sync"
                          >
                            {isBusy && busyDirection === 'Full' ? (
                              <Loader2 size={12} className="animate-spin" />
                            ) : (
                              <RefreshCw size={12} />
                            )}
                            Full
                          </button>
                        </div>
                        <button
                          onClick={() => setConfirmDeleteId(link.localVaultId)}
                          disabled={isBusy}
                          className="inline-flex items-center gap-1 px-2 py-1 border border-red-200 dark:border-red-900 text-red-600 dark:text-red-400 rounded text-xs font-medium hover:bg-red-50 dark:hover:bg-red-950/30 disabled:opacity-50"
                          title="Remove link"
                        >
                          <Trash2 size={12} />
                        </button>
                      </div>
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}

      {confirmDeleteId && (
        <DeleteLinkConfirmModal
          isPending={deleteMutation.isPending}
          onCancel={() => setConfirmDeleteId(null)}
          onConfirm={() => deleteMutation.mutate(confirmDeleteId)}
        />
      )}
    </div>
  )
}

function StatusBadge({ status, enabled }: { status: string; enabled: boolean }) {
  const lower = (status || '').toLowerCase()
  let icon: React.ReactNode = <HelpCircle size={11} />
  let classes = 'bg-muted text-muted-foreground'
  if (!enabled) {
    classes = 'bg-muted text-muted-foreground'
    icon = <HelpCircle size={11} />
  } else if (lower.includes('ok') || lower.includes('success') || lower.includes('healthy')) {
    classes = 'bg-green-50 dark:bg-green-950/30 text-green-700 dark:text-green-400'
    icon = <CheckCircle2 size={11} />
  } else if (lower.includes('fail') || lower.includes('error')) {
    classes = 'bg-red-50 dark:bg-red-950/30 text-red-700 dark:text-red-400'
    icon = <XCircle size={11} />
  } else if (lower.includes('progress') || lower.includes('running')) {
    classes = 'bg-blue-50 dark:bg-blue-950/30 text-blue-700 dark:text-blue-400'
    icon = <Loader2 size={11} className="animate-spin" />
  }
  return (
    <span
      className={`inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium ${classes}`}
    >
      {icon}
      {status || (enabled ? 'Ready' : 'Disabled')}
    </span>
  )
}

function DeleteLinkConfirmModal({
  isPending,
  onCancel,
  onConfirm,
}: {
  isPending: boolean
  onCancel: () => void
  onConfirm: () => void
}) {
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
      <div className="fixed inset-0 bg-black/50" onClick={onCancel} />
      <div className="relative bg-card border border-border/60 rounded-xl shadow-xl w-full max-w-md p-6">
        <div className="flex items-center gap-3 mb-4">
          <div className="flex items-center justify-center w-10 h-10 rounded-full bg-red-50 dark:bg-red-950/40">
            <AlertTriangle size={20} className="text-red-600 dark:text-red-400" />
          </div>
          <h3 className="text-lg font-semibold">Remove Sync Link?</h3>
        </div>
        <p className="text-sm text-muted-foreground mb-6">
          The vault on this instance will no longer sync with the Knowz Cloud vault. Local data is not
          deleted, but future automatic pulls will stop.
        </p>
        <div className="flex justify-end gap-3">
          <button
            onClick={onCancel}
            disabled={isPending}
            className="px-4 py-2 border border-input rounded-md text-sm font-medium hover:bg-muted disabled:opacity-50"
          >
            Cancel
          </button>
          <button
            onClick={onConfirm}
            disabled={isPending}
            className="inline-flex items-center gap-2 px-4 py-2 bg-red-600 hover:bg-red-700 text-white rounded-md text-sm font-medium disabled:opacity-50"
          >
            {isPending && <Loader2 size={14} className="animate-spin" />}
            Remove Link
          </button>
        </div>
      </div>
    </div>
  )
}
