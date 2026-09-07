import { useState, useRef, useEffect } from 'react'
import { Link } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { BookOpen, Loader2, Info, X } from 'lucide-react'
import { api } from '../lib/api-client'
import type { KnowledgeItem } from '../lib/types'
import { AnchoredPortal } from './ui/AnchoredPortal'

interface SourcesBubbleProps {
  sources: { knowledgeId: string }[]
}

function SourceRow({
  knowledgeId,
  enabled,
  onShowExcerpt,
}: {
  knowledgeId: string
  enabled: boolean
  onShowExcerpt: (item: KnowledgeItem) => void
}) {
  const id = knowledgeId ?? (knowledgeId as any).KnowledgeId ?? ''

  const { data, isLoading, isError } = useQuery({
    queryKey: ['knowledge', id],
    queryFn: () => api.getKnowledge(id),
    staleTime: 5 * 60_000,
    enabled: enabled && !!id,
  })

  if (isLoading) {
    return (
      <div className="flex items-center gap-2 px-2.5 py-1.5 text-xs text-muted-foreground">
        <Loader2 size={12} className="animate-spin" />
        <span className="truncate">{id.slice(0, 8)}...</span>
      </div>
    )
  }

  if (isError || !data) {
    return (
      <div className="flex items-center gap-2 px-2.5 py-1.5">
        <Link
          to={`/knowledge/${id}`}
          className="text-xs text-primary hover:underline truncate"
        >
          {id.slice(0, 8)}...
        </Link>
      </div>
    )
  }

  return (
    <div className="flex items-center gap-2 px-2.5 py-1.5 group/row hover:bg-muted/50 rounded-md transition-colors">
      <Link
        to={`/knowledge/${id}`}
        className="flex-1 min-w-0 text-xs text-primary hover:underline truncate"
        title={data.title}
      >
        {data.title}
      </Link>
      <span className="text-[10px] px-1.5 py-0.5 bg-muted text-muted-foreground rounded flex-shrink-0">
        {data.type}
      </span>
      {(data.summary || data.content) && (
        <button
          onClick={(e) => {
            e.stopPropagation()
            onShowExcerpt(data)
          }}
          className="opacity-0 group-hover/row:opacity-100 p-0.5 rounded hover:bg-accent transition-opacity flex-shrink-0"
          title="Show excerpt"
        >
          <Info size={12} className="text-muted-foreground" />
        </button>
      )}
    </div>
  )
}

function ExcerptModal({
  item,
  onClose,
}: {
  item: KnowledgeItem
  onClose: () => void
}) {
  const modalRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    function handleClickOutside(event: MouseEvent) {
      if (modalRef.current && !modalRef.current.contains(event.target as Node)) {
        onClose()
      }
    }
    document.addEventListener('mousedown', handleClickOutside)
    return () => document.removeEventListener('mousedown', handleClickOutside)
  }, [onClose])

  useEffect(() => {
    function handleEsc(e: KeyboardEvent) {
      if (e.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', handleEsc)
    return () => window.removeEventListener('keydown', handleEsc)
  }, [onClose])

  const excerpt = item.summary || item.content.slice(0, 300)

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/30 animate-fade-in">
      <div
        ref={modalRef}
        className="bg-card border border-border/60 rounded-xl shadow-lg p-4 max-w-md w-full mx-4 animate-slide-up"
      >
        <div className="flex items-start justify-between gap-2 mb-2">
          <h4 className="text-sm font-medium truncate">{item.title}</h4>
          <button
            onClick={onClose}
            className="p-0.5 rounded hover:bg-muted transition-colors flex-shrink-0"
          >
            <X size={14} className="text-muted-foreground" />
          </button>
        </div>
        <p className="text-xs text-muted-foreground mb-2">
          {item.type}
          {item.topic && <> &middot; {item.topic.name}</>}
        </p>
        <div className="text-sm text-foreground/90 whitespace-pre-wrap break-words max-h-48 overflow-y-auto">
          {excerpt}
          {!item.summary && item.content.length > 300 && '...'}
        </div>
        <div className="mt-3 pt-2 border-t border-border/40">
          <Link
            to={`/knowledge/${item.id}`}
            className="text-xs text-primary hover:underline"
          >
            View full item
          </Link>
        </div>
      </div>
    </div>
  )
}

export default function SourcesBubble({ sources }: SourcesBubbleProps) {
  const [open, setOpen] = useState(false)
  const [hasOpened, setHasOpened] = useState(false)
  const [excerptItem, setExcerptItem] = useState<KnowledgeItem | null>(null)
  const popoverRef = useRef<HTMLDivElement>(null)
  const triggerRef = useRef<HTMLButtonElement>(null)
  const panelRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    function handleClickOutside(event: MouseEvent) {
      const target = event.target as Node
      if (popoverRef.current?.contains(target)) return
      if (panelRef.current?.contains(target)) return
      setOpen(false)
    }
    if (open) {
      document.addEventListener('mousedown', handleClickOutside)
    }
    return () => document.removeEventListener('mousedown', handleClickOutside)
  }, [open])

  const handleToggle = () => {
    const next = !open
    setOpen(next)
    if (next) setHasOpened(true)
  }

  if (!sources || sources.length === 0) return null

  return (
    <div className="relative inline-block" ref={popoverRef}>
      <button
        ref={triggerRef}
        onClick={handleToggle}
        className="inline-flex items-center gap-1 px-2 py-0.5 bg-muted text-muted-foreground text-[10px] rounded hover:bg-accent transition-colors"
      >
        <BookOpen size={10} />
        {sources.length} {sources.length === 1 ? 'source' : 'sources'}
      </button>

      <AnchoredPortal
        open={open}
        anchorRef={triggerRef}
        panelRef={panelRef}
        placement="top-start"
        offset={6}
        className="w-72 rounded-xl border border-border/80 bg-card text-card-foreground shadow-elevated animate-slide-up"
      >
        <div className="px-3 py-2 border-b border-border/40">
          <span className="text-[10px] font-medium text-muted-foreground uppercase tracking-wider">
            Sources
          </span>
        </div>
        <div className="py-1 max-h-48 overflow-y-auto">
          {sources.map((source, idx) => {
            const id = source.knowledgeId ?? (source as any).KnowledgeId ?? ''
            return (
              <SourceRow
                key={id || idx}
                knowledgeId={id}
                enabled={hasOpened}
                onShowExcerpt={setExcerptItem}
              />
            )
          })}
        </div>
      </AnchoredPortal>

      {excerptItem && (
        <ExcerptModal
          item={excerptItem}
          onClose={() => setExcerptItem(null)}
        />
      )}
    </div>
  )
}
