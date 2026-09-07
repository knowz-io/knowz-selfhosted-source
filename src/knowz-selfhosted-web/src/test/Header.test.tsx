import { describe, it, expect, vi, beforeEach } from 'vitest'
import { fireEvent, screen } from '@testing-library/react'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { renderWithProviders } from './test-utils'
import { UserRole } from '../lib/types'

let currentRole: UserRole = UserRole.SuperAdmin
let isAuthed = true

vi.mock('../lib/auth', () => ({
  useAuth: () => ({
    user: isAuthed
      ? {
          id: 'u',
          username: 'testuser',
          email: null,
          displayName: 'Test User',
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
    logout: vi.fn(),
    activeTenantId: null,
    setActiveTenantId: vi.fn(),
    availableTenants: [],
  }),
}))

vi.mock('../lib/theme', () => ({
  useTheme: () => ({
    theme: 'light',
    toggle: vi.fn(),
  }),
}))

vi.mock('../lib/api-client', () => ({
  api: {
    listTenants: vi.fn().mockResolvedValue([]),
  },
}))

import Header from '../components/Header'

const headerSource = readFileSync(
  resolve(__dirname, '../components/Header.tsx'),
  'utf8',
)

describe('Header — shell + landmarks', () => {
  beforeEach(() => {
    currentRole = UserRole.SuperAdmin
    isAuthed = true
  })

  it('Should_RenderHeaderWithBannerRole', () => {
    renderWithProviders(<Header />)
    const header = screen.getByTestId('sh-header')
    expect(header.tagName.toLowerCase()).toBe('header')
    expect(header).toHaveAttribute('role', 'banner')
  })

  it('Should_RenderPrimaryNav_WithAriaLabel', () => {
    renderWithProviders(<Header />)
    const nav = screen.getByTestId('sh-nav-primary')
    expect(nav.tagName.toLowerCase()).toBe('nav')
    expect(nav).toHaveAttribute('aria-label', 'Primary')
  })

  it('Should_RenderPageMetaTitle_ForCurrentRoute', () => {
    renderWithProviders(<Header />, { initialEntries: ['/knowledge'] })
    expect(screen.getByTestId('sh-pagemeta-title').textContent).toBe('Knowledge')
  })

  it('Should_UseCompactHeaderHeight', () => {
    renderWithProviders(<Header />)
    const row = screen.getByTestId('sh-header-nav-row')
    expect(row.className).toMatch(/3\.75rem|layout-header-height/)
  })
})

describe('Header — VERIFY-L3 primary nav', () => {
  beforeEach(() => {
    currentRole = UserRole.SuperAdmin
    isAuthed = true
  })

  it('Should_RenderKnowledgeChatSearch_InPrimaryNav', () => {
    currentRole = UserRole.User
    renderWithProviders(<Header />)
    expect(screen.getByTestId('nav-link-knowledge')).toBeInTheDocument()
    expect(screen.getByTestId('nav-link-chat')).toBeInTheDocument()
    expect(screen.getByTestId('nav-link-search')).toBeInTheDocument()
  })

  it('Should_KeepAdminOutOfPrimaryNav', () => {
    currentRole = UserRole.Admin
    renderWithProviders(<Header />)
    expect(screen.queryByTestId('nav-link-admin')).not.toBeInTheDocument()
  })

  it('Should_KeepInboxOrganize_InMoreMenu', () => {
    renderWithProviders(<Header />)
    expect(screen.queryByTestId('nav-link-inbox')).not.toBeInTheDocument()
    expect(screen.queryByTestId('nav-link-organize')).not.toBeInTheDocument()
    fireEvent.click(screen.getByTestId('nav-link-more'))
    expect(screen.getByTestId('nav-link-vaults')).toBeInTheDocument()
    expect(screen.getByTestId('nav-link-files')).toBeInTheDocument()
    expect(screen.getByTestId('nav-link-inbox')).toBeInTheDocument()
    expect(screen.getByTestId('nav-link-organize')).toBeInTheDocument()
  })

  it('Should_NotAdvertisePlatformOnlyRoutes', () => {
    renderWithProviders(<Header />)
    fireEvent.click(screen.getByTestId('nav-link-more'))
    expect(screen.queryByTestId('nav-link-superadmin')).not.toBeInTheDocument()
    expect(screen.queryByTestId('nav-link-sites')).not.toBeInTheDocument()
    expect(screen.queryByTestId('nav-link-billing')).not.toBeInTheDocument()
    expect(screen.queryByTestId('nav-link-journal-qa')).not.toBeInTheDocument()
    expect(headerSource).not.toMatch(/\/superadmin|\/sites|\/billing|\/journal-qa/)
  })

  it('Should_RenderBrainIcon_InLogoBlock', () => {
    renderWithProviders(<Header />)
    expect(screen.getByTestId('sh-logo-brain-icon')).toBeInTheDocument()
  })

  it('Should_RenderPacificoWordmark_AsLowercaseKnowz', () => {
    renderWithProviders(<Header />)
    const logo = screen.getByTestId('sh-logo-link')
    expect(logo.textContent).toContain('knowz')
    expect(logo.textContent).not.toContain('Knowz')
    const wordmark = screen.getByTestId('sh-logo-wordmark')
    expect(wordmark.getAttribute('style') ?? wordmark.className).toMatch(/Pacifico/)
  })

  it('Should_HideNinePixelUppercaseNavLabels', () => {
    expect(headerSource).not.toContain('text-[9px]')
    expect(headerSource).not.toMatch(/uppercase tracking-wide/)
  })

  it('Should_DropPingRings_FromLogo', () => {
    expect(headerSource).not.toContain('animate-ping')
    renderWithProviders(<Header />)
    const logo = screen.getByTestId('sh-logo-link')
    expect(logo.innerHTML).not.toContain('animate-ping')
  })

  it('Should_MarkActiveNavItem_WithAriaCurrentPage', () => {
    renderWithProviders(<Header />, { initialEntries: ['/search'] })
    expect(screen.getByTestId('nav-link-search')).toHaveAttribute('aria-current', 'page')
    expect(screen.getByTestId('nav-link-chat')).not.toHaveAttribute('aria-current')
  })

  it('Should_RenderKnowledgeAsPlainNavLink_WithHrefToKnowledge', () => {
    renderWithProviders(<Header />)
    const knowledge = screen.getByTestId('nav-link-knowledge')
    expect(knowledge.tagName.toLowerCase()).toBe('a')
    expect(knowledge).toHaveAttribute('href', '/knowledge')
  })

  it('Should_NavigateToRoot_WhenLogoClicked', () => {
    renderWithProviders(<Header />)
    const logo = screen.getByTestId('sh-logo-link')
    expect(logo.tagName.toLowerCase()).toBe('a')
    expect(logo).toHaveAttribute('href', '/')
  })
})

describe('Header — mobile drawer', () => {
  beforeEach(() => {
    currentRole = UserRole.SuperAdmin
    isAuthed = true
  })

  it('Should_OpenMobileDrawer_WhenHamburgerClicked', () => {
    renderWithProviders(<Header />)
    expect(screen.queryByTestId('sh-mobile-drawer')).not.toBeInTheDocument()
    const hamburger = screen.getByTestId('sh-mobile-hamburger')
    fireEvent.click(hamburger)
    expect(screen.getByTestId('sh-mobile-drawer')).toBeInTheDocument()
    expect(hamburger).toHaveAttribute('aria-expanded', 'true')
  })

  it('Should_RenderPrimaryAndOverflowNavItems_InMobileDrawer', () => {
    renderWithProviders(<Header />)
    fireEvent.click(screen.getByTestId('sh-mobile-hamburger'))
    expect(screen.getByTestId('nav-link-knowledge-mobile')).toBeInTheDocument()
    expect(screen.getByTestId('nav-link-chat-mobile')).toBeInTheDocument()
    expect(screen.getByTestId('nav-link-search-mobile')).toBeInTheDocument()
    expect(screen.getByTestId('nav-link-vaults-mobile')).toBeInTheDocument()
    expect(screen.getByTestId('nav-link-files-mobile')).toBeInTheDocument()
    expect(screen.getByTestId('nav-link-inbox-mobile')).toBeInTheDocument()
    expect(screen.getByTestId('nav-link-organize-mobile')).toBeInTheDocument()
    expect(screen.queryByTestId('nav-link-admin-mobile')).not.toBeInTheDocument()
  })
})
