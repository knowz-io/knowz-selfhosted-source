import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import {
  Building2,
  ChevronDown,
  ClipboardList,
  LayoutDashboard,
  LogOut,
  Moon,
  Settings as SettingsIcon,
  Shield,
  Sun,
  Users,
  Wrench,
  type LucideIcon,
} from 'lucide-react'
import { useAuth } from '../lib/auth'
import { useTheme } from '../lib/theme'
import { UserRole } from '../lib/types'
import { SUPER_ADMIN_ONLY_ADMIN_PATHS } from './nav-config'
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

interface AdminShortcut {
  slug: string
  path: string
  label: string
  icon: LucideIcon
}

const ADMIN_SHORTCUTS: ReadonlyArray<AdminShortcut> = [
  { slug: 'overview', path: '/admin', label: 'Admin overview', icon: LayoutDashboard },
  { slug: 'users', path: '/admin/users', label: 'Users', icon: Users },
  { slug: 'audit-logs', path: '/admin/audit-logs', label: 'Audit Logs', icon: ClipboardList },
  { slug: 'tenants', path: '/admin/tenants', label: 'Tenants', icon: Building2 },
  { slug: 'sso', path: '/admin/sso', label: 'SSO', icon: Shield },
  { slug: 'settings', path: '/admin/settings', label: 'Configuration', icon: Wrench },
]

export default function UserMenu() {
  const { user, isAuthenticated, logout } = useAuth()
  const { theme, toggle: toggleTheme } = useTheme()
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

  if (!isAuthenticated || !user) return null

  const displayLabel = user.displayName || user.username
  const initial = (user.displayName ?? user.username ?? '?').charAt(0).toUpperCase()
  const badgeClass = roleBadgeStyles[user.role] ?? roleBadgeStyles[UserRole.User]
  const roleLabel = roleLabels[user.role] ?? 'User'
  const showAdminSection = user.role >= UserRole.Admin
  const isSuperAdmin = user.role === UserRole.SuperAdmin
  const visibleAdminShortcuts = ADMIN_SHORTCUTS.filter((shortcut) => {
    if (!showAdminSection) return false
    if (SUPER_ADMIN_ONLY_ADMIN_PATHS.has(shortcut.path)) return isSuperAdmin
    return true
  })

  return (
    <div className="relative shrink-0" ref={containerRef}>
      <button
        ref={triggerRef}
        type="button"
        data-testid="sh-user-menu"
        aria-label={open ? 'Close user menu' : 'Open user menu'}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls="sh-user-menu-dropdown"
        onClick={() => setOpen((prev) => !prev)}
        className="flex items-center gap-1 rounded-xl border border-border/70 bg-card/80 p-1.5 shadow-sm transition-colors hover:bg-card focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 sm:gap-2 sm:rounded-2xl sm:px-3 sm:py-2"
      >
        <div className="flex h-8 w-8 items-center justify-center rounded-xl bg-primary/10 text-sm font-bold text-primary sm:h-9 sm:w-9 sm:rounded-2xl">
          {initial}
        </div>
        <div className="hidden text-left xl:block">
          <p className="max-w-40 truncate text-sm font-medium">{displayLabel}</p>
          <p className="text-[11px] text-muted-foreground">{roleLabel}</p>
        </div>
        <ChevronDown size={14} className="hidden text-muted-foreground sm:block" />
      </button>

      <AnchoredPortal
        open={open}
        anchorRef={triggerRef}
        panelRef={panelRef}
        placement="bottom-end"
        offset={8}
        id="sh-user-menu-dropdown"
        data-testid="sh-user-menu-dropdown"
        role="menu"
        aria-orientation="vertical"
        className="w-64 rounded-3xl border border-border/80 bg-card py-1 text-card-foreground shadow-elevated animate-scale-in"
      >
        <div className="border-b border-border/60 px-4 py-3">
          <p className="truncate text-sm font-medium">{displayLabel}</p>
          <span
            className={`mt-1 inline-flex rounded px-1.5 py-0 text-[10px] font-medium ${badgeClass}`}
          >
            {roleLabel}
          </span>
          {user.tenantName && (
            <p className="mt-1 truncate text-xs text-muted-foreground">{user.tenantName}</p>
          )}
        </div>

        <Link
          to="/settings"
          role="menuitem"
          data-testid="sh-user-menu-settings"
          onClick={() => setOpen(false)}
          className="flex w-full items-center gap-2 px-4 py-2.5 text-sm transition-colors hover:bg-muted"
        >
          <SettingsIcon size={14} />
          Settings
        </Link>

        {showAdminSection && visibleAdminShortcuts.length > 0 && (
          <>
            <div className="mt-1 border-t border-border/60 px-4 pb-1 pt-2">
              <span className="text-[10px] font-semibold uppercase tracking-wider text-muted-foreground">
                Administration
              </span>
            </div>
            {visibleAdminShortcuts.map((shortcut) => {
              const Icon = shortcut.icon
              return (
                <Link
                  key={shortcut.path}
                  to={shortcut.path}
                  role="menuitem"
                  data-testid={`sh-user-menu-admin-${shortcut.slug}`}
                  onClick={() => setOpen(false)}
                  className="flex w-full items-center gap-2 px-4 py-2 text-sm transition-colors hover:bg-muted"
                >
                  <Icon size={14} />
                  {shortcut.label}
                </Link>
              )
            })}
          </>
        )}

        <button
          type="button"
          role="menuitem"
          data-testid="sh-user-menu-theme-toggle"
          aria-label={theme === 'dark' ? 'Switch to light mode' : 'Switch to dark mode'}
          onClick={toggleTheme}
          className="mt-1 flex w-full items-center gap-2 border-t border-border/60 px-4 py-2.5 text-sm transition-colors hover:bg-muted"
        >
          {theme === 'dark' ? <Sun size={14} /> : <Moon size={14} />}
          {theme === 'dark' ? 'Light mode' : 'Dark mode'}
        </button>

        <button
          type="button"
          role="menuitem"
          data-testid="sh-user-menu-logout"
          onClick={() => {
            setOpen(false)
            logout()
          }}
          className="flex w-full items-center gap-2 border-t border-border/60 px-4 py-2.5 text-sm text-red-600 transition-colors hover:bg-red-50 dark:hover:bg-red-900/20"
        >
          <LogOut size={14} />
          Sign out
        </button>
      </AnchoredPortal>
    </div>
  )
}
