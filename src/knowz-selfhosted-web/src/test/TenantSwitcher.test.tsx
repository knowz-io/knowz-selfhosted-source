import { describe, it, expect, vi, beforeEach } from 'vitest'
import { fireEvent, screen } from '@testing-library/react'
import { renderWithProviders } from './test-utils'
import { UserRole } from '../lib/types'
import type { TenantMembershipDto, UserDto } from '../lib/types'

const switchTenantMock = vi.fn()
const clearMock = vi.fn()

let currentUser: UserDto | null = null
let currentAvailable: TenantMembershipDto[] = []

vi.mock('../lib/auth', () => ({
  useAuth: () => ({
    user: currentUser,
    availableTenants: currentAvailable,
    switchTenant: switchTenantMock,
  }),
}))

vi.mock('@tanstack/react-query', async () => {
  const actual =
    await vi.importActual<typeof import('@tanstack/react-query')>('@tanstack/react-query')
  return {
    ...actual,
    useQueryClient: () => ({ clear: clearMock }),
  }
})

import TenantSwitcher from '../components/TenantSwitcher'

function makeUser(): UserDto {
  return {
    id: 'u',
    username: 'u',
    email: null,
    displayName: 'User',
    role: UserRole.User,
    tenantId: 't1',
    tenantName: 'Tenant One',
    isActive: true,
    apiKey: null,
    createdAt: '2026-01-01T00:00:00Z',
    lastLoginAt: null,
  }
}

function makeTenants(): TenantMembershipDto[] {
  return [
    { tenantId: 't1', tenantName: 'Tenant One', tenantSlug: 't1', role: UserRole.User, isActive: true },
    { tenantId: 't2', tenantName: 'Tenant Two', tenantSlug: 't2', role: UserRole.Admin, isActive: true },
    { tenantId: 't3', tenantName: 'Tenant Three', tenantSlug: 't3', role: UserRole.User, isActive: true },
  ]
}

describe('TenantSwitcher — gating', () => {
  beforeEach(() => {
    switchTenantMock.mockReset()
    clearMock.mockReset()
    currentUser = null
    currentAvailable = []
  })

  it('Should_RenderNothing_WhenSingleTenantMembership', () => {
    currentUser = makeUser()
    currentAvailable = [makeTenants()[0]]
    const { container } = renderWithProviders(<TenantSwitcher />)
    expect(container.firstChild).toBeNull()
  })

  it('Should_RenderTrigger_WhenMultipleTenantMemberships', () => {
    currentUser = makeUser()
    currentAvailable = makeTenants()
    renderWithProviders(<TenantSwitcher />)
    expect(screen.getByTestId('sh-tenant-switcher')).toBeInTheDocument()
  })
})

describe('TenantSwitcher — header-compact behavior', () => {
  beforeEach(() => {
    switchTenantMock.mockReset()
    clearMock.mockReset()
    currentUser = makeUser()
    currentAvailable = makeTenants()
  })

  it('Should_NotIncludeSidebarKickerLabel_InHeaderMode', () => {
    renderWithProviders(<TenantSwitcher />)
    // The old sidebar had an uppercase "TENANT" kicker. Header-compact form drops it.
    expect(screen.queryByText(/^tenant$/i)).not.toBeInTheDocument()
  })

  it('Should_ShowTwoOtherTenants_InMenu_WhenTriggerClicked', () => {
    const { container } = renderWithProviders(<TenantSwitcher />)
    fireEvent.click(screen.getByTestId('sh-tenant-switcher'))
    expect(screen.getByTestId('sh-tenant-switcher-menu')).toBeInTheDocument()
    expect(container.querySelector('#sh-tenant-switcher-menu')).toBeNull()
    expect(document.body.querySelector('#sh-tenant-switcher-menu')).not.toBeNull()
    expect(screen.getByTestId('sh-tenant-switcher-option-t2')).toBeInTheDocument()
    expect(screen.getByTestId('sh-tenant-switcher-option-t3')).toBeInTheDocument()
    // Current tenant must NOT appear as an option.
    expect(screen.queryByTestId('sh-tenant-switcher-option-t1')).not.toBeInTheDocument()
  })

  it('Should_CallSwitchTenantAndClearCache_WhenOptionClicked', async () => {
    switchTenantMock.mockResolvedValue(undefined)
    renderWithProviders(<TenantSwitcher />)
    fireEvent.click(screen.getByTestId('sh-tenant-switcher'))
    fireEvent.click(screen.getByTestId('sh-tenant-switcher-option-t2'))
    // switchTenant fires synchronously; cache.clear happens after await resolves.
    await Promise.resolve()
    await Promise.resolve()
    expect(switchTenantMock).toHaveBeenCalledWith('t2')
    expect(clearMock).toHaveBeenCalledTimes(1)
  })

  it('Should_CloseMenu_WhenOutsideClicked', () => {
    renderWithProviders(<TenantSwitcher />)
    fireEvent.click(screen.getByTestId('sh-tenant-switcher'))
    expect(screen.getByTestId('sh-tenant-switcher-menu')).toBeInTheDocument()
    fireEvent.mouseDown(document.body)
    expect(screen.queryByTestId('sh-tenant-switcher-menu')).not.toBeInTheDocument()
  })

  it('Should_CloseMenu_WhenEscapePressed', () => {
    renderWithProviders(<TenantSwitcher />)
    fireEvent.click(screen.getByTestId('sh-tenant-switcher'))
    fireEvent.keyDown(document, { key: 'Escape' })
    expect(screen.queryByTestId('sh-tenant-switcher-menu')).not.toBeInTheDocument()
  })
})
