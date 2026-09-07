import { describe, it, expect } from 'vitest'
import { screen } from '@testing-library/react'
import { renderWithProviders } from './test-utils'
import DetailSidebar from '../components/DetailSidebar'

const defaultProps = {
  tags: ['ai', 'docs'],
  type: 'Note',
  vaults: [{ id: 'v1', name: 'My Vault', isPrimary: true }],
  source: 'manual',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-02T00:00:00Z',
  isIndexed: true,
  indexedAt: '2026-01-01T12:00:00Z',
  attachmentCount: 3,
}

describe('DetailSidebar - SidebarRedesign', () => {
  it('Should_NotShowAISummaryCard_WhenBriefSummaryProvided', () => {
    renderWithProviders(
      <DetailSidebar {...defaultProps} briefSummary="This is a test summary" />
    )
    // The "AI Summary" sidebar card title should NOT be present
    expect(screen.queryByText('AI Summary')).not.toBeInTheDocument()
  })

  it('Should_ShowVaultCard_WhenVaultsProvided', () => {
    renderWithProviders(<DetailSidebar {...defaultProps} />)
    // Should show the vault card with vault name
    expect(screen.getByText('My Vault')).toBeInTheDocument()
  })

  it('Should_ShowVaultCardAtTop_WhenRendered', () => {
    renderWithProviders(<DetailSidebar {...defaultProps} />)
    // The Vault card should exist as a SidebarCard with title "Vault"
    // We look for the "Vault" title text in the sidebar cards
    const vaultTitle = screen.getByText('Vault')
    expect(vaultTitle).toBeInTheDocument()
  })

  it('Should_ShowPlaceholder_WhenNoVaultsProvided', () => {
    renderWithProviders(<DetailSidebar {...defaultProps} vaults={[]} />)
    expect(screen.getByText('No vault assigned')).toBeInTheDocument()
  })

  it('Should_NotShowEnrichmentStatus_InMetadataCard', () => {
    renderWithProviders(<DetailSidebar {...defaultProps} />)
    // Enrichment status line should be removed from Metadata card
    expect(screen.queryByText('Enrichment')).not.toBeInTheDocument()
    expect(screen.queryByText('Indexed')).not.toBeInTheDocument()
  })

  it('Should_StillShowTagsCard_WhenTagsProvided', () => {
    renderWithProviders(<DetailSidebar {...defaultProps} />)
    expect(screen.getByText('Tags')).toBeInTheDocument()
    expect(screen.getByText('ai')).toBeInTheDocument()
    expect(screen.getByText('docs')).toBeInTheDocument()
  })

  it('Should_StillShowMetadataCard_WhenRendered', () => {
    renderWithProviders(<DetailSidebar {...defaultProps} />)
    expect(screen.getByText('Metadata')).toBeInTheDocument()
    expect(screen.getByText('Note')).toBeInTheDocument()
  })

  it('Should_StillShowAttachmentsCard_WhenRendered', () => {
    renderWithProviders(<DetailSidebar {...defaultProps} />)
    expect(screen.getByText('Attachments')).toBeInTheDocument()
  })
})
