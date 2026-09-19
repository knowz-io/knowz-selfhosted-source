import { describe, it, expect, vi } from 'vitest'
import { screen } from '@testing-library/react'
import { useLocation } from 'react-router-dom'
import type { ReactNode } from 'react'
import { renderWithProviders } from './test-utils'
import App from '../App'

vi.mock('../components/ErrorBoundary', () => ({
  default: ({ children }: { children: ReactNode }) => <>{children}</>,
}))

vi.mock('../components/Layout', () => ({
  default: ({ children }: { children: ReactNode }) => <>{children}</>,
}))

vi.mock('../lib/theme', () => ({
  initTheme: vi.fn(),
}))

vi.mock('../lib/auth', () => ({
  AuthProvider: ({ children }: { children: ReactNode }) => <>{children}</>,
  FirstLoginRoute: ({ children }: { children: ReactNode }) => <>{children}</>,
  ProtectedRoute: ({ children }: { children: ReactNode }) => <>{children}</>,
  AdminRoute: ({ children }: { children: ReactNode }) => <>{children}</>,
  SuperAdminRoute: ({ children }: { children: ReactNode }) => <>{children}</>,
}))

vi.mock('../pages/LoginPage', () => ({ default: () => <div>Login</div> }))
vi.mock('../pages/FirstLoginPasswordPage', () => ({ default: () => <div>First login</div> }))
vi.mock('../pages/DashboardPage', () => ({ default: () => <div>Dashboard</div> }))
vi.mock('../pages/KnowledgeListPage', () => ({ default: () => <div>Knowledge</div> }))
vi.mock('../pages/KnowledgeDetailPage', () => ({ default: () => <div>Knowledge Detail</div> }))
vi.mock('../pages/KnowledgeCreatePage', () => ({ default: () => <div>Create Knowledge</div> }))
vi.mock('../pages/VaultListPage', () => ({ default: () => <div>Vaults</div> }))
vi.mock('../pages/VaultDetailPage', () => ({ default: () => <div>Vault Detail</div> }))
vi.mock('../pages/SearchPage', () => ({ default: () => <div>Search</div> }))
vi.mock('../pages/InboxPage', () => ({ default: () => <div>Inbox</div> }))
vi.mock('../pages/FilesPage', () => ({ default: () => <div>Files</div> }))
vi.mock('../pages/ChatPage', () => ({ default: () => <div>Chat</div> }))
vi.mock('../pages/UnifiedSettingsPage', () => ({ default: () => <div>Settings</div> }))
vi.mock('../pages/admin/AdminDashboardPage', () => ({ default: () => <div>Admin</div> }))
vi.mock('../pages/admin/TenantsPage', () => ({ default: () => <div>Tenants</div> }))
vi.mock('../pages/admin/UsersPage', () => ({ default: () => <div>Users</div> }))
vi.mock('../pages/admin/AdminSettingsPage', () => ({ default: () => <div>Admin Settings</div> }))
vi.mock('../pages/admin/SSOSettingsPage', () => ({ default: () => <div>SSO</div> }))
vi.mock('../pages/admin/AuditLogPage', () => ({ default: () => <div>Audit</div> }))
vi.mock('../pages/admin/PlatformSyncPage', () => ({ default: () => <div>Destinations page</div> }))
vi.mock('../pages/SSOCallbackPage', () => ({ default: () => <div>Callback</div> }))
vi.mock('../pages/OrganizePage', () => ({
  default: () => {
    const location = useLocation()
    const tab = new URLSearchParams(location.search).get('tab') ?? 'missing'
    return <div>Organize tab: {tab}</div>
  },
}))

describe('App organize redirects', () => {
  it('Should_PreserveTopicsIntent_WhenLegacyTopicsRouteUsed', () => {
    renderWithProviders(<App />, {
      initialEntries: ['/topics'],
    })

    expect(screen.getByText('Organize tab: topics')).toBeInTheDocument()
  })

  it('Should_PreserveEntitiesIntent_WhenLegacyEntitiesRouteUsed', () => {
    renderWithProviders(<App />, {
      initialEntries: ['/entities'],
    })

    expect(screen.getByText('Organize tab: entities')).toBeInTheDocument()
  })
})

describe('App destinations alias', () => {
  it('Should_LandOnSyncRoute_WhenFriendlyDestinationsAliasUsed', () => {
    renderWithProviders(<App />, {
      initialEntries: ['/destinations'],
    })

    expect(screen.getByText('Destinations page')).toBeInTheDocument()
  })
})
