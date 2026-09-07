import { describe, it, expect } from 'vitest'
import {
  primaryNav,
  overflowNav,
  filterNavItems,
  isActivePath,
  SUPER_ADMIN_ONLY_ADMIN_PATHS,
  type NavItem,
} from '../components/nav-config'
import { UserRole, type UserDto } from '../lib/types'

function makeUser(role: UserRole): UserDto {
  return {
    id: 'u',
    username: 'u',
    email: null,
    displayName: 'U',
    role,
    tenantId: 't',
    tenantName: 'T',
    isActive: true,
    apiKey: null,
    createdAt: '2026-01-01T00:00:00Z',
    lastLoginAt: null,
  }
}

describe('nav-config — VERIFY-L3 primary strip', () => {
  it('Should_AdvertiseKnowledgeChatSearch_AsPrimary', () => {
    expect(primaryNav.map((i) => i.path)).toEqual([
      '/knowledge',
      '/chat',
      '/search',
    ])
    expect(primaryNav.map((i) => i.testId)).toEqual([
      'knowledge',
      'chat',
      'search',
    ])
  })

  it('Should_KeepVaultsFilesInboxOrganize_InOverflow', () => {
    expect(overflowNav.map((i) => i.path)).toEqual([
      '/vaults',
      '/files',
      '/inbox',
      '/organize',
      '/sync',
    ])
  })

  it('Should_KeepAdminOutOfPrimaryAndOverflow', () => {
    expect([...primaryNav, ...overflowNav].some((i) => i.path === '/admin')).toBe(false)
  })

  it('Should_FreezePrimaryAndOverflowNav_AtModuleLevel', () => {
    expect(Object.isFrozen(primaryNav)).toBe(true)
    expect(Object.isFrozen(overflowNav)).toBe(true)
  })

  it('Should_NotHaveSettings_InPrimary', () => {
    expect(primaryNav.some((i) => i.path === '/settings')).toBe(false)
  })
})

describe('nav-config — VERIFY-L6 advertised routes', () => {
  it('Should_NotAdvertisePlatformOnlyRoutes', () => {
    const paths = [...primaryNav, ...overflowNav].map((i) => i.path)
    for (const banned of ['/superadmin', '/sites', '/billing', '/journal-qa']) {
      expect(paths).not.toContain(banned)
    }
  })
})

describe('nav-config — filterNavItems', () => {
  it('Should_ReturnEmptyArray_WhenUserIsNull', () => {
    expect(filterNavItems(primaryNav, null)).toEqual([])
  })

  it('Should_ReturnPrimaryItems_WhenUserRoleIsUser', () => {
    const visible = filterNavItems(primaryNav, makeUser(UserRole.User))
    expect(visible.map((i) => i.path)).toEqual(['/knowledge', '/chat', '/search'])
    for (const item of visible) {
      expect(item.adminOnly).not.toBe(true)
      expect(item.superAdminOnly).not.toBe(true)
    }
  })

  it('Should_ReturnIdenticalPrimaryPaths_WhenAdminVsSuperAdmin', () => {
    const a = filterNavItems(primaryNav, makeUser(UserRole.Admin)).map((i) => i.path)
    const s = filterNavItems(primaryNav, makeUser(UserRole.SuperAdmin)).map((i) => i.path)
    expect(a).toEqual(s)
  })

  it('Should_PreserveOverflowOrder', () => {
    const visible = filterNavItems(overflowNav, makeUser(UserRole.User))
    expect(visible.map((i) => i.path)).toEqual([
      '/vaults',
      '/files',
      '/inbox',
      '/organize',
      '/sync',
    ])
  })

  it('Should_Throw_WhenItemHasBothAdminOnlyAndSuperAdminOnly', () => {
    const bad: NavItem = {
      path: '/bad',
      label: 'Bad',
      icon: primaryNav[0].icon,
      adminOnly: true,
      superAdminOnly: true,
    }
    expect(() => filterNavItems([bad], makeUser(UserRole.SuperAdmin))).toThrow()
  })
})

describe('nav-config — isActivePath', () => {
  it('Should_MatchExactRoot_WhenEndTrue', () => {
    const root: NavItem = { path: '/', label: 'Dashboard', icon: primaryNav[0].icon, end: true }
    expect(isActivePath(root, '/')).toBe(true)
    expect(isActivePath(root, '/knowledge')).toBe(false)
  })

  it('Should_TreatRootAsKnowledgeHome', () => {
    const knowledge = primaryNav.find((i) => i.path === '/knowledge') as NavItem
    expect(isActivePath(knowledge, '/')).toBe(true)
    expect(isActivePath(knowledge, '/knowledge')).toBe(true)
    expect(isActivePath(knowledge, '/knowledge/new')).toBe(true)
  })

  it('Should_MatchPrefixWithSeparator_ByDefault', () => {
    const search = primaryNav.find((i) => i.path === '/search') as NavItem
    expect(isActivePath(search, '/search')).toBe(true)
    expect(isActivePath(search, '/search/foo')).toBe(true)
    expect(isActivePath(search, '/search-other')).toBe(false)
  })

  it('Should_NotCrossMatchKnowledgeAndVaults', () => {
    const knowledge = primaryNav.find((i) => i.path === '/knowledge') as NavItem
    const vaults = overflowNav.find((i) => i.path === '/vaults') as NavItem
    expect(isActivePath(knowledge, '/vaults')).toBe(false)
    expect(isActivePath(vaults, '/knowledge')).toBe(false)
    expect(isActivePath(knowledge, '/knowledge')).toBe(true)
    expect(isActivePath(vaults, '/vaults')).toBe(true)
  })
})

describe('nav-config — SUPER_ADMIN_ONLY_ADMIN_PATHS', () => {
  it('Should_ContainExactlyThreePaths', () => {
    expect(SUPER_ADMIN_ONLY_ADMIN_PATHS.size).toBe(3)
    expect(Array.from(SUPER_ADMIN_ONLY_ADMIN_PATHS).sort()).toEqual(
      ['/admin/settings', '/admin/sso', '/admin/tenants'].sort(),
    )
  })
})
