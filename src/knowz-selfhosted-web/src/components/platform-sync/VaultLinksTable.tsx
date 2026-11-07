import { Fragment, useContext, useState } from 'react'
import { Link } from 'react-router-dom'
import { AuthContext } from '../../lib/auth'
import { SavedArchivePeerPanel } from '../portability/SavedArchivePeerPanel'
import type { ArchivePeerTransport } from '../../lib/archive-peer-api'
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
  X,
} from 'lucide-react'
import { api, ApiError } from '../../lib/api-client'
import type {
  PlatformConnectionDto,
  SyncDirection,
  VaultSyncResult,
  VaultSyncStatusDto,
} from '../../lib/types'
import { isSafeDetail, toUserError } from '../../lib/toUserError'
import { useFormatters } from '../../hooks/useFormatters'
import { describeLinkDestination, hasPeerConnections } from '../../lib/destinations'

/**
 * UI_DestinationsLiveConnect S5 — the outcome of the most recent manual run.
 * `result` carries a server-reported run (including a capped/partial one);
 * `error` carries a run that never produced a body.
 */
type RunBanner =
  | { kind: 'result'; result: VaultSyncResult }
  | { kind: 'error'; tone: 'red' | 'amber'; message: string }

/** S6 — the server-side limits, stated before the operator clicks. */
const ITEMS_PER_RUN = 100
const RUNS_PER_HOUR = 10

/**
 * The orchestrator's catch-all assigns `ex.Message` to `error`, so a server sentence
 * is only shown when it is already product-shaped.
 */
const RUN_FAILED = 'The run did not complete.'

/** HTTP statuses the run endpoint uses to mean something specific. */
const RUN_REFUSED = 422
const RATE_LIMITED = 429
const archiveTransport: ArchivePeerTransport = { review: (id, signal) => api.getSavedPeer(id, signal), register: (id, input, signal) => api.registerSavedPeer(id, input, signal), transfer: (id, direction, binding, signal) => api.transferSavedPeer(id, direction, binding, signal) }

interface VaultLinksTableProps {
  links: VaultSyncStatusDto[]
  isLoading: boolean
  onBrowsePlatform: () => void
  /**
   * UI_SH_PeerDestination (ADR_G6, #1043) — the stored connections, used to name each
   * link's destination. Omitted → every link is labelled from its own URL.
   */
  connections?: PlatformConnectionDto[]
  /** Opens the "Link a vault" flow (`POST /api/v1/sync/links` with `platformConnectionId`). */
  onLinkVault?: () => void
}

export default function VaultLinksTable({
  links,
  isLoading,
  onBrowsePlatform,
  connections = [],
  onLinkVault,
}: VaultLinksTableProps) {
  const queryClient = useQueryClient()
  const auth = useContext(AuthContext)
  const scopeKey = `${auth?.activeTenantId ?? auth?.user?.tenantId}:${auth?.user?.id}:${localStorage.getItem('apiUrl') ?? ''}`
  const fmt = useFormatters()
  const [confirmDeleteId, setConfirmDeleteId] = useState<string | null>(null)
  const [busyLinkId, setBusyLinkId] = useState<string | null>(null)
  const [busyDirection, setBusyDirection] = useState<SyncDirection | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [runBanner, setRunBanner] = useState<RunBanner | null>(null)

  const runMutation = useMutation({
    mutationFn: ({ localVaultId, direction }: { localVaultId: string; direction: SyncDirection }) =>
      api.runSyncLink(localVaultId, direction),
    onMutate: ({ localVaultId, direction }) => {
      setBusyLinkId(localVaultId)
      setBusyDirection(direction)
      setError(null)
      setRunBanner(null)
    },
    onSuccess: (result) => {
      // The server answers 200 with the same shape on a refused run, so trust
      // `success` rather than the status code alone.
      setRunBanner(
        result.success
          ? { kind: 'result', result }
          : { kind: 'error', tone: 'red', message: safeServerSentence(result.error) },
      )
      queryClient.invalidateQueries({ queryKey: ['platform-sync', 'links'] })
      queryClient.invalidateQueries({ queryKey: ['platform-sync', 'history'] })
    },
    onError: (err) => {
      setRunBanner({ kind: 'error', ...describeRunFailure(err) })
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
      setRunBanner(null)
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
        <div className="flex items-center gap-2">
          {onLinkVault && (
            <button
              data-testid="link-vault-button"
              onClick={onLinkVault}
              className="inline-flex items-center gap-2 px-3 py-1.5 bg-primary text-primary-foreground rounded-md text-sm font-medium hover:opacity-90 transition-opacity"
            >
              <Link2 size={14} />
              Link a vault
            </button>
          )}
          <button
            onClick={onBrowsePlatform}
            className="inline-flex items-center gap-2 px-3 py-1.5 border border-input rounded-md text-sm font-medium hover:bg-muted transition-colors"
          >
            {/* With zero peer configuration the only destination IS Knowz Cloud, so say so. */}
            {hasPeerConnections(connections) ? 'Browse a destination' : 'Browse Knowz Cloud'}
          </button>
        </div>
      </div>

      <p data-testid="sync-caps-helper" className="px-5 pt-3 text-xs text-muted-foreground">
        Manual runs only · up to {ITEMS_PER_RUN} items per run · {RUNS_PER_HOUR} runs per
        hour on this instance for legacy inline sync. Complete archive controls below preserve protected graphs and use their declared archive limits.
      </p>

      <p data-testid="archive-local-key-helper" className="px-5 pt-2 text-xs text-muted-foreground">
        Complete transfers require a personal API key for your local account. Create one in{' '}
        <Link to="/settings?tab=api-keys" className="text-primary underline">Settings → API keys</Link>.
        If you replace that key, review and register each link again. Original vault encryption keys stay on your device.
      </p>

      {runBanner && (
        <RunResultBanner banner={runBanner} onDismiss={() => setRunBanner(null)} />
      )}

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
            Link a vault on this instance to a vault on a destination, then run Pull, Push, or
            Full when you choose.
          </p>
        </div>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-border/60 bg-muted/20">
                <th className="text-left px-4 py-2.5 font-medium text-muted-foreground">Local Vault</th>
                <th className="text-left px-4 py-2.5 font-medium text-muted-foreground">Destination Vault</th>
                <th className="text-left px-4 py-2.5 font-medium text-muted-foreground">Status</th>
                <th className="text-left px-4 py-2.5 font-medium text-muted-foreground">Last Sync</th>
                <th className="text-right px-4 py-2.5 font-medium text-muted-foreground">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-border">
              {links.map((link) => {
                const isBusy = busyLinkId === link.localVaultId
                // The server allows one run per tenant, so every row's run actions
                // are unavailable while any run is in flight — only the spinner is
                // per-row. Without this, a second click just earns a 429.
                const runsLocked = runMutation.isPending
                const destination = describeLinkDestination(link, connections)
                return (
                  <Fragment key={link.linkId}><tr className="hover:bg-muted/20 transition-colors">
                    <td className="px-4 py-2.5 font-medium">{link.localVaultName}</td>
                    <td className="px-4 py-2.5">
                      <div className="font-mono text-xs text-muted-foreground">
                        {link.remoteVaultId.slice(0, 8)}…
                      </div>
                      <div
                        data-testid="link-destination"
                        className="text-xs text-foreground/80 mt-0.5"
                        title={link.platformApiUrl}
                      >
                        {destination.label}
                      </div>
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
                        <details open={link.platformConnectionId ? undefined : true}><summary hidden={!link.platformConnectionId}>Legacy plain inline sync</summary>
                        <div className="inline-flex items-stretch rounded border border-input overflow-hidden">
                          <button
                            onClick={() =>
                              runMutation.mutate({
                                localVaultId: link.localVaultId,
                                direction: 'PullOnly',
                              })
                            }
                            disabled={isBusy || runsLocked}
                            className="inline-flex items-center gap-1 px-2 py-1 text-xs font-medium hover:bg-muted disabled:opacity-50 border-r border-input"
                            title={`Pull: ${destination.label} → this instance`}
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
                            disabled={isBusy || runsLocked}
                            className="inline-flex items-center gap-1 px-2 py-1 text-xs font-medium hover:bg-muted disabled:opacity-50 border-r border-input"
                            title={`Push: this instance → ${destination.label}`}
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
                            disabled={isBusy || runsLocked}
                            className="inline-flex items-center gap-1 px-2 py-1 text-xs font-medium hover:bg-muted disabled:opacity-50"
                            title="Full: pull then push"
                          >
                            {isBusy && busyDirection === 'Full' ? (
                              <Loader2 size={12} className="animate-spin" />
                            ) : (
                              <RefreshCw size={12} />
                            )}
                            Full
                          </button>
                        </div>
                        </details>
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
                  {link.platformConnectionId && <tr><td colSpan={5} className="p-3"><SavedArchivePeerPanel scopeKey={scopeKey} localSourceBaseUrl={localStorage.getItem('apiUrl') || window.location.origin} transport={archiveTransport}
                    link={{ id: link.linkId, localVaultId: link.localVaultId, remoteVaultId: link.remoteVaultId, destinationBaseUrl: link.platformApiUrl, destinationLabel: destination.label, enabled: link.syncEnabled, directions: ['pull', 'push'] }}
                    onPreserved={() => { void queryClient.invalidateQueries({ queryKey: ['platform-sync', 'links'] }); }} /></td></tr>}
                  </Fragment>
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
        <p data-testid="remove-link-body" className="text-sm text-muted-foreground mb-6">
          The vault on this instance will no longer sync with its destination vault. Local
          data is not deleted. You can link the vault again later.
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

/**
 * S5 — one honest sentence of counts, with the half that does not apply to the
 * chosen direction omitted.
 */
function formatRunCounts(result: VaultSyncResult): string {
  const parts: string[] = []
  if (result.direction !== 'PushOnly') {
    parts.push(`Pull: ${result.pullAccepted} accepted, ${result.pullSkipped} skipped`)
  }
  if (result.direction !== 'PullOnly') {
    parts.push(`Push: ${result.pushAccepted} accepted, ${result.pushSkipped} skipped`)
  }
  return parts.join(' · ')
}

/**
 * S5 — the two failure shapes the run endpoint can produce carry copy the generic
 * error mapper cannot express: 422 returns a server sentence worth showing, and
 * 429 names one of the limiter's reasons (see `describeRateLimit`), not a generic throttle.
 */
function describeRunFailure(err: unknown): { tone: 'red' | 'amber'; message: string } {
  if (err instanceof ApiError) {
    if (err.status === RATE_LIMITED) {
      return { tone: 'amber', message: describeRateLimit(err.message) }
    }
    if (err.status === RUN_REFUSED) {
      return { tone: 'red', message: safeServerSentence(err.message) }
    }
  }
  return { tone: 'red', message: toUserError(err) }
}

/**
 * A 429 has three distinct causes and the `reason` field never reaches the client, so
 * the server sentence is the only discriminator. Naming the hourly budget when the real
 * cause was a concurrent run would be untrue at the exact moment the operator reads it.
 */
function describeRateLimit(message: string): string {
  if (/already in progress/i.test(message)) {
    return 'Another sync is already running on this instance. Wait for it to finish and try again.'
  }
  if (/per hour/i.test(message)) {
    return `This instance allows ${RUNS_PER_HOUR} sync runs per hour. Wait a moment and try again.`
  }
  // Any other 429 sentence is product-shaped today; a body-less proxy 429 would surface
  // the client's own `Request failed…` text, so filter before echoing.
  return message && isSafeDetail(message) && !/^Request failed/i.test(message)
    ? message
    : 'This instance refused the run for now. Wait a moment and try again.'
}

/** Server text reaches the operator only when it is not machine internals. */
function safeServerSentence(message: string | null): string {
  return message && isSafeDetail(message) ? message : RUN_FAILED
}

const BANNER_TONES: Record<'green' | 'amber' | 'red', string> = {
  green:
    'bg-green-50 dark:bg-green-950/30 text-green-800 dark:text-green-300 border-green-200 dark:border-green-900',
  amber:
    'bg-amber-50 dark:bg-amber-950/30 text-amber-800 dark:text-amber-300 border-amber-200 dark:border-amber-900',
  red: 'bg-red-50 dark:bg-red-950/30 text-red-800 dark:text-red-300 border-red-200 dark:border-red-900',
}

/**
 * G7 (#1044) — the orchestrator reports every lane it did not run as a detail line that
 * starts with one of these phrases (`VaultSyncOrchestrator.PushSkippedDetail`,
 * `FilesSkippedPartialDetail`, `FilesNotSyncedDirectionDetail`). Those lines answer "why did
 * my Full run not push / not move files?", so they are promoted out of the collapsed details
 * list and shown in the banner body where the operator cannot miss them.
 */
const SKIP_REASON = /^(Push skipped|Files skipped|Files not synced)\b/

function splitSkipReasons(details: string[]): { skipReasons: string[]; rest: string[] } {
  const skipReasons: string[] = []
  const rest: string[] = []
  for (const detail of details) {
    if (SKIP_REASON.test(detail)) skipReasons.push(detail)
    else rest.push(detail)
  }
  return { skipReasons, rest }
}

function RunResultBanner({
  banner,
  onDismiss,
}: {
  banner: RunBanner
  onDismiss: () => void
}) {
  const isPartial = banner.kind === 'result' && banner.result.partial
  const { skipReasons, rest: otherDetails } =
    banner.kind === 'result' ? splitSkipReasons(banner.result.details) : { skipReasons: [], rest: [] }
  const tone: 'green' | 'amber' | 'red' =
    banner.kind === 'error' ? banner.tone : isPartial ? 'amber' : 'green'
  const Icon = tone === 'green' ? CheckCircle2 : tone === 'amber' ? AlertTriangle : XCircle

  return (
    <div
      data-testid="sync-run-banner"
      role={tone === 'red' ? 'alert' : 'status'}
      className={`mx-5 mt-3 px-3 py-2 rounded-md border text-sm flex items-start gap-2 ${BANNER_TONES[tone]}`}
    >
      <Icon size={14} className="mt-0.5 shrink-0" />
      <div className="flex-1 space-y-1">
        {banner.kind === 'error' ? (
          <p>{banner.message}</p>
        ) : (
          <>
            {isPartial && (
              <p className="font-medium">
                Stopped at the {ITEMS_PER_RUN}-item limit. Run again to continue from where
                it left off.
              </p>
            )}
            <p>{formatRunCounts(banner.result)}</p>
            {skipReasons.length > 0 && (
              <ul
                data-testid="sync-run-skip-reasons"
                className="text-xs space-y-0.5 list-disc list-inside"
              >
                {skipReasons.map((reason, i) => (
                  <li key={`${i}-${reason}`}>{reason}</li>
                ))}
              </ul>
            )}
            {otherDetails.length > 0 && (
              <details className="text-xs">
                <summary className="cursor-pointer">Details ({otherDetails.length})</summary>
                <ul className="mt-1 space-y-0.5 list-disc list-inside">
                  {otherDetails.map((detail, i) => (
                    <li key={`${i}-${detail}`}>{detail}</li>
                  ))}
                </ul>
              </details>
            )}
          </>
        )}
      </div>
      <button
        onClick={onDismiss}
        className="p-0.5 opacity-70 hover:opacity-100"
        aria-label="Dismiss"
      >
        <X size={14} />
      </button>
    </div>
  )
}
