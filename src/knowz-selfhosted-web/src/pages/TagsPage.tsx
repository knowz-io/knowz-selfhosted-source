import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from '../lib/api-client'
import { Tag, Plus, Pencil, Trash2, X, Check, Search } from 'lucide-react'
import type { TagItem } from '../lib/types'
import SurfaceCard from '../components/ui/SurfaceCard'

export default function TagsPage() {
  const queryClient = useQueryClient()
  const [search, setSearch] = useState('')
  const [newTagName, setNewTagName] = useState('')
  const [showCreate, setShowCreate] = useState(false)
  const [editingId, setEditingId] = useState<string | null>(null)
  const [editName, setEditName] = useState('')
  const [deleteConfirm, setDeleteConfirm] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const { data: tags, isLoading } = useQuery({
    queryKey: ['tags', search],
    queryFn: () => api.listTags(search || undefined),
  })

  const createMutation = useMutation({
    mutationFn: (name: string) => api.createTag(name),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['tags'] })
      setNewTagName('')
      setShowCreate(false)
      setError(null)
    },
    onError: (err: Error) => setError(err.message),
  })

  const updateMutation = useMutation({
    mutationFn: ({ id, name }: { id: string; name: string }) => api.updateTag(id, name),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['tags'] })
      setEditingId(null)
      setError(null)
    },
    onError: (err: Error) => setError(err.message),
  })

  const deleteMutation = useMutation({
    mutationFn: (id: string) => api.deleteTag(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['tags'] })
      setDeleteConfirm(null)
      setError(null)
    },
    onError: (err: Error) => setError(err.message),
  })

  const handleCreate = () => {
    const trimmed = newTagName.trim()
    if (!trimmed) return
    createMutation.mutate(trimmed)
  }

  const handleUpdate = (id: string) => {
    const trimmed = editName.trim()
    if (!trimmed) return
    updateMutation.mutate({ id, name: trimmed })
  }

  const startEdit = (tag: TagItem) => {
    setEditingId(tag.id)
    setEditName(tag.name)
    setError(null)
  }

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-end">
        <button
          onClick={() => { setShowCreate(true); setError(null) }}
          className="inline-flex items-center gap-2 rounded-2xl bg-primary px-4 py-2 text-sm font-semibold text-primary-foreground shadow-sm shadow-primary/20 transition-all duration-200 hover:brightness-110"
        >
          <Plus size={16} /> Add tag
        </button>
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
            value={newTagName}
            onChange={(e) => setNewTagName(e.target.value)}
            onKeyDown={(e) => e.key === 'Enter' && handleCreate()}
            placeholder="Tag name..."
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
            onClick={() => { setShowCreate(false); setNewTagName(''); setError(null) }}
            className="p-1.5 text-muted-foreground hover:bg-muted rounded transition-colors"
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
            placeholder="Search tags..."
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
          {tags?.map((tag: TagItem) => (
            <SurfaceCard key={tag.id} className="p-3">
              <div className="flex items-center gap-3">
              <Tag size={16} className="text-muted-foreground flex-shrink-0" />
              {editingId === tag.id ? (
                <>
                  <input
                    type="text"
                    value={editName}
                    onChange={(e) => setEditName(e.target.value)}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter') handleUpdate(tag.id)
                      if (e.key === 'Escape') setEditingId(null)
                    }}
                    className="flex-1 px-2 py-1 text-sm border border-input rounded bg-card focus:outline-none focus:ring-1 focus:ring-ring"
                    autoFocus
                  />
                  <button
                    onClick={() => handleUpdate(tag.id)}
                    disabled={updateMutation.isPending}
                    className="p-1 text-green-600 hover:bg-green-50 dark:hover:bg-green-950/30 rounded"
                  >
                    <Check size={16} />
                  </button>
                  <button
                    onClick={() => setEditingId(null)}
                    className="p-1 text-muted-foreground hover:bg-muted rounded transition-colors"
                  >
                    <X size={16} />
                  </button>
                </>
              ) : (
                <>
                  <Link
                    to={`/knowledge?tag=${encodeURIComponent(tag.name)}`}
                    className="flex-1 rounded-md px-2 py-1 text-sm font-medium text-foreground transition-colors hover:bg-muted hover:underline"
                  >
                    {tag.name}
                  </Link>
                  <span className="text-xs text-muted-foreground">
                    {tag.knowledgeCount} item{tag.knowledgeCount !== 1 ? 's' : ''}
                  </span>
                  <button
                    onClick={() => startEdit(tag)}
                    className="p-1 text-muted-foreground hover:text-foreground hover:bg-muted rounded transition-colors"
                  >
                    <Pencil size={14} />
                  </button>
                  {deleteConfirm === tag.id ? (
                    <div className="flex items-center gap-1">
                      <button
                        onClick={() => deleteMutation.mutate(tag.id)}
                        disabled={deleteMutation.isPending}
                        className="px-2 py-0.5 text-xs text-red-600 border border-red-300 dark:border-red-800 rounded hover:bg-red-50 dark:hover:bg-red-950/30"
                      >
                        Delete
                      </button>
                      <button
                        onClick={() => setDeleteConfirm(null)}
                        className="p-1 text-muted-foreground hover:bg-muted rounded transition-colors"
                      >
                        <X size={14} />
                      </button>
                    </div>
                  ) : (
                    <button
                      onClick={() => setDeleteConfirm(tag.id)}
                      className="p-1 text-muted-foreground hover:text-red-600 hover:bg-red-50 dark:hover:bg-red-950/30 rounded transition-colors"
                    >
                      <Trash2 size={14} />
                    </button>
                  )}
                </>
              )}
              </div>
            </SurfaceCard>
          ))}
          {tags?.length === 0 && (
            <SurfaceCard className="p-10 text-center">
              <p className="text-sm text-muted-foreground">No tags found.</p>
            </SurfaceCard>
          )}
        </div>
      )}
    </div>
  )
}
