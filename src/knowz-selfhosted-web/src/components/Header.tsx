import { useEffect, useRef, useState } from 'react'
import { Link, NavLink, useLocation } from 'react-router-dom'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import {
  ArrowLeftRight,
  Brain,
  Building2,
  Menu,
  MoreHorizontal,
  Plus,
  X,
} from 'lucide-react'
import { useAuth } from '../lib/auth'
import { api } from '../lib/api-client'
import { UserRole } from '../lib/types'
import TenantSwitcher from './TenantSwitcher'
import UserMenu from './UserMenu'
import {
  filterNavItems,
  isActivePath,
  overflowNav,
  primaryNav,
  type NavItem,
} from './nav-config'
import { getPageMeta, getDocumentTitle, isPageMetaSuppressed } from './page-meta-config'
import { AnchoredPortal } from './ui/AnchoredPortal'

function defaultSlug(path: string): string {
  if (path === '/') return 'root'
  return path.replace(/^\//, '').replace(/\//g, '-')
}

function testIdFor(item: NavItem): string {
  return `nav-link-${item.testId ?? defaultSlug(item.path)}`
}

function topNavClass(active: boolean): string {
  return [
    'inline-flex items-center gap-1.5 rounded-lg px-2.5 py-1.5 t-label transition-colors',
    'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2',
    active
      ? 'bg-primary/10 text-primary'
      : 'text-muted-foreground hover:bg-muted hover:text-foreground',
  ].join(' ')
}

function mobileNavClass(active: boolean): string {
  return [
    'flex items-center gap-3 rounded-lg px-3 py-2.5 t-label transition-colors',
    active
      ? 'bg-primary/10 text-primary'
      : 'text-muted-foreground hover:bg-muted hover:text-foreground',
  ].join(' ')
}

export default function Header() {
  const location = useLocation()
  const pathname = location.pathname
  const { user, isAuthenticated, activeTenantId, setActiveTenantId } = useAuth()
  const isSuperAdmin = user?.role === UserRole.SuperAdmin
  const queryClient = useQueryClient()
  const [mobileMenuOpen, setMobileMenuOpen] = useState(false)
  const [moreOpen, setMoreOpen] = useState(false)
  const drawerRef = useRef<HTMLDivElement>(null)
  const hamburgerRef = useRef<HTMLButtonElement>(null)
  const moreBtnRef = useRef<HTMLButtonElement>(null)
  const morePanelRef = useRef<HTMLDivElement>(null)

  const visibleNavItems: NavItem[] = filterNavItems(primaryNav, user ?? null)
  const overflowItems: NavItem[] = filterNavItems(overflowNav, user ?? null)
  const overflowActive = overflowItems.some((item) => isActivePath(item, pathname))
  const mobileNavItems: NavItem[] = [...visibleNavItems, ...overflowItems]
  const pageMeta = getPageMeta(pathname)
  // SH_InstanceCopyHygiene R3: suppression is enumerated centrally in
  // page-meta-config, not decided ad hoc per page.
  const showPageMeta = !isPageMetaSuppressed(pathname)

  // SH_InstanceCopyHygiene R9: per-route document.title.
  useEffect(() => {
    document.title = getDocumentTitle(pathname)
  }, [pathname])

  const { data: tenants } = useQuery({
    queryKey: ['admin', 'tenants-header'],
    queryFn: () => api.listTenants(),
    enabled: isSuperAdmin,
    staleTime: 60_000,
  })

  // SH_InstanceCopyHygiene R14: single-tenant chrome is a VISIBILITY gate.
  // Routes stay registered (SelfHostedShellParity route-stability rule); only
  // the cross-tenant affordances are hidden when there is nothing to switch to.
  const isMultiTenant = (tenants?.length ?? 0) > 1

  const activeTenantName = activeTenantId
    ? tenants?.find((t) => t.id === activeTenantId)?.name ?? 'Loading...'
    : null

  useEffect(() => {
    setMobileMenuOpen(false)
    setMoreOpen(false)
  }, [pathname])

  useEffect(() => {
    if (!mobileMenuOpen) return
    function handleKey(event: KeyboardEvent) {
      if (event.key === 'Escape') setMobileMenuOpen(false)
    }
    function handleClickOutside(event: MouseEvent) {
      const target = event.target as Node
      if (
        drawerRef.current &&
        !drawerRef.current.contains(target) &&
        hamburgerRef.current &&
        !hamburgerRef.current.contains(target)
      ) {
        setMobileMenuOpen(false)
      }
    }
    document.addEventListener('keydown', handleKey)
    document.addEventListener('mousedown', handleClickOutside)
    return () => {
      document.removeEventListener('keydown', handleKey)
      document.removeEventListener('mousedown', handleClickOutside)
    }
  }, [mobileMenuOpen])

  useEffect(() => {
    if (!moreOpen) return
    function handleKey(event: KeyboardEvent) {
      if (event.key === 'Escape') setMoreOpen(false)
    }
    function handleClickOutside(event: MouseEvent) {
      const target = event.target as Node
      if (moreBtnRef.current?.contains(target)) return
      if (morePanelRef.current?.contains(target)) return
      setMoreOpen(false)
    }
    document.addEventListener('keydown', handleKey)
    document.addEventListener('mousedown', handleClickOutside)
    return () => {
      document.removeEventListener('keydown', handleKey)
      document.removeEventListener('mousedown', handleClickOutside)
    }
  }, [moreOpen])

  const handleTenantChange = (event: React.ChangeEvent<HTMLSelectElement>) => {
    const value = event.target.value || null
    setActiveTenantId(value)
    queryClient.invalidateQueries()
  }

  return (
    <header
      data-testid="sh-header"
      role="banner"
      className="sticky top-0 z-header border-b border-border bg-background"
    >
      <div className="mx-auto max-w-7xl px-2 sm:px-6 lg:px-8">
        <div
          data-testid="sh-header-nav-row"
          className="flex h-[3.75rem] items-center justify-between gap-2"
        >
          <div className="flex min-w-0 items-center gap-2 lg:gap-8">
            <div className="flex min-w-0 items-center gap-2 sm:gap-3">
              <button
                ref={hamburgerRef}
                type="button"
                data-testid="sh-mobile-hamburger"
                aria-label={mobileMenuOpen ? 'Close menu' : 'Open menu'}
                aria-expanded={mobileMenuOpen}
                aria-controls="sh-mobile-drawer"
                onClick={() => setMobileMenuOpen((prev) => !prev)}
                className="inline-flex items-center justify-center rounded-md border border-border bg-card p-2 text-muted-foreground transition-colors hover:bg-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 lg:hidden"
              >
                {mobileMenuOpen ? <X size={20} /> : <Menu size={20} />}
              </button>
              <Link
                to="/"
                data-testid="sh-logo-link"
                className="group inline-flex items-center gap-2 shrink-0"
              >
                <div className="hidden h-8 w-8 items-center justify-center rounded-md bg-primary shadow-elev-1 sm:flex">
                  <Brain className="h-[18px] w-[18px] text-primary-foreground" data-testid="sh-logo-brain-icon" />
                </div>
                <span
                  data-testid="sh-logo-wordmark"
                  className="font-logo text-2xl font-normal tracking-wide text-foreground sm:text-3xl"
                  style={{
                    fontFamily: "'Pacifico', cursive",
                    WebkitFontSmoothing: 'antialiased',
                    MozOsxFontSmoothing: 'grayscale',
                  }}
                >
                  knowz
                </span>
              </Link>
              {isSuperAdmin && isMultiTenant && activeTenantId && activeTenantName && (
                <span
                  data-testid="sh-superadmin-viewing-pill"
                  className="inline-flex items-center gap-1 rounded-full border border-purple-500/40 bg-purple-50 px-2.5 py-1 text-[10px] font-medium text-purple-700 dark:bg-purple-950/30 dark:text-purple-300"
                >
                  <Building2 size={11} />
                  Viewing: {activeTenantName}
                </span>
              )}
            </div>

            <nav
              aria-label="Primary"
              data-testid="sh-nav-primary"
              className="hidden min-w-0 lg:flex"
            >
              <ul className="flex flex-nowrap items-center gap-0.5">
                {visibleNavItems.map((item) => {
                  const Icon = item.icon
                  const active = isActivePath(item, pathname)
                  const testId = testIdFor(item)
                  return (
                    <li key={item.path}>
                      <NavLink
                        to={item.path}
                        end={item.end}
                        data-testid={testId}
                        className={() => topNavClass(active)}
                        aria-current={active ? 'page' : undefined}
                      >
                        <Icon size={16} />
                        <span>{item.label}</span>
                      </NavLink>
                    </li>
                  )
                })}
                {overflowItems.length > 0 && (
                  <li>
                    <button
                      ref={moreBtnRef}
                      type="button"
                      data-testid="nav-link-more"
                      aria-haspopup="menu"
                      aria-expanded={moreOpen}
                      onClick={() => setMoreOpen((prev) => !prev)}
                      className={topNavClass(overflowActive || moreOpen)}
                    >
                      <MoreHorizontal size={16} />
                      <span>More</span>
                    </button>
                    <AnchoredPortal
                      open={moreOpen}
                      anchorRef={moreBtnRef}
                      panelRef={morePanelRef}
                      placement="bottom-start"
                      offset={8}
                      data-testid="sh-nav-more-menu"
                      role="menu"
                      className="min-w-44 rounded-lg border border-border bg-card py-1 text-card-foreground shadow-elev-2"
                    >
                      {overflowItems.map((item) => {
                        const Icon = item.icon
                        const active = isActivePath(item, pathname)
                        return (
                          <NavLink
                            key={item.path}
                            to={item.path}
                            end={item.end}
                            role="menuitem"
                            data-testid={testIdFor(item)}
                            aria-current={active ? 'page' : undefined}
                            onClick={() => setMoreOpen(false)}
                            className={`flex items-center gap-2 px-3 py-2 t-label transition-colors ${
                              active
                                ? 'bg-primary/10 text-primary'
                                : 'text-foreground hover:bg-muted'
                            }`}
                          >
                            <Icon size={16} />
                            <span>{item.label}</span>
                          </NavLink>
                        )
                      })}
                    </AnchoredPortal>
                  </li>
                )}
              </ul>
            </nav>
          </div>

          <div className="flex shrink-0 items-center gap-1 sm:gap-2">
            <Link
              to="/knowledge/new"
              className="inline-flex min-h-10 min-w-10 items-center justify-center gap-2 rounded-lg bg-primary px-2 py-2 t-label font-semibold text-primary-foreground shadow-elev-1 transition-colors hover:brightness-110 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 sm:px-3"
              aria-label="New Knowledge"
            >
              <Plus size={15} />
              <span className="hidden lg:inline">New Knowledge</span>
            </Link>

            <TenantSwitcher />

            {isSuperAdmin && tenants && isMultiTenant && (
              <div className="hidden items-center gap-1 xl:inline-flex">
                <ArrowLeftRight size={12} className="text-purple-500" />
                <select
                  data-testid="sh-superadmin-tenant-select"
                  aria-label="Cross-tenant viewing context"
                  value={activeTenantId ?? ''}
                  onChange={handleTenantChange}
                  className="rounded-md border border-border bg-card px-2 py-1 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2"
                >
                  <option value="">My Tenant ({user?.tenantName ?? 'Default'})</option>
                  {tenants.map((t) => (
                    <option key={t.id} value={t.id}>
                      {t.name} {t.id === user?.tenantId ? '(yours)' : ''}
                    </option>
                  ))}
                </select>
              </div>
            )}

            {isAuthenticated && <UserMenu />}
          </div>
        </div>

        {showPageMeta && (
          <div data-testid="sh-pagemeta" className="border-t border-border py-2">
            <div className="min-w-0">
              <h1
                data-testid="sh-pagemeta-title"
                className="truncate t-title tracking-tight"
              >
                {pageMeta.title}
              </h1>
              <p
                data-testid="sh-pagemeta-description"
                className="hidden max-w-2xl truncate t-meta text-muted-foreground sm:block"
              >
                {pageMeta.description}
              </p>
            </div>
          </div>
        )}
      </div>

      {mobileMenuOpen && (
        <nav
          ref={drawerRef}
          id="sh-mobile-drawer"
          data-testid="sh-mobile-drawer"
          aria-label="Mobile"
          aria-hidden={!mobileMenuOpen}
          className="border-t border-border bg-background py-3 lg:hidden"
        >
          <div className="mx-auto max-w-7xl px-4 sm:px-6">
            <ul className="flex flex-col gap-1">
              {mobileNavItems.map((item) => {
                const Icon = item.icon
                const active = isActivePath(item, pathname)
                const testId = `${testIdFor(item)}-mobile`
                return (
                  <li key={item.path}>
                    <NavLink
                      to={item.path}
                      end={item.end}
                      data-testid={testId}
                      aria-current={active ? 'page' : undefined}
                      className={() => mobileNavClass(active)}
                    >
                      <Icon size={18} />
                      <span>{item.label}</span>
                    </NavLink>
                  </li>
                )
              })}
            </ul>
          </div>
        </nav>
      )}
    </header>
  )
}
