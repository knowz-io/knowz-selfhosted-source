import type { LucideIcon } from 'lucide-react'
import {
  BookOpen,
  Archive,
  FileText,
  Inbox,
  Search,
  Layers,
  MessagesSquare,
  Cloud,
} from 'lucide-react'
import { UserRole, type UserDto } from '../lib/types'

export interface NavItem {
  path: string
  label: string
  icon: LucideIcon
  end?: boolean
  adminOnly?: boolean
  superAdminOnly?: boolean
  testId?: string
}

export const primaryNav: ReadonlyArray<NavItem> = Object.freeze<NavItem[]>([
  { path: '/knowledge', label: 'Knowledge', icon: BookOpen, testId: 'knowledge' },
  { path: '/chat', label: 'Chat', icon: MessagesSquare, testId: 'chat' },
  { path: '/search', label: 'Search', icon: Search, testId: 'search' },
])

export const overflowNav: ReadonlyArray<NavItem> = Object.freeze<NavItem[]>([
  { path: '/vaults', label: 'Vaults', icon: Archive, testId: 'vaults' },
  { path: '/files', label: 'Files', icon: FileText, testId: 'files' },
  { path: '/inbox', label: 'Inbox', icon: Inbox, testId: 'inbox' },
  { path: '/organize', label: 'Organize', icon: Layers, testId: 'organize' },
  { path: '/sync', label: 'Destinations', icon: Cloud, testId: 'sync' },
])

export const SUPER_ADMIN_ONLY_ADMIN_PATHS: ReadonlySet<string> = new Set([
  '/admin/tenants',
  '/admin/sso',
  '/admin/settings',
])

function itemVisibleTo(
  user: UserDto,
  item: Pick<NavItem, 'adminOnly' | 'superAdminOnly'>,
): boolean {
  if (item.adminOnly && item.superAdminOnly) {
    throw new Error('NavItem cannot declare both adminOnly and superAdminOnly')
  }
  if (item.superAdminOnly) return user.role === UserRole.SuperAdmin
  if (item.adminOnly) return user.role >= UserRole.Admin
  return true
}

export function filterNavItems(
  items: ReadonlyArray<NavItem>,
  user: UserDto | null,
): NavItem[] {
  if (!user) return []
  const result: NavItem[] = []
  for (const item of items) {
    if (!itemVisibleTo(user, item)) continue
    result.push(item)
  }
  return result
}

export function isActivePath(item: NavItem, pathname: string): boolean {
  if (item.end) return pathname === item.path
  if (item.path === '/') return pathname === '/'
  if (item.path === '/knowledge' && pathname === '/') return true
  return pathname === item.path || pathname.startsWith(item.path + '/')
}

/**
 * SH_InstanceCopyHygiene R1 — the full nav surface, used by tests that assert a
 * single user-visible name per feature across nav / page-meta / heading.
 */
export const NAV_ITEMS: ReadonlyArray<NavItem> = Object.freeze([...primaryNav, ...overflowNav])
