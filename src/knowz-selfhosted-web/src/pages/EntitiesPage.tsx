import { useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from '../lib/api-client'
import { Users, MapPin, Calendar, Plus, Pencil, Trash2, X, Check, Search } from 'lucide-react'
import type { EntityItem } from '../lib/types'
import { useFormatters } from '../hooks/useFormatters'
import SurfaceCard from '../components/ui/SurfaceCard'

const entityTabs = [
  { type: 'person', label: 'Persons', icon: Users },
  { type: 'location', label: 'Locations', icon: MapPin },
  { type: 'event', label: 'Events', icon: Calendar },
] as const

type EntityType = (typeof entityTabs)[number]['type']

export default function EntitiesPage() {
  const queryClient = useQueryClient()
  const fmt = useFormatters()
  const [activeTab, setActiveTab] = useState<EntityType>('person')
  const [search, setSearch] = useState('')
  const [showCreate, setShowCreate] = useState(false)
  const [newName, setNewName] = useState('')
  const [editingId, setEditingId] = useState<string | null>(null)
  const [editName, setEditName] = useState('')
  const [deleteConfirm, setDeleteConfirm] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const { data, isLoading } = useQuery({
    queryKey: ['entities', activeTab, search],
    queryFn: () => api.findEntities(activeTab, search || undefined),
  })

  const createMutation = useMutation({
    mutationFn: (name: string) => api.createEntity(activeTab, name),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['entities', activeTab] })
      setNewName('')
      setShowCreate(false)
      setError(null)
    },
    onError: (err: Error) => setError(err.message),
  })

  const updateMutation = useMutation({
    mutationFn: ({ id, name }: { id: string; name: string }) =>
      api.updateEntity(id, activeTab, name),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['entities', activeTab] })
      setEditingId(null)
      setError(null)
    },
    onError: (err: Error) => setError(err.message),
  })

  const deleteMutation = useMutation({
    mutationFn: (id: string) => api.deleteEntity(id, activeTab),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['entities', activeTab] })
      setDeleteConfirm(null)
      setError(null)
    },
    onError: (err: Error) => setError(err.message),
  })

  const handleTabChange = (tab: EntityType) => {
    setActiveTab(tab)
    setSearch('')
    setShowCreate(false)
    setEditingId(null)
    setDeleteConfirm(null)
    setError(null)
  }

  const handleCreate = () => {
    const trimmed = newName.trim()
    if (!trimmed) return
    createMutation.mutate(trimmed)
  }

  const handleUpdate = (id: string) => {
    const trimmed = editName.trim()
    if (!trimmed) return
    updateMutation.mutate({ id, name: trimmed })
  }

  const entities: EntityItem[] = data?.entities ?? []
  const activeLabel = entityTabs.find((tab) => tab.type === activeTab)?.label ?? 'Entities'

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-end">
        <button
          onClick={() => { setShowCreate(true); setError(null) }}
          className="inline-flex items-center gap-2 rounded-2xl bg-primary px-4 py-2 text-sm font-semibold text-primary-foreground shadow-sm shadow-primary/20 transition-all duration-200 hover:brightness-110"
        >
          <Plus size={16} /> Add {activeLabel.slice(0, -1)}
        </button>
      </div>

      {/* Tabs */}
      <div className="sh-toolbar flex flex-wrap gap-2 p-1.5">
        {entityTabs.map(({ type, label, icon: Icon }) => (
          <button
            key={type}
            onClick={() => handleTabChange(type)}
            className={`inline-flex items-center gap-2 rounded-2xl px-4 py-2.5 text-sm font-medium transition-all duration-150 ${
              activeTab === type
                ? 'bg-card text-foreground shadow-card ring-1 ring-border/70'
                : 'text-muted-foreground hover:bg-background/70 hover:text-foreground'
            }`}
          >
            <Icon size={16} />
            {label}
          </button>
        ))}
      </div>

      {error && (
        <SurfaceCard className="border-red-200/90 bg-red-50/80 p-4 text-sm text-red-700 dark:border-red-900/60 dark:bg-red-950/20 dark:text-red-300">
          {error}
        </SurfaceCard>
      )}

      {/* Create form */}
      {showCreate && (
        <SurfaceCard className="p-4">
          <input
            type="text"
            value={newName}
            onChange={(e) => setNewName(e.target.value)}
            onKeyDown={(e) => e.key === 'Enter' && handleCreate()}
            placeholder={`${activeTab.charAt(0).toUpperCase() + activeTab.slice(1)} name...`}
            className="flex-1 px-3 py-1.5 text-sm border border-input rounded-md bg-card focus:outline-none focus:ring-1 focus:ring-ring"
            autoFocus
          />
          <button
            onClick={handleCreate}
            disabled={createMutation.isPending}
            className="p-1.5 text-green-600 hover:bg-green-50 dark:hover:bg-green-950/30 rounded"
          >
            <Check size={18} />
          </button>
          <button
            onClick={() => { setShowCreate(false); setNewName(''); setError(null) }}
            className="p-1.5 text-muted-foreground hover:bg-muted rounded"
          >
            <X size={18} />
          </button>
        </SurfaceCard>
      )}

      {/* Search */}
      <div className="sh-toolbar p-3">
        <div className="relative">
          <Search size={16} className="absolute left-3 top-1/2 -translate-y-1/2 text-muted-foreground" />
          <input
            type="text"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder={`Search ${activeTab}s...`}
            className="w-full rounded-2xl border border-border/70 bg-card/70 py-2.5 pl-10 pr-3 text-sm focus:outline-none focus:ring-2 focus:ring-ring"
          />
        </div>
      </div>

      {/* List */}
      {isLoading ? (
        <div className="space-y-2">
          {Array.from({ length: 5 }).map((_, i) => (
            <div key={i} className="sh-surface h-14 animate-pulse" />
          ))}
        </div>
      ) : (
        <div className="space-y-3">
          {entities.map((entity) => {
            const ActiveIcon = entityTabs.find((t) => t.type === activeTab)?.icon ?? Users
            return (
              <SurfaceCard key={entity.id} className="p-3">
                <div className="flex items-center gap-3">
                <ActiveIcon size={16} className="text-muted-foreground flex-shrink-0" />
                {editingId === entity.id ? (
                  <>
                    <input
                      type="text"
                      value={editName}
                      onChange={(e) => setEditName(e.target.value)}
                      onKeyDown={(e) => {
                        if (e.key === 'Enter') handleUpdate(entity.id)
                        if (e.key === 'Escape') setEditingId(null)
                      }}
                      className="flex-1 px-2 py-1 text-sm border border-input rounded bg-card focus:outline-none focus:ring-1 focus:ring-ring"
                      autoFocus
                    />
                    <button
                      onClick={() => handleUpdate(entity.id)}
                      disabled={updateMutation.isPending}
                      className="p-1 text-green-600 hover:bg-green-50 dark:hover:bg-green-950/30 rounded"
                    >
                      <Check size={16} />
                    </button>
                    <button
                      onClick={() => setEditingId(null)}
                      className="p-1 text-muted-foreground hover:bg-muted rounded"
                    >
                      <X size={16} />
                    </button>
                  </>
                ) : (
                  <>
                    <span className="flex-1 font-medium text-sm">{entity.name}</span>
                    <span className="text-xs text-muted-foreground">
                      {fmt.date(entity.createdAt)}
                    </span>
                    <button
                      onClick={() => {
                        setEditingId(entity.id)
                        setEditName(entity.name)
                        setError(null)
                      }}
                      className="p-1 text-muted-foreground hover:text-foreground hover:bg-muted rounded"
                    >
                      <Pencil size={14} />
                    </button>
                    {deleteConfirm === entity.id ? (
                      <div className="flex items-center gap-1">
                        <button
                          onClick={() => deleteMutation.mutate(entity.id)}
                          disabled={deleteMutation.isPending}
                          className="px-2 py-0.5 text-xs text-red-600 border border-red-300 dark:border-red-800 rounded hover:bg-red-50 dark:hover:bg-red-950/30"
                        >
                          Delete
                        </button>
                        <button
                          onClick={() => setDeleteConfirm(null)}
                          className="p-1 text-muted-foreground hover:bg-muted rounded"
                        >
                          <X size={14} />
                        </button>
                      </div>
                    ) : (
                      <button
                        onClick={() => setDeleteConfirm(entity.id)}
                        className="p-1 text-muted-foreground hover:text-red-600 hover:bg-red-50 dark:hover:bg-red-950/30 rounded"
                      >
                        <Trash2 size={14} />
                      </button>
                    )}
                  </>
                )}
                </div>
              </SurfaceCard>
            )
          })}
          {entities.length === 0 && (
            <SurfaceCard className="p-10 text-center">
              <p className="text-sm text-muted-foreground">No {activeTab}s found.</p>
            </SurfaceCard>
          )}
        </div>
      )}
    </div>
  )
}
