import { useState } from 'react'
import { toUserError } from '../lib/toUserError'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { Link, useNavigate } from 'react-router-dom'
import { api } from '../lib/api-client'
import { Archive, RefreshCw, Plus, X, Settings } from 'lucide-react'
import SurfaceCard from '../components/ui/SurfaceCard'

export default function VaultListPage() {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const [showCreate, setShowCreate] = useState(false)
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [vaultType, setVaultType] = useState('')
  const [createError, setCreateError] = useState('')

  const { data, isLoading, error, refetch } = useQuery({
    queryKey: ['vaults', 'list'],
    queryFn: () => api.listVaults(true),
  })

  const createMutation = useMutation({
    mutationFn: () =>
      api.createVault({
        name: name.trim(),
        description: description.trim() || undefined,
        vaultType: vaultType || undefined,
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['vaults'] })
      setShowCreate(false)
      setName('')
      setDescription('')
      setVaultType('')
      setCreateError('')
    },
    onError: (err) => {
      setCreateError(toUserError(err, 'We could not create that vault.'))
    },
  })

  const handleCreate = (e: React.FormEvent) => {
    e.preventDefault()
    if (!name.trim()) return
    setCreateError('')
    createMutation.mutate()
  }

  if (error) {
    return (
      <div className="space-y-4">
        <SurfaceCard className="p-8 text-center">
          <p className="mb-4 text-red-600 dark:text-red-400">
            {toUserError(error, 'We could not load your vaults.')}
          </p>
          <button
            onClick={() => refetch()}
            className="inline-flex items-center gap-2 rounded-2xl bg-primary px-4 py-2.5 text-sm font-semibold text-primary-foreground shadow-sm shadow-primary/20 transition-all duration-200 hover:brightness-110"
          >
            <RefreshCw size={16} /> Retry
          </button>
        </SurfaceCard>
      </div>
    )
  }

  const vaults = data?.vaults ?? []

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-end">
        <button
          onClick={() => setShowCreate(true)}
          className="inline-flex items-center gap-2 rounded-2xl bg-primary px-4 py-2 text-sm font-semibold text-primary-foreground shadow-sm shadow-primary/20 transition-all duration-200 hover:brightness-110"
        >
          <Plus size={16} /> Create Vault
        </button>
      </div>

      {showCreate && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/55 p-4">
          <div className="sh-surface w-full max-w-lg">
            <div className="flex items-center justify-between border-b border-border/60 px-6 py-5">
              <div>
                <p className="sh-kicker">Create</p>
                <h2 className="mt-2 text-xl font-semibold">New vault</h2>
              </div>
              <button
                onClick={() => {
                  setShowCreate(false)
                  setCreateError('')
                }}
                className="rounded-2xl p-2 transition-colors hover:bg-muted"
              >
                <X size={20} />
              </button>
            </div>
            <form onSubmit={handleCreate} className="space-y-4 p-6">
              <div>
                <label className="mb-1 block text-sm font-medium">Name *</label>
                <input
                  type="text"
                  value={name}
                  onChange={(e) => setName(e.target.value)}
                  placeholder="Customer Insights"
                  className="w-full rounded-2xl border border-input bg-background/80 px-3 py-2.5 text-sm"
                  autoFocus
                  required
                />
              </div>
              <div>
                <label className="mb-1 block text-sm font-medium">Description</label>
                <textarea
                  value={description}
                  onChange={(e) => setDescription(e.target.value)}
                  placeholder="What belongs in this vault?"
                  rows={3}
                  className="w-full resize-none rounded-2xl border border-input bg-background/80 px-3 py-2.5 text-sm"
                />
              </div>
              <div>
                <label className="mb-1 block text-sm font-medium">Type</label>
                <select
                  value={vaultType}
                  onChange={(e) => setVaultType(e.target.value)}
                  className="w-full rounded-2xl border border-input bg-background/80 px-3 py-2.5 text-sm"
                >
                  <option value="">General Knowledge</option>
                  <option value="Business">Business</option>
                  <option value="Product">Product</option>
                  <option value="CodeBase">CodeBase</option>
                  <option value="DailyDiary">Daily Diary</option>
                  <option value="QuestionAnswer">Question &amp; Answer</option>
                  <option value="PersonBound">Person Bound</option>
                  <option value="LocationBound">Location Bound</option>
                </select>
              </div>
              {createError && (
                <p className="text-sm text-red-600 dark:text-red-400">{createError}</p>
              )}
              <div className="flex justify-end gap-3 pt-2">
                <button
                  type="button"
                  onClick={() => {
                    setShowCreate(false)
                    setCreateError('')
                  }}
                  className="rounded-2xl border border-input px-4 py-2 text-sm font-medium text-muted-foreground transition-colors hover:bg-muted"
                >
                  Cancel
                </button>
                <button
                  type="submit"
                  disabled={!name.trim() || createMutation.isPending}
                  className="rounded-2xl bg-primary px-4 py-2 text-sm font-semibold text-primary-foreground transition-all duration-200 hover:brightness-110 disabled:opacity-50"
                >
                  {createMutation.isPending ? 'Creating...' : 'Create'}
                </button>
              </div>
            </form>
          </div>
        </div>
      )}

      {isLoading ? (
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-3">
          {[1, 2, 3].map((i) => (
            <div key={i} className="sh-surface h-40 animate-pulse" />
          ))}
        </div>
      ) : (
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-3">
          {vaults.map((vault) => (
            <Link
              key={vault.id}
              to={`/knowledge?vaultId=${vault.id}`}
              className="block"
            >
              <SurfaceCard className="relative h-full p-5 transition-all duration-200 hover:-translate-y-0.5 hover:bg-card">
                <button
                  type="button"
                  onClick={(e) => {
                    e.preventDefault()
                    e.stopPropagation()
                    navigate(`/vaults/${vault.id}`)
                  }}
                  className="absolute right-3 top-3 inline-flex h-8 w-8 items-center justify-center rounded-xl text-muted-foreground transition-colors hover:bg-muted hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                  aria-label={`Manage ${vault.name}`}
                  title="Manage vault (edit or delete)"
                >
                  <Settings size={16} />
                </button>
                <div className="mb-3 flex items-start gap-3">
                  <div className="flex h-11 w-11 shrink-0 items-center justify-center rounded-2xl bg-primary/10 text-primary">
                    <Archive size={18} />
                  </div>
                  <div className="min-w-0 flex-1 pr-8">
                    <div className="flex flex-wrap items-center gap-2">
                      <h3 className="truncate font-semibold">{vault.name}</h3>
                      {vault.isDefault && (
                        <span className="rounded-full bg-blue-100 px-2 py-0.5 text-[10px] font-semibold text-blue-700 dark:bg-blue-900/30 dark:text-blue-300">
                          Default
                        </span>
                      )}
                    </div>
                    {vault.vaultType && (
                      <span className="mt-2 inline-block rounded-full bg-muted px-2 py-0.5 text-[10px] font-medium text-muted-foreground">
                        {vault.vaultType}
                      </span>
                    )}
                  </div>
                </div>
                {vault.description && (
                  <p className="mb-4 line-clamp-3 text-sm text-muted-foreground">
                    {vault.description}
                  </p>
                )}
                <div className="rounded-[20px] border border-border/60 bg-background/70 px-3 py-3 text-sm">
                  <p className="font-semibold">{vault.knowledgeCount ?? 0} items</p>
                  <p className="mt-1 text-xs text-muted-foreground">Open the filtered knowledge view.</p>
                </div>
              </SurfaceCard>
            </Link>
          ))}
          {vaults.length === 0 && (
            <SurfaceCard className="col-span-full p-10 text-center">
              <p className="text-muted-foreground">
                No vaults found. Create one to get started.
              </p>
            </SurfaceCard>
          )}
        </div>
      )}
    </div>
  )
}
