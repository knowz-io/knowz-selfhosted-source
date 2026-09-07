import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { AuthContext, FirstLoginRoute, ProtectedRoute } from '../lib/auth'

function authValue(mustChangePassword: boolean) {
  return {
    user: {
      id: 'u', username: 'admin', email: null, displayName: 'Admin', role: 2,
      tenantId: 't', tenantName: 'Default', isActive: true, apiKey: null,
      createdAt: '2026-09-02T00:00:00Z', lastLoginAt: null, mustChangePassword,
    },
    token: 'token', isAuthenticated: true, isLoading: false,
    login: vi.fn(), loginWithToken: vi.fn(), logout: vi.fn(), refreshUser: vi.fn(),
    activeTenantId: null, setActiveTenantId: vi.fn(), availableTenants: [],
    currentTenantName: 'Default', selectTenant: vi.fn(), switchTenant: vi.fn(), pendingUserId: null,
  } as never
}

function renderRoutes(mustChangePassword: boolean, initialEntry: string) {
  return (
    <AuthContext.Provider value={authValue(mustChangePassword)}>
      <MemoryRouter initialEntries={[initialEntry]}>
        <Routes>
          <Route path="/" element={<ProtectedRoute><div>dashboard</div></ProtectedRoute>} />
          <Route path="/first-login" element={<FirstLoginRoute><div>change screen</div></FirstLoginRoute>} />
        </Routes>
      </MemoryRouter>
    </AuthContext.Provider>
  )
}

describe('first-login route guards', () => {
  it('redirects a provisional user away from the normal shell', () => {
    render(renderRoutes(true, '/'))
    expect(screen.getByText('change screen')).toBeInTheDocument()
    expect(screen.queryByText('dashboard')).not.toBeInTheDocument()
  })

  it('redirects a normal user away from the first-login screen', () => {
    render(renderRoutes(false, '/first-login'))
    expect(screen.getByText('dashboard')).toBeInTheDocument()
  })
})
