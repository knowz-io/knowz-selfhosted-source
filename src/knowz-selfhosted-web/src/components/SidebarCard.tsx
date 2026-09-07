import { useState } from 'react'
import { ChevronDown, ChevronRight } from 'lucide-react'

interface SidebarCardProps {
  title: string
  icon: React.ReactNode
  count?: number
  defaultOpen?: boolean
  children: React.ReactNode
}

function getStorage(): Storage | null {
  try {
    const storage = globalThis.localStorage
    if (
      storage &&
      typeof storage.getItem === 'function' &&
      typeof storage.setItem === 'function'
    ) {
      return storage
    }
  } catch {
    // Ignore storage access errors in tests or restricted environments.
  }
  return null
}

export default function SidebarCard({
  title,
  icon,
  count,
  defaultOpen = true,
  children,
}: SidebarCardProps) {
  const storageKey = `sidebar-card-${title}`
  const [open, setOpen] = useState(() => {
    const stored = getStorage()?.getItem(storageKey)
    if (stored === 'true') return true
    if (stored === 'false') return false
    return defaultOpen
  })

  const toggle = () => {
    const next = !open
    setOpen(next)
    getStorage()?.setItem(storageKey, String(next))
  }

  return (
    <div className="border border-border/60 rounded-lg overflow-hidden">
      <button
        onClick={toggle}
        className="w-full flex items-center gap-2 px-3 py-2 hover:bg-muted/50 transition-colors text-left"
      >
        <span className="flex items-center justify-center w-6 h-6 rounded bg-primary/10 text-primary flex-shrink-0">
          {icon}
        </span>
        <span className="text-[11px] font-semibold flex-1 truncate">{title}</span>
        {count !== undefined && count > 0 && (
          <span className="px-1.5 py-0.5 text-[10px] font-medium bg-muted rounded-full text-muted-foreground">
            {count}
          </span>
        )}
        {open ? (
          <ChevronDown size={14} className="text-muted-foreground flex-shrink-0" />
        ) : (
          <ChevronRight size={14} className="text-muted-foreground flex-shrink-0" />
        )}
      </button>
      {open && (
        <div className="px-3 pb-3 pt-1 animate-fade-in">
          {children}
        </div>
      )}
    </div>
  )
}
