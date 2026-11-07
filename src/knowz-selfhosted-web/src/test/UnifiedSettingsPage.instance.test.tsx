import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen } from '@testing-library/react'
import { renderWithProviders } from './test-utils'
import UnifiedSettingsPage from '../pages/UnifiedSettingsPage'
import { UserRole } from '../lib/types'

const session = vi.hoisted(() => ({ role: 2 }))
vi.mock('../lib/auth', () => ({ useAuth: () => ({ user: { role: session.role } }) }))
vi.mock('../pages/SettingsPage', () => ({ default: () => <div>Connection settings</div> }))
vi.mock('../pages/AccountPage', () => ({ default: () => <div>Account settings</div> }))
vi.mock('../pages/ApiKeysPage', () => ({ default: () => <div>API keys</div> }))
vi.mock('../pages/DataPortabilityPage', () => ({ default: () => <div>Data</div> }))
vi.mock('../pages/McpSetupPage', () => ({ default: () => <div>MCP setup</div> }))

beforeEach(() => { session.role = UserRole.SuperAdmin })
describe('selfhosted instance settings access', () => {
  it('links operators to the actual protected instance configuration route', () => {
    renderWithProviders(<UnifiedSettingsPage />)
    expect(screen.getByRole('link', { name: 'Configure this instance' })).toHaveAttribute('href', '/admin/settings')
    expect(screen.getByText(/AI providers, application services and restart status/i)).toBeInTheDocument()
    expect(screen.getByText('knowz runtime configure')).toBeInTheDocument()
  })
  it.each([UserRole.User, UserRole.Admin])('preserves account settings without offering instance authority to role %s', role => {
    session.role = role
    renderWithProviders(<UnifiedSettingsPage />)
    expect(screen.queryByRole('link', { name: 'Configure this instance' })).not.toBeInTheDocument()
    expect(screen.getByText('Connection settings')).toBeInTheDocument()
  })
})
