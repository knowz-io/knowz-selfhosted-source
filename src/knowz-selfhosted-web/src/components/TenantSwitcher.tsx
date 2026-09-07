import { useEffect, useRef, useState } from 'react'
import { ArrowLeftRight, ChevronDown, Loader2 } from 'lucide-react'
import { useQueryClient } from '@tanstack/react-query'
import { useAuth } from '../lib/auth'
import { UserRole } from '../lib/types'
import { AnchoredPortal } from './ui/AnchoredPortal'

const roleLabels: Record<number, string> = {
  [UserRole.SuperAdmin]: 'SuperAdmin',
  [UserRole.Admin]: 'Admin',
  [UserRole.User]: 'User',
}

const roleBadgeStyles: Record<number, string> = {
  [UserRole.SuperAdmin]: 'bg-purple-100 dark:bg-purple-950/40 text-purple-700 dark:text-purple-400',
  [UserRole.Admin]: 'bg-blue-100 dark:bg-blue-950/40 text-blue-700 dark:text-blue-400',
  [UserRole.User]: 'bg-muted text-muted-foreground',
}

export default function TenantSwitcher() {
  const { user, availableTenants, switchTenant } = useAuth()
  const queryClient = useQueryClient()
  const [switching, setSwitching] = useState(false)
  const [open, setOpen] = useState(false)
  const containerRef = useRef<HTMLDivElement>(null)
  const triggerRef = useRef<HTMLButtonElement>(null)
  const panelRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return
    function handleClickOutside(event: MouseEvent) {
      const target = event.target as Node
      if (containerRef.current?.contains(target)) return
      if (panelRef.current?.contains(target)) return
      setOpen(false)
    }
    function handleKey(event: KeyboardEvent) {
      if (event.key === 'Escape') setOpen(false)
    }
    document.addEventListener('mousedown', handleClickOutside)
    document.addEventListener('keydown', handleKey)
    return () => {
      document.removeEventListener('mousedown', handleClickOutside)
      document.removeEventListener('keydown', handleKey)
    }
  }, [open])

  if (!user || !availableTenants || availableTenants.length < 2) return null

  const currentTenant = availableTenants.find((tenant) => tenant.tenantId === user.tenantId)
  const otherTenants = availableTenants.filter((tenant) => tenant.tenantId !== user.tenantId)
  const currentName = currentTenant?.tenantName ?? user.tenantName ?? 'Unknown'
  const currentRole = currentTenant?.role ?? user.role

  const handleSwitch = async (tenantId: string) => {
    setSwitching(true)
    try {
      await switchTenant(tenantId)
      queryClient.clear()
      setOpen(false)
    } catch (err) {
      console.error('Failed to switch tenant:', err)
    } finally {
      setSwitching(false)
    }
  }

  return (
    <div className="relative shrink-0" ref={containerRef}>
      <button
        ref={triggerRef}
        type="button"
        data-testid="sh-tenant-switcher"
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls="sh-tenant-switcher-menu"
        onClick={() => setOpen((prev) => !prev)}
        disabled={switching}
        className="flex items-center gap-2 rounded-2xl border border-border/70 bg-card/80 px-3 py-2 shadow-sm transition-colors hover:bg-card focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2"
      >
        <ArrowLeftRight size={14} className="text-muted-foreground" />
        <div className="hidden min-w-0 text-left sm:block">
          <p className="max-w-40 truncate text-sm font-medium">{currentName}</p>
          <span
            className={`inline-flex rounded px-1 py-0 t-meta font-medium ${
              roleBadgeStyles[currentRole] ?? roleBadgeStyles[UserRole.User]
            }`}
          >
            {roleLabels[currentRole] ?? 'User'}
          </span>
        </div>
        {switching ? (
          <Loader2 size={12} className="animate-spin text-muted-foreground" />
        ) : (
          <ChevronDown size={12} className="text-muted-foreground" />
        )}
      </button>

      <AnchoredPortal
        open={open && !switching}
        anchorRef={triggerRef}
        panelRef={panelRef}
        placement="bottom-end"
        offset={8}
        id="sh-tenant-switcher-menu"
        data-testid="sh-tenant-switcher-menu"
        role="menu"
        aria-orientation="vertical"
        className="w-56 overflow-hidden rounded-2xl border border-border/80 bg-card text-card-foreground shadow-elevated"
      >
        {otherTenants.map((tenant) => (
          <button
            key={tenant.tenantId}
            type="button"
            role="menuitem"
            data-testid={`sh-tenant-switcher-option-${tenant.tenantId}`}
            onClick={() => handleSwitch(tenant.tenantId)}
            className="flex w-full items-center justify-between border-b border-border/50 px-3 py-2 text-left text-xs transition-colors last:border-b-0 hover:bg-muted"
          >
            <div className="min-w-0 flex-1">
              <p className="truncate text-sm font-medium">{tenant.tenantName}</p>
              <span
                className={`inline-flex rounded px-1 py-0 t-meta font-medium ${
                  roleBadgeStyles[tenant.role] ?? roleBadgeStyles[UserRole.User]
                }`}
              >
                {roleLabels[tenant.role] ?? 'User'}
              </span>
            </div>
          </button>
        ))}
      </AnchoredPortal>
    </div>
  )
}
