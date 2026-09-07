import { describe, it, expect, vi } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import { renderWithProviders } from './test-utils'
import VaultListPage from '../pages/VaultListPage'

// Mock the api-client module
vi.mock('../lib/api-client', () => ({
  api: {
    listVaults: vi.fn().mockResolvedValue({
      vaults: [
        {
          id: 'vault-1',
          name: 'My Vault',
          description: 'A test vault',
          isDefault: false,
          knowledgeCount: 5,
          vaultType: 'Business',
          createdAt: '2026-01-01T00:00:00Z',
          updatedAt: '2026-01-01T00:00:00Z',
        },
        {
          id: 'vault-2',
          name: 'Default Vault',
          description: null,
          isDefault: true,
          knowledgeCount: 12,
          vaultType: null,
          createdAt: '2026-01-01T00:00:00Z',
          updatedAt: '2026-01-01T00:00:00Z',
        },
      ],
    }),
  },
  ApiError: class ApiError extends Error {
    status: number
    constructor(status: number, message: string) {
      super(message)
      this.status = status
      this.name = 'ApiError'
    }
  },
}))

describe('VaultListPage Navigation', () => {
  it('Should_LinkToKnowledgeFilteredByVaultId_WhenVaultCardClicked', async () => {
    renderWithProviders(<VaultListPage />)

    // Wait for vaults to load
    await waitFor(() => {
      expect(screen.getByText('My Vault')).toBeInTheDocument()
    })

    // The vault card should link to knowledge filtered by vaultId, NOT to /vaults/:id
    const vaultLink = screen.getByText('My Vault').closest('a')
    expect(vaultLink).toBeInTheDocument()
    expect(vaultLink!.getAttribute('href')).toBe('/knowledge?vaultId=vault-1')
  })

  it('Should_LinkToKnowledgeFilteredByVaultId_ForDefaultVault', async () => {
    renderWithProviders(<VaultListPage />)

    await waitFor(() => {
      expect(screen.getByText('Default Vault')).toBeInTheDocument()
    })

    const vaultLink = screen.getByText('Default Vault').closest('a')
    expect(vaultLink).toBeInTheDocument()
    expect(vaultLink!.getAttribute('href')).toBe('/knowledge?vaultId=vault-2')
  })

  it('Should_NotLinkToVaultDetailPage_WhenVaultClicked', async () => {
    renderWithProviders(<VaultListPage />)

    await waitFor(() => {
      expect(screen.getByText('My Vault')).toBeInTheDocument()
    })

    // Make sure it does NOT link to /vaults/:id (which shows git sync)
    const links = screen.getAllByRole('link')
    const vaultLinks = links.filter(
      (link) => link.getAttribute('href')?.startsWith('/vaults/')
    )
    // None of the vault card links should go to /vaults/:id
    expect(vaultLinks.length).toBe(0)
  })
})
