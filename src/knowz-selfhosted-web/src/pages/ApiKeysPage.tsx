import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Key, Copy, Trash2, RefreshCw, CheckCircle, AlertTriangle, Loader2 } from 'lucide-react'
import { api } from '../lib/api-client'
import SurfaceCard from '../components/ui/SurfaceCard'

export default function ApiKeysPage() {
  const queryClient = useQueryClient()
  const [newKey, setNewKey] = useState<string | null>(null)
  const [copied, setCopied] = useState(false)
  const [showConfirmGenerate, setShowConfirmGenerate] = useState(false)
  const [showConfirmRevoke, setShowConfirmRevoke] = useState(false)

  const { data: keyStatus, isLoading } = useQuery({
    queryKey: ['account', 'api-key'],
    queryFn: () => api.getApiKeyStatus(),
  })

  const generateMutation = useMutation({
    mutationFn: () => api.generateApiKey(),
    onSuccess: (data) => {
      setNewKey(data.apiKey)
      setShowConfirmGenerate(false)
      queryClient.invalidateQueries({ queryKey: ['account', 'api-key'] })
    },
  })

  const revokeMutation = useMutation({
    mutationFn: () => api.revokeApiKey(),
    onSuccess: () => {
      setNewKey(null)
      setShowConfirmRevoke(false)
      queryClient.invalidateQueries({ queryKey: ['account', 'api-key'] })
    },
  })

  const handleCopy = async (text: string) => {
    await navigator.clipboard.writeText(text)
    setCopied(true)
    setTimeout(() => setCopied(false), 2000)
  }

  const handleGenerate = () => {
    if (keyStatus?.hasKey) {
      setShowConfirmGenerate(true)
    } else {
      generateMutation.mutate()
    }
  }

  if (isLoading) {
    return (
      <div className="flex items-center justify-center py-12">
        <Loader2 size={24} className="animate-spin text-muted-foreground" />
      </div>
    )
  }

  return (
    <div className="space-y-4 max-w-2xl">
      {/* New key banner - shown once after generation */}
      {newKey && (
        <SurfaceCard className="border-green-200/90 bg-green-50/80 p-4 dark:border-green-900/60 dark:bg-green-950/20">
          <div className="flex items-start gap-3">
            <CheckCircle size={20} className="text-green-600 dark:text-green-400 mt-0.5 shrink-0" />
            <div className="flex-1 min-w-0">
              <p className="text-sm font-medium text-green-800 dark:text-green-300">
                API key generated successfully
              </p>
              <p className="text-xs text-green-700 dark:text-green-400 mt-1">
                Copy this key now. You will not be able to see it again.
              </p>
              <div className="mt-3 flex items-center gap-2">
                <code className="flex-1 px-3 py-2 bg-card border border-green-300 dark:border-green-700 rounded text-sm font-mono break-all">
                  {newKey}
                </code>
                <button
                  onClick={() => handleCopy(newKey)}
                  className="shrink-0 inline-flex items-center gap-1.5 px-3 py-2 bg-green-600 hover:bg-green-700 text-white rounded text-sm font-medium transition-colors"
                >
                  {copied ? <CheckCircle size={14} /> : <Copy size={14} />}
                  {copied ? 'Copied' : 'Copy'}
                </button>
              </div>
            </div>
          </div>
        </SurfaceCard>
      )}

      {/* Current key status */}
      <SurfaceCard className="p-5">
        <h2 className="text-lg font-semibold mb-4">Current API Key</h2>

        {keyStatus?.hasKey ? (
          <div className="space-y-4">
            <div className="flex items-center gap-3">
              <div className="flex items-center gap-2 flex-1 min-w-0">
                <Key size={16} className="text-muted-foreground shrink-0" />
                <code className="px-3 py-1.5 bg-muted rounded text-sm font-mono">
                  {keyStatus.maskedKey}
                </code>
              </div>
            </div>

            <div className="flex gap-3">
              <button
                onClick={handleGenerate}
                disabled={generateMutation.isPending}
                className="inline-flex items-center gap-2 px-4 py-2 bg-primary text-primary-foreground rounded-md text-sm font-medium hover:opacity-90 transition-opacity disabled:opacity-50"
              >
                {generateMutation.isPending ? (
                  <Loader2 size={14} className="animate-spin" />
                ) : (
                  <RefreshCw size={14} />
                )}
                Regenerate Key
              </button>
              <button
                onClick={() => setShowConfirmRevoke(true)}
                disabled={revokeMutation.isPending}
                className="inline-flex items-center gap-2 px-4 py-2 border border-red-300 dark:border-red-700 text-red-600 dark:text-red-400 rounded-md text-sm font-medium hover:bg-red-50 dark:hover:bg-red-950/30 transition-colors disabled:opacity-50"
              >
                {revokeMutation.isPending ? (
                  <Loader2 size={14} className="animate-spin" />
                ) : (
                  <Trash2 size={14} />
                )}
                Revoke Key
              </button>
            </div>
          </div>
        ) : (
          <div className="space-y-4">
            <p className="text-sm text-muted-foreground">
              No API key generated yet. Generate one to access the Knowz API programmatically.
            </p>
            <button
              onClick={() => generateMutation.mutate()}
              disabled={generateMutation.isPending}
              className="inline-flex items-center gap-2 px-4 py-2 bg-primary text-primary-foreground rounded-md text-sm font-medium hover:opacity-90 transition-opacity disabled:opacity-50"
            >
              {generateMutation.isPending ? (
                <Loader2 size={14} className="animate-spin" />
              ) : (
                <Key size={14} />
              )}
              Generate API Key
            </button>
          </div>
        )}

        {generateMutation.isError && (
          <p className="mt-3 text-sm text-red-600 dark:text-red-400">
            Failed to generate API key. Please try again.
          </p>
        )}
        {revokeMutation.isError && (
          <p className="mt-3 text-sm text-red-600 dark:text-red-400">
            Failed to revoke API key. Please try again.
          </p>
        )}
      </SurfaceCard>

      {/* Confirm regenerate dialog */}
      {showConfirmGenerate && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50">
          <div className="bg-card border border-border/60 rounded-xl p-6 max-w-md w-full mx-4 shadow-xl">
            <div className="flex items-start gap-3">
              <AlertTriangle size={20} className="text-amber-500 mt-0.5 shrink-0" />
              <div>
                <h3 className="font-semibold">Regenerate API Key?</h3>
                <p className="text-sm text-muted-foreground mt-1">
                  This will invalidate your existing API key. Any applications using the current key will stop working.
                </p>
              </div>
            </div>
            <div className="flex justify-end gap-3 mt-6">
              <button
                onClick={() => setShowConfirmGenerate(false)}
                className="px-4 py-2 border border-input rounded-md text-sm font-medium hover:bg-muted transition-colors"
              >
                Cancel
              </button>
              <button
                onClick={() => generateMutation.mutate()}
                disabled={generateMutation.isPending}
                className="px-4 py-2 bg-primary text-primary-foreground rounded-md text-sm font-medium hover:opacity-90 transition-colors disabled:opacity-50"
              >
                {generateMutation.isPending ? 'Generating...' : 'Regenerate'}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Confirm revoke dialog */}
      {showConfirmRevoke && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50">
          <div className="bg-card border border-border/60 rounded-xl p-6 max-w-md w-full mx-4 shadow-xl">
            <div className="flex items-start gap-3">
              <AlertTriangle size={20} className="text-red-500 mt-0.5 shrink-0" />
              <div>
                <h3 className="font-semibold">Revoke API Key?</h3>
                <p className="text-sm text-muted-foreground mt-1">
                  This will permanently revoke your API key. Any applications using this key will immediately lose access.
                </p>
              </div>
            </div>
            <div className="flex justify-end gap-3 mt-6">
              <button
                onClick={() => setShowConfirmRevoke(false)}
                className="px-4 py-2 border border-input rounded-md text-sm font-medium hover:bg-muted transition-colors"
              >
                Cancel
              </button>
              <button
                onClick={() => revokeMutation.mutate()}
                disabled={revokeMutation.isPending}
                className="px-4 py-2 bg-red-600 text-white rounded-md text-sm font-medium hover:bg-red-700 transition-colors disabled:opacity-50"
              >
                {revokeMutation.isPending ? 'Revoking...' : 'Revoke Key'}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Usage instructions */}
      <SurfaceCard className="p-5">
        <h2 className="text-lg font-semibold mb-3">Usage</h2>
        <p className="text-sm text-muted-foreground mb-3">
          Include your API key in the <code className="px-1.5 py-0.5 bg-muted rounded text-xs">X-Api-Key</code> header with each request:
        </p>
        <pre className="px-4 py-3 bg-muted rounded-lg text-sm font-mono overflow-x-auto">
{`curl -H "X-Api-Key: your-api-key" \\
  ${window.location.origin}/api/v1/knowledge`}
        </pre>
      </SurfaceCard>
    </div>
  )
}
