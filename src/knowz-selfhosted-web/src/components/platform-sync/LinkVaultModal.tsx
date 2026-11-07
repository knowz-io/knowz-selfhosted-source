import { useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, Link2, Loader2, X } from 'lucide-react'
import { api, ApiError } from '../../lib/api-client'
import { isSafeDetail, toUserError } from '../../lib/toUserError'
import { describeDestination, primaryConnection } from '../../lib/destinations'
import type { PlatformConnectionDto, VaultSyncStatusDto } from '../../lib/types'

/**
 * UI_SH_PeerDestination (ADR_G6, #1043) — the minimal "Link a vault" flow.
 *
 * Establishes a vault sync link with `POST /api/v1/sync/links`, binding it to the chosen
 * stored connection via `platformConnectionId` (default: the primary). The remote vault
 * list is browsed through `?connectionId=` so the picker and the list always agree.
 * Linking moves nothing — every run remains a manual Pull / Push / Full from the table.
 */
interface LinkVaultModalProps {
  connections: PlatformConnectionDto[]
  links: VaultSyncStatusDto[]
  onClose: () => void
  /** Preselect a destination (falls back to the primary). */
  initialConnectionId?: string
}

const LINK_FAILED = 'The link could not be created.'

/**
 * `POST /links` answers 400 with the orchestrator's own sentence (e.g. "Vault … already has a
 * sync link", or the chosen destination no longer exists); the generic status mapper would
 * hide it.
 */
function describeLinkFailure(err: unknown): string {
  if (
    err instanceof ApiError &&
    err.status === 400 &&
    isSafeDetail(err.message) &&
    !/^Request failed/i.test(err.message)
  ) {
    return err.message
  }
  return toUserError(err, LINK_FAILED)
}

export default function LinkVaultModal({
  connections,
  links,
  onClose,
  initialConnectionId,
}: LinkVaultModalProps) {
  const queryClient = useQueryClient()
  const primary = primaryConnection(connections)
  const [connectionId, setConnectionId] = useState<string>(
    initialConnectionId ?? primary?.id ?? '',
  )
  const [localVaultId, setLocalVaultId] = useState('')
  const [remoteVaultId, setRemoteVaultId] = useState('')
  const [error, setError] = useState<string | null>(null)

  const selected =
    connections.find((c) => c.id === connectionId) ?? primary ?? null
  const destination = describeDestination(selected)
  const hasDestination = !!selected
  // Older backends may omit `id`; then the server's own primary resolution applies.
  const effectiveConnectionId = selected?.id ?? undefined

  const localVaultsQuery = useQuery({
    queryKey: ['vaults', 'list-for-link'],
    queryFn: async () => (await api.listVaults(false)).vaults,
  })

  const remoteVaultsQuery = useQuery({
    queryKey: ['platform-sync', 'browse', 'vaults', effectiveConnectionId ?? 'primary'],
    queryFn: async () =>
      (await api.listPlatformVaults(effectiveConnectionId)) ?? { vaults: [], totalCount: 0 },
    enabled: hasDestination,
  })

  const linkedLocalIds = useMemo(() => new Set(links.map((l) => l.localVaultId)), [links])
  const localVaults = (localVaultsQuery.data ?? []).filter((v) => !linkedLocalIds.has(v.id))
  const remoteVaults = remoteVaultsQuery.data?.vaults ?? []

  const establishMutation = useMutation({
    mutationFn: () =>
      api.establishSyncLink({
        localVaultId,
        remoteVaultId,
        platformConnectionId: effectiveConnectionId,
      }),
    onMutate: () => setError(null),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['platform-sync', 'links'] })
      queryClient.invalidateQueries({ queryKey: ['platform-sync', 'history'] })
      onClose()
    },
    onError: (err) => setError(describeLinkFailure(err)),
  })

  const submitDisabled =
    establishMutation.isPending || !hasDestination || !localVaultId || !remoteVaultId

  const handleDestinationChange = (nextId: string) => {
    setConnectionId(nextId)
    setRemoteVaultId('')
    setError(null)
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
      <div className="fixed inset-0 bg-black/50" onClick={onClose} />
      <div
        role="dialog"
        aria-labelledby="link-vault-title"
        className="relative bg-card border border-border/60 rounded-xl shadow-xl w-full max-w-lg p-6 space-y-4"
      >
        <div className="flex items-center gap-3">
          <Link2 size={20} className="text-muted-foreground" />
          <h3 id="link-vault-title" className="text-lg font-semibold">
            Link a vault
          </h3>
          <button
            onClick={onClose}
            disabled={establishMutation.isPending}
            className="ml-auto p-1.5 rounded hover:bg-muted text-muted-foreground disabled:opacity-50"
            aria-label="Close"
          >
            <X size={18} />
          </button>
        </div>

        <p data-testid="link-vault-honesty" className="text-xs text-muted-foreground">
          Linking pairs a vault on this instance with one on a destination. It moves nothing:
          run Pull, Push, or Full from the table when you choose.
        </p>

        {error && (
          <div
            role="alert"
            className="px-3 py-2 rounded-md bg-red-50 dark:bg-red-950/30 text-red-800 dark:text-red-300 text-sm flex items-start gap-2"
          >
            <AlertTriangle size={14} className="mt-0.5 shrink-0" />
            <span className="flex-1">{error}</span>
          </div>
        )}

        {!hasDestination ? (
          <p data-testid="link-vault-no-destination" className="text-sm text-muted-foreground">
            Connect a destination first, then come back to link a vault.
          </p>
        ) : (
          <div className="space-y-4">
            <div>
              <label htmlFor="link-vault-destination" className="block text-sm font-medium mb-1">
                Destination
              </label>
              <select
                id="link-vault-destination"
                aria-label="Destination"
                value={selected?.id ?? ''}
                onChange={(e) => handleDestinationChange(e.target.value)}
                disabled={establishMutation.isPending || connections.length <= 1}
                className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm focus:outline-none focus:ring-2 focus:ring-ring disabled:opacity-70"
              >
                {connections.map((c, index) => {
                  const d = describeDestination(c)
                  return (
                    <option key={c.id ?? `${index}:${c.platformApiUrl}`} value={c.id ?? ''}>
                      {d.label}
                      {d.label !== d.kindLabel ? ` · ${d.kindLabel}` : ''}
                      {index === 0 ? ' · Primary' : ''}
                    </option>
                  )
                })}
              </select>
            </div>

            <div>
              <label htmlFor="link-vault-local" className="block text-sm font-medium mb-1">
                Vault on this instance
              </label>
              <select
                id="link-vault-local"
                aria-label="Vault on this instance"
                value={localVaultId}
                onChange={(e) => setLocalVaultId(e.target.value)}
                disabled={establishMutation.isPending || localVaultsQuery.isLoading}
                className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm focus:outline-none focus:ring-2 focus:ring-ring"
              >
                <option value="">
                  {localVaultsQuery.isLoading ? 'Loading vaults…' : 'Choose a vault'}
                </option>
                {localVaults.map((v) => (
                  <option key={v.id} value={v.id}>
                    {v.name}
                  </option>
                ))}
              </select>
              {localVaultsQuery.error && (
                <p className="mt-1 text-xs text-red-600 dark:text-red-400">
                  {toUserError(localVaultsQuery.error, 'Could not load the vaults on this instance.')}
                </p>
              )}
              {!localVaultsQuery.isLoading && !localVaultsQuery.error && localVaults.length === 0 && (
                <p className="mt-1 text-xs text-muted-foreground">
                  Every vault on this instance is already linked.
                </p>
              )}
            </div>

            <div>
              <label htmlFor="link-vault-remote" className="block text-sm font-medium mb-1">
                Vault on {destination.label}
              </label>
              <select
                id="link-vault-remote"
                aria-label={`Vault on ${destination.label}`}
                value={remoteVaultId}
                onChange={(e) => setRemoteVaultId(e.target.value)}
                disabled={establishMutation.isPending || remoteVaultsQuery.isLoading}
                className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm focus:outline-none focus:ring-2 focus:ring-ring"
              >
                <option value="">
                  {remoteVaultsQuery.isLoading ? 'Loading vaults…' : 'Choose a vault'}
                </option>
                {remoteVaults.map((v) => (
                  <option key={v.id} value={v.id}>
                    {v.name}
                  </option>
                ))}
              </select>
              {remoteVaultsQuery.error && (
                <p className="mt-1 text-xs text-red-600 dark:text-red-400">
                  {toUserError(
                    remoteVaultsQuery.error,
                    `Could not list the vaults on ${destination.label}.`,
                  )}
                </p>
              )}
            </div>
          </div>
        )}

        <div className="flex justify-end gap-3 pt-2">
          <button
            onClick={onClose}
            disabled={establishMutation.isPending}
            className="px-4 py-2 border border-input rounded-md text-sm font-medium hover:bg-muted disabled:opacity-50"
          >
            Cancel
          </button>
          <button
            onClick={() => establishMutation.mutate()}
            disabled={submitDisabled}
            className="inline-flex items-center gap-2 px-4 py-2 bg-primary text-primary-foreground rounded-md text-sm font-medium hover:opacity-90 disabled:opacity-50 disabled:cursor-not-allowed"
          >
            {establishMutation.isPending ? (
              <Loader2 size={14} className="animate-spin" />
            ) : (
              <Link2 size={14} />
            )}
            Link vault
          </button>
        </div>
      </div>
    </div>
  )
}
