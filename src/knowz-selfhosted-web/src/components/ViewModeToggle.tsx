import { useState, useRef, useEffect } from 'react'
import { Grid3x3, LayoutGrid, List, Image, Code2, ChevronDown, Check } from 'lucide-react'
import { useViewMode, type ViewMode } from '../contexts/ViewModeContext'
import { AnchoredPortal } from './ui/AnchoredPortal'

const modeConfig = {
  grid: {
    icon: Grid3x3,
    label: 'Grid',
    title: 'Grid view - Default cards',
    description: 'Default cards',
  },
  compact: {
    icon: LayoutGrid,
    label: 'Compact',
    title: 'Compact view - Dense grid',
    description: 'Dense grid',
  },
  list: {
    icon: List,
    label: 'List',
    title: 'List view - Horizontal rows',
    description: 'Horizontal rows',
  },
  gallery: {
    icon: Image,
    label: 'Gallery',
    title: 'Gallery view - Large cards',
    description: 'Large cards',
  },
  code: {
    icon: Code2,
    label: 'Code',
    title: 'Code view - File browser layout',
    description: 'File browser',
  },
} as const

interface ViewModeToggleProps {
  pageKey?: string
  allowedModes?: ViewMode[]
}

export function ViewModeToggle({ pageKey, allowedModes }: ViewModeToggleProps) {
  const { viewMode, setViewMode } = useViewMode(pageKey)
  const [open, setOpen] = useState(false)
  const wrapRef = useRef<HTMLDivElement>(null)
  const triggerRef = useRef<HTMLButtonElement>(null)
  const panelRef = useRef<HTMLDivElement>(null)

  const modes = allowedModes
    ? (Object.keys(modeConfig) as ViewMode[]).filter((mode) => allowedModes.includes(mode))
    : (Object.keys(modeConfig) as ViewMode[])

  useEffect(() => {
    if (allowedModes && !allowedModes.includes(viewMode)) {
      setViewMode(allowedModes[0] ?? 'grid')
    }
  }, [allowedModes, viewMode, setViewMode])

  useEffect(() => {
    if (!open) return
    function handleClick(event: MouseEvent) {
      const target = event.target as Node
      if (wrapRef.current?.contains(target)) return
      if (panelRef.current?.contains(target)) return
      setOpen(false)
    }
    document.addEventListener('mousedown', handleClick)
    return () => document.removeEventListener('mousedown', handleClick)
  }, [open])

  useEffect(() => {
    if (!open) return
    function handleKey(event: KeyboardEvent) {
      if (event.key === 'Escape') setOpen(false)
    }
    document.addEventListener('keydown', handleKey)
    return () => document.removeEventListener('keydown', handleKey)
  }, [open])

  const activeConfig = modeConfig[viewMode]
  const ActiveIcon = activeConfig.icon

  return (
    <div ref={wrapRef} className="relative" data-testid="view-mode-selector">
      <button
        ref={triggerRef}
        onClick={() => setOpen((prev) => !prev)}
        className={[
          'flex items-center gap-1.5 px-2.5 py-1.5 rounded-lg text-sm font-medium transition-all',
          'border border-border bg-background hover:border-primary/40',
          'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/70',
        ].join(' ')}
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-label={`View mode: ${activeConfig.label}`}
      >
        <ActiveIcon className="h-4 w-4 text-primary" />
        <span className="hidden sm:inline">{activeConfig.label}</span>
        <ChevronDown
          className={`h-3.5 w-3.5 text-muted-foreground transition-transform${open ? ' rotate-180' : ''}`}
        />
      </button>

      <AnchoredPortal
        open={open}
        anchorRef={triggerRef}
        panelRef={panelRef}
        placement="bottom-end"
        offset={4}
        className="min-w-[190px] rounded-lg border border-border/80 bg-card p-1 text-card-foreground shadow-elevated"
        role="listbox"
        aria-label="View mode options"
      >
        {modes.map((mode) => {
          const config = modeConfig[mode]
          const Icon = config.icon
          const isActive = viewMode === mode

          return (
            <button
              key={mode}
              onClick={() => {
                setViewMode(mode)
                setOpen(false)
              }}
              className={[
                'flex items-center gap-2.5 w-full px-2.5 py-2 rounded-md text-sm transition-colors',
                'hover:bg-muted/50 focus-visible:outline-none focus-visible:bg-muted/50',
                isActive ? 'text-primary font-medium bg-primary/5' : 'text-muted-foreground',
              ].join(' ')}
              role="option"
              aria-selected={isActive}
              aria-label={config.title}
              data-testid={`view-mode-${mode}`}
            >
              <Icon className="h-4 w-4 flex-shrink-0" />
              <span>{config.label}</span>
              {isActive ? (
                <Check className="h-3.5 w-3.5 ml-auto flex-shrink-0 text-primary" />
              ) : (
                <span className="ml-auto text-xs text-muted-foreground/60">{config.description}</span>
              )}
            </button>
          )
        })}
      </AnchoredPortal>
    </div>
  )
}
