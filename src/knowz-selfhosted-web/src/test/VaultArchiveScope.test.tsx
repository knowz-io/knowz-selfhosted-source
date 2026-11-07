import { describe, expect, it, vi } from 'vitest'
import { screen } from '@testing-library/react'
import { renderWithProviders } from './test-utils'
import VaultDetailPage from '../pages/VaultDetailPage'

vi.mock('react-router-dom', async () => ({ ...await vi.importActual('react-router-dom'), useParams: () => ({ id: 'selected-vault' }) }))
vi.mock('../hooks/useFormatters', () => ({ useFormatters: () => ({ date: () => 'October 6', dateTime: () => 'October 6', relative: () => 'today' }) }))
vi.mock('../components/portability/AccountArchivePanel', () => ({ AccountArchivePanel: ({ vaultId }: { vaultId?: string }) => <section aria-label="Scoped archive" data-vault-id={vaultId} /> }))
vi.mock('../lib/api-client', () => ({
  api: {
    getVault: vi.fn().mockResolvedValue({ id: 'selected-vault', name: 'Selected vault', createdAt: '2026-10-06T00:00:00Z' }),
    getVaultContents: vi.fn().mockResolvedValue({ totalItems: 0, items: [] }),
    getGitSyncStatus: vi.fn().mockResolvedValue(null),
  },
  ApiError: class extends Error {},
}))

describe('Vault detail archive scope', () => {
  it('exposes archive recovery and export bound to the current vault', async () => {
    renderWithProviders(<VaultDetailPage />)
    await screen.findByText('Selected vault')
    expect(screen.getByRole('region', { name: 'Scoped archive' })).toHaveAttribute('data-vault-id', 'selected-vault')
  })
})
