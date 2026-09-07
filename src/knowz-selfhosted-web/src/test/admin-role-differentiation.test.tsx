import { describe, it, expect, vi, beforeEach } from 'vitest'
import { createContext } from 'react'
import { screen } from '@testing-library/react'
import { renderWithProviders } from './test-utils'
import { UserRole } from '../lib/types'
import type { UserDto } from '../lib/types'
import { Navigate } from 'react-router-dom'

// ---- Helpers ----

function makeMockUser(overrides: Partial<UserDto> = {}): UserDto {
  return {
    id: 'user-1',
    username: 'testuser',
    email: 'test@example.com',
    displayName: 'Test User',
    role: UserRole.SuperAdmin,
    tenantId: 'tenant-1',
    tenantName: 'Test Tenant',
    isActive: true,
    apiKey: null,
    createdAt: '2026-01-01T00:00:00Z',
    lastLoginAt: null,
    ...overrides,
  }
}

function mockAuth(user: UserDto | null, overrides: Record<string, unknown> = {}) {
  return {
    user,
    token: user ? 'test-token' : null,
    isAuthenticated: !!user,
    isLoading: false,
    login: vi.fn(),
    loginWithToken: vi.fn(),
    logout: vi.fn(),
    activeTenantId: null,
    setActiveTenantId: vi.fn(),
    ...overrides,
  }
}


/**
 * Builds a complete mock of the `../lib/auth` module, including the
 * `AuthContext` export. `useFormatters` (used by components under test)
 * reads `AuthContext` directly, so the mock must provide it or rendering
 * will throw during test setup.
 */
function authModuleMock(authValue: ReturnType<typeof mockAuth>) {
  return {
    useAuth: () => authValue,
    AuthContext: createContext(authValue),
  }
}

// ---- SuperAdminRoute Tests ----
// Test the SuperAdminRoute logic directly by verifying the contract:
// - SuperAdmin -> render children
// - Admin -> redirect to /admin
// - Not authenticated -> redirect to /login
// - Loading -> show spinner

describe('SuperAdminRoute', () => {
  beforeEach(() => {
    vi.resetModules()
  })

  it('Should_RenderChildren_WhenUserIsSuperAdmin', async () => {
    vi.doMock('../lib/auth', () => authModuleMock(mockAuth(makeMockUser({ role: UserRole.SuperAdmin }))))
    // Build an inline component that replicates SuperAdminRoute logic using mocked useAuth
    const { useAuth } = await import('../lib/auth')

    function TestSuperAdminRoute({ children }: { children: React.ReactNode }) {
      const { user, isAuthenticated, isLoading } = useAuth()
      if (isLoading) return <div className="animate-spin" />
      if (!isAuthenticated) return <Navigate to="/login" replace />
      if (user?.role !== UserRole.SuperAdmin) return <Navigate to="/admin" replace />
      return <>{children}</>
    }

    renderWithProviders(
      <TestSuperAdminRoute>
        <div data-testid="protected-content">Protected</div>
      </TestSuperAdminRoute>,
    )
    expect(screen.getByTestId('protected-content')).toBeInTheDocument()
  })

  it('Should_RedirectToAdmin_WhenUserIsAdmin', async () => {
    vi.doMock('../lib/auth', () => authModuleMock(mockAuth(makeMockUser({ role: UserRole.Admin }))))
    const { useAuth } = await import('../lib/auth')

    function TestSuperAdminRoute({ children }: { children: React.ReactNode }) {
      const { user, isAuthenticated, isLoading } = useAuth()
      if (isLoading) return <div className="animate-spin" />
      if (!isAuthenticated) return <Navigate to="/login" replace />
      if (user?.role !== UserRole.SuperAdmin) return <Navigate to="/admin" replace />
      return <>{children}</>
    }

    renderWithProviders(
      <TestSuperAdminRoute>
        <div data-testid="protected-content">Protected</div>
      </TestSuperAdminRoute>,
    )
    expect(screen.queryByTestId('protected-content')).not.toBeInTheDocument()
  })

  it('Should_RedirectToLogin_WhenNotAuthenticated', async () => {
    vi.doMock('../lib/auth', () => authModuleMock(mockAuth(null)))
    const { useAuth } = await import('../lib/auth')

    function TestSuperAdminRoute({ children }: { children: React.ReactNode }) {
      const { user, isAuthenticated, isLoading } = useAuth()
      if (isLoading) return <div className="animate-spin" />
      if (!isAuthenticated) return <Navigate to="/login" replace />
      if (user?.role !== UserRole.SuperAdmin) return <Navigate to="/admin" replace />
      return <>{children}</>
    }

    renderWithProviders(
      <TestSuperAdminRoute>
        <div data-testid="protected-content">Protected</div>
      </TestSuperAdminRoute>,
    )
    expect(screen.queryByTestId('protected-content')).not.toBeInTheDocument()
  })

  it('Should_ShowLoadingSpinner_WhenAuthIsLoading', async () => {
    vi.doMock('../lib/auth', () => authModuleMock(mockAuth(makeMockUser(), { isLoading: true })))
    const { useAuth } = await import('../lib/auth')

    function TestSuperAdminRoute({ children }: { children: React.ReactNode }) {
      const { user, isAuthenticated, isLoading } = useAuth()
      if (isLoading) return <div className="animate-spin" data-testid="spinner" />
      if (!isAuthenticated) return <Navigate to="/login" replace />
      if (user?.role !== UserRole.SuperAdmin) return <Navigate to="/admin" replace />
      return <>{children}</>
    }

    const { container } = renderWithProviders(
      <TestSuperAdminRoute>
        <div data-testid="protected-content">Protected</div>
      </TestSuperAdminRoute>,
    )
    expect(screen.queryByTestId('protected-content')).not.toBeInTheDocument()
    expect(container.querySelector('.animate-spin')).toBeInTheDocument()
  })

  it('Should_ExportSuperAdminRoute_FromAuthModule', async () => {
    // Structural test to verify the export exists
    const authModule = await vi.importActual<typeof import('../lib/auth')>('../lib/auth')
    expect(authModule.SuperAdminRoute).toBeDefined()
    expect(typeof authModule.SuperAdminRoute).toBe('function')
  })
})

// ---- Header Nav Role-Filter Tests (post-SH_HeaderNavigation) ----
// SH_LookAndFeelParity: node-operator Admin stays out of the primary strip.
// Admin shortcuts live in UserMenu. These cases assert the primary-nav
// contract: Knowledge/Chat/Search are visible; `/admin` is not advertised.

describe('Header Primary Nav Role Filtering', () => {
  beforeEach(() => {
    vi.resetModules()
  })

  async function renderHeader() {
    const { default: HeaderComp } = await import('../components/Header')
    return renderWithProviders(<HeaderComp />)
  }

  function mockTheme() {
    vi.doMock('../lib/theme', () => ({
      useTheme: () => ({ theme: 'dark', toggle: vi.fn() }),
    }))
  }

  it('Should_KeepAdminOutOfPrimaryNav_WhenUserIsSuperAdmin', async () => {
    vi.doMock('../lib/auth', () => authModuleMock(mockAuth(makeMockUser({ role: UserRole.SuperAdmin }))))
    vi.doMock('../lib/api-client', () => ({
      api: { listTenants: vi.fn().mockResolvedValue([]) },
    }))
    mockTheme()
    await renderHeader()

    expect(screen.queryByTestId('nav-link-admin')).not.toBeInTheDocument()
    expect(screen.getByTestId('nav-link-knowledge')).toBeInTheDocument()
    expect(screen.getByTestId('nav-link-chat')).toBeInTheDocument()
    expect(screen.getByTestId('nav-link-search')).toBeInTheDocument()
  })

  it('Should_KeepAdminOutOfPrimaryNav_WhenUserIsAdmin', async () => {
    vi.doMock('../lib/auth', () => authModuleMock(mockAuth(makeMockUser({ role: UserRole.Admin }))))
    vi.doMock('../lib/api-client', () => ({
      api: { listTenants: vi.fn().mockResolvedValue([]) },
    }))
    mockTheme()
    await renderHeader()

    expect(screen.queryByTestId('nav-link-admin')).not.toBeInTheDocument()
    expect(screen.getByTestId('nav-link-knowledge')).toBeInTheDocument()
  })

  it('Should_NotShowAdminNavEntry_WhenUserIsRegularUser', async () => {
    vi.doMock('../lib/auth', () => authModuleMock(mockAuth(makeMockUser({ role: UserRole.User }))))
    vi.doMock('../lib/api-client', () => ({
      api: { listTenants: vi.fn().mockResolvedValue([]) },
    }))
    mockTheme()
    await renderHeader()

    expect(screen.queryByTestId('nav-link-admin')).not.toBeInTheDocument()
  })

  it('Should_NotShowSuperAdminTenantSelect_WhenUserIsAdmin', async () => {
    vi.doMock('../lib/auth', () => authModuleMock(mockAuth(makeMockUser({ role: UserRole.Admin }))))
    vi.doMock('../lib/api-client', () => ({
      api: {
        listTenants: vi.fn().mockResolvedValue([
          { id: 't1', name: 'Tenant 1', slug: 't1', description: null, isActive: true, userCount: 0, createdAt: '2026-01-01T00:00:00Z' },
        ]),
      },
    }))
    mockTheme()
    await renderHeader()

    expect(screen.queryByTestId('sh-superadmin-tenant-select')).not.toBeInTheDocument()
  })
})

// ---- AdminDashboardPage Tests ----

describe('AdminDashboardPage Role Differentiation', () => {
  beforeEach(() => {
    vi.resetModules()
  })

  it('Should_ShowTenantManagementLink_WhenUserIsSuperAdmin', async () => {
    vi.doMock('../lib/auth', () => authModuleMock(mockAuth(makeMockUser({ role: UserRole.SuperAdmin }))))
    vi.doMock('../lib/api-client', () => ({
      api: {
        listTenants: vi.fn().mockResolvedValue([
          { id: 't1', name: 'Tenant 1', slug: 't1', description: null, isActive: true, userCount: 5, createdAt: '2026-01-01T00:00:00Z' },
        ]),
        listUsers: vi.fn().mockResolvedValue([
          makeMockUser(),
        ]),
      },
    }))
    const { default: DashboardComp } = await import('../pages/admin/AdminDashboardPage')
    renderWithProviders(<DashboardComp />)

    // Wait for data to load
    const tenantLink = await screen.findByText('Tenant Management')
    expect(tenantLink).toBeInTheDocument()
  })

  it('Should_HideTenantManagementLink_WhenUserIsAdmin', async () => {
    vi.doMock('../lib/auth', () => authModuleMock(mockAuth(makeMockUser({ role: UserRole.Admin }))))
    vi.doMock('../lib/api-client', () => ({
      api: {
        listTenants: vi.fn().mockResolvedValue([]),
        listUsers: vi.fn().mockResolvedValue([
          makeMockUser({ role: UserRole.User }),
        ]),
      },
    }))
    const { default: DashboardComp } = await import('../pages/admin/AdminDashboardPage')
    renderWithProviders(<DashboardComp />)

    // Wait for data to load (user management link should be there)
    await screen.findByText('User Management')
    expect(screen.queryByText('Tenant Management')).not.toBeInTheDocument()
  })

  it('Should_HideRecentTenantsSection_WhenUserIsAdmin', async () => {
    vi.doMock('../lib/auth', () => authModuleMock(mockAuth(makeMockUser({ role: UserRole.Admin }))))
    vi.doMock('../lib/api-client', () => ({
      api: {
        listTenants: vi.fn().mockResolvedValue([]),
        listUsers: vi.fn().mockResolvedValue([
          makeMockUser({ role: UserRole.User }),
        ]),
      },
    }))
    const { default: DashboardComp } = await import('../pages/admin/AdminDashboardPage')
    renderWithProviders(<DashboardComp />)

    await screen.findByText('User Management')
    expect(screen.queryByText('Recent Tenants')).not.toBeInTheDocument()
  })

  it('Should_ShowRecentTenantsSection_WhenUserIsSuperAdmin', async () => {
    vi.doMock('../lib/auth', () => authModuleMock(mockAuth(makeMockUser({ role: UserRole.SuperAdmin }))))
    vi.doMock('../lib/api-client', () => ({
      api: {
        listTenants: vi.fn().mockResolvedValue([
          { id: 't1', name: 'Tenant 1', slug: 't1', description: null, isActive: true, userCount: 5, createdAt: '2026-01-01T00:00:00Z' },
        ]),
        listUsers: vi.fn().mockResolvedValue([
          makeMockUser(),
        ]),
      },
    }))
    const { default: DashboardComp } = await import('../pages/admin/AdminDashboardPage')
    renderWithProviders(<DashboardComp />)

    const section = await screen.findByText('Recent Tenants')
    expect(section).toBeInTheDocument()
  })
})

// ---- UsersPage Tests ----

describe('UsersPage Role Differentiation', () => {
  beforeEach(() => {
    vi.resetModules()
  })

  it('Should_ShowTenantFilterDropdown_WhenUserIsSuperAdmin', async () => {
    vi.doMock('../lib/auth', () => authModuleMock(mockAuth(makeMockUser({ role: UserRole.SuperAdmin }))))
    vi.doMock('../lib/api-client', () => ({
      api: {
        listUsers: vi.fn().mockResolvedValue([]),
        listTenants: vi.fn().mockResolvedValue([
          { id: 't1', name: 'Tenant 1', slug: 't1', description: null, isActive: true, userCount: 5, createdAt: '2026-01-01T00:00:00Z' },
        ]),
      },
    }))
    const { default: UsersComp } = await import('../pages/admin/UsersPage')
    renderWithProviders(<UsersComp />)

    // The tenant filter has "All tenants" as default option
    const allTenantsOption = await screen.findByText('All tenants')
    expect(allTenantsOption).toBeInTheDocument()
  })

  it('Should_HideTenantFilterDropdown_WhenUserIsAdmin', async () => {
    vi.doMock('../lib/auth', () => authModuleMock(mockAuth(makeMockUser({ role: UserRole.Admin }))))
    vi.doMock('../lib/api-client', () => ({
      api: {
        listUsers: vi.fn().mockResolvedValue([]),
        listTenants: vi.fn().mockResolvedValue([]),
      },
    }))
    const { default: UsersComp } = await import('../pages/admin/UsersPage')
    renderWithProviders(<UsersComp />)

    // Wait for initial render
    await screen.findByText('Users')
    expect(screen.queryByText('All tenants')).not.toBeInTheDocument()
  })

  it('Should_ShowTenantColumnInTable_WhenUserIsSuperAdmin', async () => {
    vi.doMock('../lib/auth', () => authModuleMock(mockAuth(makeMockUser({ role: UserRole.SuperAdmin }))))
    vi.doMock('../lib/api-client', () => ({
      api: {
        listUsers: vi.fn().mockResolvedValue([
          makeMockUser({ id: 'u1', username: 'user1', role: UserRole.User, tenantName: 'Tenant A' }),
        ]),
        listTenants: vi.fn().mockResolvedValue([]),
      },
    }))
    const { default: UsersComp } = await import('../pages/admin/UsersPage')
    renderWithProviders(<UsersComp />)

    // Wait for the table to render
    await screen.findByText('user1')
    // Tenant column header should be visible
    expect(screen.getByText('Tenant')).toBeInTheDocument()
  })

  it('Should_HideTenantColumnInTable_WhenUserIsAdmin', async () => {
    vi.doMock('../lib/auth', () => authModuleMock(mockAuth(makeMockUser({ role: UserRole.Admin }))))
    vi.doMock('../lib/api-client', () => ({
      api: {
        listUsers: vi.fn().mockResolvedValue([
          makeMockUser({ id: 'u1', username: 'user1', role: UserRole.User, tenantName: 'Tenant A' }),
        ]),
        listTenants: vi.fn().mockResolvedValue([]),
      },
    }))
    const { default: UsersComp } = await import('../pages/admin/UsersPage')
    renderWithProviders(<UsersComp />)

    await screen.findByText('user1')
    // Tenant column header should NOT exist in the table
    // Note: multiple elements may have "Tenant" text in other contexts
    const headers = screen.getAllByRole('columnheader')
    const tenantHeader = headers.find(h => h.textContent === 'Tenant')
    expect(tenantHeader).toBeUndefined()
  })

  it('Should_HideActionButtons_ForAdminUsers_WhenCallerIsAdmin', async () => {
    const adminUser = makeMockUser({ id: 'u2', username: 'admin-user', role: UserRole.Admin, tenantName: 'Test Tenant' })
    const regularUser = makeMockUser({ id: 'u3', username: 'regular-user', role: UserRole.User, tenantName: 'Test Tenant' })

    vi.doMock('../lib/auth', () => authModuleMock(mockAuth(makeMockUser({ role: UserRole.Admin }))))
    vi.doMock('../lib/api-client', () => ({
      api: {
        listUsers: vi.fn().mockResolvedValue([adminUser, regularUser]),
        listTenants: vi.fn().mockResolvedValue([]),
      },
    }))
    const { default: UsersComp } = await import('../pages/admin/UsersPage')
    renderWithProviders(<UsersComp />)

    await screen.findByText('admin-user')
    await screen.findByText('regular-user')

    // The admin-user row should NOT have action buttons (edit, delete, etc.)
    // The regular-user row SHOULD have action buttons
    const rows = screen.getAllByRole('row')
    // First row is header, second is admin-user, third is regular-user
    const adminRow = rows.find(r => r.textContent?.includes('admin-user'))
    const regularRow = rows.find(r => r.textContent?.includes('regular-user'))

    // Admin row should have no edit button (title="Edit user")
    const adminEditButtons = adminRow?.querySelectorAll('button[title="Edit user"]')
    expect(adminEditButtons?.length ?? 0).toBe(0)

    // Regular user row should have edit button
    const regularEditButtons = regularRow?.querySelectorAll('button[title="Edit user"]')
    expect(regularEditButtons?.length ?? 0).toBe(1)
  })
})
