import { describe, it, expect, vi, beforeEach } from 'vitest'
import { fireEvent, screen } from '@testing-library/react'
import { renderWithProviders } from './test-utils'
import { UserRole } from '../lib/types'

const logoutMock = vi.fn()
const toggleMock = vi.fn()

let currentRole: UserRole = UserRole.Admin
let currentDisplayName: string | null = 'Test User'
let isAuthed = true
let currentTheme: 'light' | 'dark' = 'dark'

vi.mock('../lib/auth', () => ({
  useAuth: () => ({
    user: isAuthed
      ? {
          id: 'u',
          username: 'testuser',
          email: null,
          displayName: currentDisplayName,
          role: currentRole,
          tenantId: 't',
          tenantName: 'Test Tenant',
          isActive: true,
          apiKey: null,
          createdAt: '2026-01-01T00:00:00Z',
          lastLoginAt: null,
        }
      : null,
    isAuthenticated: isAuthed,
    logout: logoutMock,
  }),
}))

vi.mock('../lib/theme', () => ({
  useTheme: () => ({
    theme: currentTheme,
    toggle: toggleMock,
  }),
}))

import UserMenu from '../components/UserMenu'

function openDropdown() {
  fireEvent.click(screen.getByTestId('sh-user-menu'))
}

describe('UserMenu — base behavior', () => {
  beforeEach(() => {
    logoutMock.mockClear()
    toggleMock.mockClear()
    currentRole = UserRole.Admin
    currentDisplayName = 'Test User'
    isAuthed = true
    currentTheme = 'dark'
  })

  it('Should_RenderNothing_WhenUserIsNotAuthenticated', () => {
    isAuthed = false
    const { container } = renderWithProviders(<UserMenu />)
    expect(container.firstChild).toBeNull()
  })

  it('Should_OpenDropdown_WhenTriggerClicked', () => {
    const { container } = renderWithProviders(<UserMenu />)
    openDropdown()
    expect(screen.getByTestId('sh-user-menu-dropdown')).toBeInTheDocument()
    expect(container.querySelector('#sh-user-menu-dropdown')).toBeNull()
    expect(document.body.querySelector('#sh-user-menu-dropdown')).not.toBeNull()
  })

  it('Should_CloseDropdown_WhenOutsideClicked', () => {
    renderWithProviders(<UserMenu />)
    openDropdown()
    fireEvent.mouseDown(document.body)
    expect(screen.queryByTestId('sh-user-menu-dropdown')).not.toBeInTheDocument()
  })

  it('Should_CloseDropdown_WhenEscapePressed', () => {
    renderWithProviders(<UserMenu />)
    openDropdown()
    fireEvent.keyDown(document, { key: 'Escape' })
    expect(screen.queryByTestId('sh-user-menu-dropdown')).not.toBeInTheDocument()
  })

  it('Should_RenderSettingsLink_WithTestId', () => {
    renderWithProviders(<UserMenu />)
    openDropdown()
    const settingsLink = screen.getByTestId('sh-user-menu-settings')
    expect(settingsLink).toHaveAttribute('href', '/settings')
  })

  it('Should_InvokeThemeToggle_WhenThemeButtonClicked', () => {
    renderWithProviders(<UserMenu />)
    openDropdown()
    fireEvent.click(screen.getByTestId('sh-user-menu-theme-toggle'))
    expect(toggleMock).toHaveBeenCalledTimes(1)
  })

  it('Should_KeepDropdownOpen_WhenThemeToggleClicked', () => {
    renderWithProviders(<UserMenu />)
    openDropdown()
    fireEvent.click(screen.getByTestId('sh-user-menu-theme-toggle'))
    // Theme toggle must NOT close the dropdown (spec §Section 4).
    expect(screen.getByTestId('sh-user-menu-dropdown')).toBeInTheDocument()
  })

  it('Should_InvokeLogout_WhenSignOutClicked', () => {
    renderWithProviders(<UserMenu />)
    openDropdown()
    fireEvent.click(screen.getByTestId('sh-user-menu-logout'))
    expect(logoutMock).toHaveBeenCalledTimes(1)
    expect(screen.queryByTestId('sh-user-menu-dropdown')).not.toBeInTheDocument()
  })

  it('Should_UseFirstCharOfDisplayName_ForAvatarInitial', () => {
    currentDisplayName = 'alice'
    renderWithProviders(<UserMenu />)
    const trigger = screen.getByTestId('sh-user-menu')
    expect(trigger.textContent).toContain('A')
  })
})

describe('UserMenu — admin shortcuts (Section 3)', () => {
  beforeEach(() => {
    logoutMock.mockClear()
    toggleMock.mockClear()
    currentDisplayName = 'Test User'
    isAuthed = true
    currentTheme = 'dark'
  })

  const COMMON_ADMIN_SLUGS = ['overview', 'users', 'audit-logs'] as const
  const SUPER_ADMIN_ONLY_SLUGS = ['tenants', 'sso', 'settings'] as const
  const SLUG_TO_PATH: Record<string, string> = {
    overview: '/admin',
    users: '/admin/users',
    'audit-logs': '/admin/audit-logs',
    tenants: '/admin/tenants',
    sso: '/admin/sso',
    settings: '/admin/settings',
  }

  it('Should_HideAdministrationSection_WhenUserIsPlainUser', () => {
    currentRole = UserRole.User
    renderWithProviders(<UserMenu />)
    openDropdown()
    expect(screen.queryByText(/administration/i)).not.toBeInTheDocument()
    for (const slug of [...COMMON_ADMIN_SLUGS, ...SUPER_ADMIN_ONLY_SLUGS]) {
      expect(screen.queryByTestId(`sh-user-menu-admin-${slug}`)).not.toBeInTheDocument()
    }
  })

  it('Should_RenderCommonAdminShortcuts_ButHideSuperAdminOnes_WhenUserIsAdmin', () => {
    currentRole = UserRole.Admin
    renderWithProviders(<UserMenu />)
    openDropdown()
    expect(screen.getByText(/administration/i)).toBeInTheDocument()
    for (const slug of COMMON_ADMIN_SLUGS) {
      const link = screen.getByTestId(`sh-user-menu-admin-${slug}`)
      expect(link).toHaveAttribute('href', SLUG_TO_PATH[slug])
    }
    for (const slug of SUPER_ADMIN_ONLY_SLUGS) {
      expect(screen.queryByTestId(`sh-user-menu-admin-${slug}`)).not.toBeInTheDocument()
    }
  })

  it('Should_RenderAllSixAdminShortcuts_WhenUserIsSuperAdmin', () => {
    currentRole = UserRole.SuperAdmin
    renderWithProviders(<UserMenu />)
    openDropdown()
    for (const slug of [...COMMON_ADMIN_SLUGS, ...SUPER_ADMIN_ONLY_SLUGS]) {
      const link = screen.getByTestId(`sh-user-menu-admin-${slug}`)
      expect(link).toHaveAttribute('href', SLUG_TO_PATH[slug])
      expect(link).toHaveAttribute('role', 'menuitem')
    }
  })

  it('Should_CloseDropdown_WhenAdminShortcutClicked', () => {
    currentRole = UserRole.SuperAdmin
    renderWithProviders(<UserMenu />)
    openDropdown()
    fireEvent.click(screen.getByTestId('sh-user-menu-admin-overview'))
    expect(screen.queryByTestId('sh-user-menu-dropdown')).not.toBeInTheDocument()
  })
})
