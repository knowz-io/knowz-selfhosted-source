import { useEffect, useRef, useState } from 'react'
import { toUserError } from '../../lib/toUserError'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import {
  Cloud,
  CheckCircle2,
  XCircle,
  AlertTriangle,
  Loader2,
  Plug,
  PlugZap,
  Save,
  X,
  Edit3,
  Shield,
} from 'lucide-react'
import { api, ApiError } from '../../lib/api-client'
import { useFormatters } from '../../hooks/useFormatters'
import type {
  PlatformConnectionDto,
  PlatformConnectionTestResult,
  PlatformConnectionTestStatus,
} from '../../lib/types'

interface ConnectionCardProps {
  connection: PlatformConnectionDto | null
  linkCount: number
}

type BannerKind = 'success' | 'error' | 'warning' | 'info'

interface Banner {
  kind: BannerKind
  message: string
}

export default function ConnectionCard({ connection, linkCount }: ConnectionCardProps) {
  const queryClient = useQueryClient()
  const fmt = useFormatters()
  const isConnected = !!connection && connection.hasApiKey
  const [isEditing, setIsEditing] = useState(!isConnected)
  const [url, setUrl] = useState(connection?.platformApiUrl ?? '')
  const [displayName, setDisplayName] = useState(connection?.displayName ?? '')
  const [apiKey, setApiKey] = useState('')
  const [banner, setBanner] = useState<Banner | null>(null)
  const [candidateTestResult, setCandidateTestResult] = useState<PlatformConnectionTestResult | null>(null)
  const [showDisconnectConfirm, setShowDisconnectConfirm] = useState(false)

  // When the parent's connection query resolves asynchronously (null → connected),
  // useState's initializer has already locked `isEditing` to `true`. Sync it to the
  // connected view and seed the display fields from the newly-arrived prop, but only
  // on the initial async resolution — subsequent connection prop changes (e.g. after
  // a Save) should not yank the user out of whatever mode they are in.
  const hasSyncedInitialConnection = useRef(false)
  useEffect(() => {
    if (hasSyncedInitialConnection.current) return
    if (isConnected && connection) {
      hasSyncedInitialConnection.current = true
      setIsEditing(false)
      setUrl(connection.platformApiUrl ?? '')
      setDisplayName(connection.displayName ?? '')
    }
  }, [isConnected, connection])

  const resetForm = () => {
    setUrl(connection?.platformApiUrl ?? '')
    setDisplayName(connection?.displayName ?? '')
    setApiKey('')
    setCandidateTestResult(null)
    setBanner(null)
  }

  const handleError = (err: unknown): string => {
    if (err instanceof ApiError) return toUserError(err)
    if (err instanceof Error) return toUserError(err)
    return 'Request failed'
  }

  const testCandidateMutation = useMutation({
    mutationFn: () => api.testPlatformConnectionCandidate(url.trim(), apiKey),
    onSuccess: (result) => {
      setCandidateTestResult(result)
      if (result.status === 'Ok') {
        setBanner({ kind: 'success', message: result.message || 'Connection test succeeded.' })
      } else {
        setBanner({ kind: 'error', message: result.message || `Test failed: ${result.status}` })
      }
    },
    onError: (err) => {
      setCandidateTestResult(null)
      setBanner({ kind: 'error', message: handleError(err) })
    },
  })

  const saveMutation = useMutation({
    mutationFn: () =>
      api.upsertPlatformConnection({
        platformApiUrl: url.trim(),
        displayName: displayName.trim() || null,
        apiKey: apiKey || null,
      }),
    onSuccess: () => {
      // V-SEC-04: clear plaintext API key immediately after successful save.
      setApiKey('')
      setCandidateTestResult(null)
      setBanner({ kind: 'success', message: 'Connection saved.' })
      setIsEditing(false)
      queryClient.invalidateQueries({ queryKey: ['platform-sync', 'connection'] })
      queryClient.invalidateQueries({ queryKey: ['platform-sync', 'history'] })
    },
    onError: (err) => {
      setBanner({ kind: 'error', message: handleError(err) })
    },
  })

  const testExistingMutation = useMutation({
    mutationFn: () => api.testPlatformConnection(),
    onSuccess: (result) => {
      if (result.status === 'Ok') {
        setBanner({ kind: 'success', message: result.message || 'Connection is healthy.' })
      } else {
        setBanner({ kind: 'error', message: result.message || `Test failed: ${result.status}` })
      }
      queryClient.invalidateQueries({ queryKey: ['platform-sync', 'connection'] })
      queryClient.invalidateQueries({ queryKey: ['platform-sync', 'history'] })
    },
    onError: (err) => {
      setBanner({ kind: 'error', message: handleError(err) })
    },
  })

  const disconnectMutation = useMutation({
    mutationFn: () => api.deletePlatformConnection(),
    onSuccess: () => {
      setBanner({ kind: 'success', message: 'Knowz Cloud connection removed.' })
      setShowDisconnectConfirm(false)
      setIsEditing(true)
      setUrl('')
      setDisplayName('')
      setApiKey('')
      queryClient.invalidateQueries({ queryKey: ['platform-sync', 'connection'] })
      queryClient.invalidateQueries({ queryKey: ['platform-sync', 'history'] })
    },
    onError: (err) => {
      const message = handleError(err)
      if (err instanceof ApiError && err.status === 409) {
        setBanner({
          kind: 'warning',
          message: message || 'Remove all sync links first before disconnecting.',
        })
      } else {
        setBanner({ kind: 'error', message })
      }
    },
  })

  const testDisabled =
    testCandidateMutation.isPending ||
    saveMutation.isPending ||
    !url.trim() ||
    !apiKey.trim()

  const saveDisabled =
    saveMutation.isPending ||
    testCandidateMutation.isPending ||
    !url.trim() ||
    (!isConnected && !apiKey.trim())

  return (
    <div data-testid="platform-connection-card" className="bg-card border border-border/60 rounded-xl shadow-sm overflow-hidden">
      <div className="flex items-center gap-3 px-5 py-4 border-b border-border/60 bg-muted/30">
        <Cloud size={20} className="text-muted-foreground" />
        <h2 className="text-lg font-semibold">Connection</h2>
        <div className="ml-auto">
          <ConnectionStatusPill status={connection?.lastTestStatus ?? 'Untested'} connected={isConnected} />
        </div>
      </div>

      <div className="p-5 space-y-4">
        {banner && <BannerBox banner={banner} onDismiss={() => setBanner(null)} />}

        {!isEditing && connection && (
          <div className="space-y-3">
            <FieldRow label="Knowz Cloud URL" value={connection.platformApiUrl} mono />
            {connection.displayName && <FieldRow label="Display Name" value={connection.displayName} />}
            <FieldRow
              label="API Key"
              value={connection.apiKeyMask ?? 'No API key set'}
              mono
            />
            {connection.remoteTenantId && (
              <FieldRow
                label="Remote Tenant"
                value={`${connection.remoteTenantId.slice(0, 8)}…`}
                mono
              />
            )}
            {connection.lastTestedAt && (
              <FieldRow
                label="Last Tested"
                value={fmt.dateTime(connection.lastTestedAt)}
              />
            )}
            {connection.lastTestError && (
              <FieldRow label="Last Test Error" value={connection.lastTestError} />
            )}

            <div className="flex flex-wrap items-center gap-2 pt-2">
              <button
                onClick={() => testExistingMutation.mutate()}
                disabled={testExistingMutation.isPending}
                className="inline-flex items-center gap-2 px-3 py-1.5 border border-input rounded-md text-sm font-medium hover:bg-muted transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
              >
                {testExistingMutation.isPending ? (
                  <Loader2 size={14} className="animate-spin" />
                ) : (
                  <PlugZap size={14} />
                )}
                Test Connection
              </button>
              <button
                onClick={() => {
                  resetForm()
                  setIsEditing(true)
                }}
                className="inline-flex items-center gap-2 px-3 py-1.5 border border-input rounded-md text-sm font-medium hover:bg-muted transition-colors"
              >
                <Edit3 size={14} />
                Edit
              </button>
              <button
                onClick={() => setShowDisconnectConfirm(true)}
                disabled={disconnectMutation.isPending}
                className="ml-auto inline-flex items-center gap-2 px-3 py-1.5 border border-red-200 dark:border-red-900 text-red-600 dark:text-red-400 rounded-md text-sm font-medium hover:bg-red-50 dark:hover:bg-red-950/30 transition-colors disabled:opacity-50"
              >
                <X size={14} />
                Disconnect
              </button>
            </div>
          </div>
        )}

        {isEditing && (
          <div className="space-y-4">
            <div>
              <label className="block text-sm font-medium text-foreground mb-1">
                Knowz Cloud URL <span className="text-red-500">*</span>
              </label>
              <input
                data-testid="platform-connection-url"
                type="url"
                value={url}
                onChange={(e) => setUrl(e.target.value)}
                placeholder="https://api.knowz.io"
                autoComplete="off"
                className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm focus:outline-none focus:ring-2 focus:ring-ring"
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-foreground mb-1">
                Display Name
              </label>
              <input
                type="text"
                value={displayName}
                onChange={(e) => setDisplayName(e.target.value)}
                placeholder="e.g. Production Knowz"
                autoComplete="off"
                className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm focus:outline-none focus:ring-2 focus:ring-ring"
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-foreground mb-1">
                API Key {!isConnected && <span className="text-red-500">*</span>}
                {isConnected && (
                  <span className="text-xs text-muted-foreground ml-1">
                    (leave blank to keep existing)
                  </span>
                )}
              </label>
              <input
                data-testid="platform-connection-api-key"
                type="password"
                autoComplete="off"
                value={apiKey}
                onChange={(e) => setApiKey(e.target.value)}
                placeholder="ukz_..."
                className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm font-mono focus:outline-none focus:ring-2 focus:ring-ring"
              />
              <p className="mt-1 text-xs text-muted-foreground flex items-center gap-1">
                <Shield size={11} />
                API key is never displayed after save. Only a masked form is shown.
              </p>
            </div>

            {candidateTestResult && (
              <div
                className={`rounded-md px-3 py-2 text-sm ${
                  candidateTestResult.status === 'Ok'
                    ? 'bg-green-50 dark:bg-green-950/30 text-green-800 dark:text-green-300'
                    : 'bg-red-50 dark:bg-red-950/30 text-red-800 dark:text-red-300'
                }`}
              >
                <strong>{candidateTestResult.status}:</strong>{' '}
                {candidateTestResult.message || 'No message'}
              </div>
            )}

            <div className="flex flex-wrap items-center gap-2 pt-2">
              <button
                onClick={() => testCandidateMutation.mutate()}
                disabled={testDisabled}
                className="inline-flex items-center gap-2 px-3 py-1.5 border border-input rounded-md text-sm font-medium hover:bg-muted transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
              >
                {testCandidateMutation.isPending ? (
                  <Loader2 size={14} className="animate-spin" />
                ) : (
                  <PlugZap size={14} />
                )}
                Test
              </button>
              <button
                onClick={() => saveMutation.mutate()}
                disabled={saveDisabled}
                className="inline-flex items-center gap-2 px-4 py-1.5 bg-primary text-primary-foreground rounded-md text-sm font-medium hover:opacity-90 transition-opacity disabled:opacity-50 disabled:cursor-not-allowed"
              >
                {saveMutation.isPending ? (
                  <Loader2 size={14} className="animate-spin" />
                ) : (
                  <Save size={14} />
                )}
                Save
              </button>
              {isConnected && (
                <button
                  onClick={() => {
                    resetForm()
                    setIsEditing(false)
                  }}
                  disabled={saveMutation.isPending}
                  className="px-3 py-1.5 text-sm text-muted-foreground hover:text-foreground transition-colors disabled:opacity-50"
                >
                  Cancel
                </button>
              )}
            </div>
          </div>
        )}
      </div>

      {showDisconnectConfirm && (
        <DisconnectConfirmModal
          linkCount={linkCount}
          isPending={disconnectMutation.isPending}
          onCancel={() => setShowDisconnectConfirm(false)}
          onConfirm={() => disconnectMutation.mutate()}
        />
      )}
    </div>
  )
}

function FieldRow({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <div className="flex items-start gap-4">
      <span className="text-xs font-medium uppercase tracking-wider text-muted-foreground w-32 shrink-0 pt-0.5">
        {label}
      </span>
      <span className={`text-sm text-foreground break-all ${mono ? 'font-mono' : ''}`}>
        {value}
      </span>
    </div>
  )
}

function ConnectionStatusPill({
  status,
  connected,
}: {
  status: PlatformConnectionTestStatus
  connected: boolean
}) {
  if (!connected) {
    return (
      <span
        data-testid="platform-connection-empty"
        className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium bg-muted text-muted-foreground"
      >
        <Plug size={11} />
        Not Connected
      </span>
    )
  }
  const config: Record<
    PlatformConnectionTestStatus,
    { label: string; classes: string; icon: React.ReactNode }
  > = {
    Untested: {
      label: 'Untested',
      classes: 'bg-muted text-muted-foreground',
      icon: <Plug size={11} />,
    },
    Ok: {
      label: 'Connected',
      classes: 'bg-green-50 dark:bg-green-950/30 text-green-700 dark:text-green-400',
      icon: <CheckCircle2 size={11} />,
    },
    Unauthorized: {
      label: 'Unauthorized',
      classes: 'bg-red-50 dark:bg-red-950/30 text-red-700 dark:text-red-400',
      icon: <XCircle size={11} />,
    },
    NetworkError: {
      label: 'Network Error',
      classes: 'bg-amber-50 dark:bg-amber-950/30 text-amber-700 dark:text-amber-400',
      icon: <AlertTriangle size={11} />,
    },
    SchemaIncompatible: {
      label: 'Schema Incompatible',
      classes: 'bg-red-50 dark:bg-red-950/30 text-red-700 dark:text-red-400',
      icon: <XCircle size={11} />,
    },
  }
  const c = config[status]
  return (
    <span className={`inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium ${c.classes}`}>
      {c.icon}
      {c.label}
    </span>
  )
}

function BannerBox({ banner, onDismiss }: { banner: Banner; onDismiss: () => void }) {
  const styles: Record<BannerKind, string> = {
    success: 'bg-green-50 dark:bg-green-950/30 text-green-800 dark:text-green-300 border-green-200 dark:border-green-900',
    error: 'bg-red-50 dark:bg-red-950/30 text-red-800 dark:text-red-300 border-red-200 dark:border-red-900',
    warning: 'bg-amber-50 dark:bg-amber-950/30 text-amber-800 dark:text-amber-300 border-amber-200 dark:border-amber-900',
    info: 'bg-blue-50 dark:bg-blue-950/30 text-blue-800 dark:text-blue-300 border-blue-200 dark:border-blue-900',
  }
  return (
    <div className={`flex items-start gap-2 px-3 py-2 border rounded-md text-sm ${styles[banner.kind]}`}>
      <span className="flex-1">{banner.message}</span>
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

function DisconnectConfirmModal({
  linkCount,
  isPending,
  onCancel,
  onConfirm,
}: {
  linkCount: number
  isPending: boolean
  onCancel: () => void
  onConfirm: () => void
}) {
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center">
      <div className="fixed inset-0 bg-black/50" onClick={onCancel} />
      <div className="relative bg-card border border-border/60 rounded-xl shadow-xl w-full max-w-md mx-4 p-6">
        <div className="flex items-center gap-3 mb-4">
          <div className="flex items-center justify-center w-10 h-10 rounded-full bg-red-50 dark:bg-red-950/40">
            <AlertTriangle size={20} className="text-red-600 dark:text-red-400" />
          </div>
          <h3 className="text-lg font-semibold">Disconnect Platform?</h3>
        </div>
        <p className="text-sm text-muted-foreground mb-2">
          This will remove the stored Knowz Cloud connection. Future syncs will fail until
          a new connection is configured.
        </p>
        {linkCount > 0 && (
          <p className="text-sm text-amber-600 dark:text-amber-400 mb-2">
            {linkCount} vault sync link{linkCount === 1 ? '' : 's'} still reference this
            connection. You may need to remove them first.
          </p>
        )}
        <div className="flex justify-end gap-3 mt-6">
          <button
            onClick={onCancel}
            disabled={isPending}
            className="px-4 py-2 border border-input rounded-md text-sm font-medium hover:bg-muted transition-colors disabled:opacity-50"
          >
            Cancel
          </button>
          <button
            onClick={onConfirm}
            disabled={isPending}
            className="inline-flex items-center gap-2 px-4 py-2 bg-red-600 hover:bg-red-700 text-white rounded-md text-sm font-medium transition-colors disabled:opacity-50"
          >
            {isPending && <Loader2 size={14} className="animate-spin" />}
            Disconnect
          </button>
        </div>
      </div>
    </div>
  )
}
