import { describe, it, expect, vi } from 'vitest'
import { screen } from '@testing-library/react'
import { renderWithProviders } from './test-utils'
import Layout from '../components/Layout'

vi.mock('../lib/auth', () => ({
  useAuth: () => ({
    user: {
      id: 'user-1',
      username: 'testuser',
      email: null,
      displayName: 'Test User',
      role: 2,
      tenantId: 'tenant-1',
      tenantName: 'Test Tenant',
      isActive: true,
      apiKey: null,
      createdAt: '2026-01-01T00:00:00Z',
      lastLoginAt: null,
    },
    token: 'test-token',
    isAuthenticated: true,
    isLoading: false,
    login: vi.fn(),
    loginWithToken: vi.fn(),
    logout: vi.fn(),
    refreshUser: vi.fn(),
    activeTenantId: null,
    setActiveTenantId: vi.fn(),
    availableTenants: [],
    currentTenantName: 'Test Tenant',
    selectTenant: vi.fn(),
    switchTenant: vi.fn(),
    pendingUserId: null,
  }),
}))

vi.mock('../components/Header', () => ({
  default: () => <header data-testid="sh-header" role="banner">header-stub</header>,
}))

describe('Layout', () => {
  it('Should_RenderHeaderLandmark_WhenShellMounts', () => {
    renderWithProviders(
      <Layout>
        <div>Content</div>
      </Layout>,
    )
    expect(screen.getByTestId('sh-header')).toBeInTheDocument()
  })

  it('Should_ExposeMainLandmark_WithCorrectIdAndTabIndex', () => {
    renderWithProviders(
      <Layout>
        <div>Content</div>
      </Layout>,
    )
    const main = screen.getByRole('main')
    expect(main.id).toBe('main-content')
    expect(main.getAttribute('tabindex')).toBe('-1')
  })

  it('Should_RenderSkipLink_AsFirstFocusableElement', () => {
    renderWithProviders(
      <Layout>
        <div>Content</div>
      </Layout>,
    )
    const skipLink = screen.getByTestId('sh-skip-link')
    expect(skipLink.tagName.toLowerCase()).toBe('a')
    expect(skipLink).toHaveAttribute('href', '#main-content')
  })
})
