import { describe, it, expect } from 'vitest'
import { screen } from '@testing-library/react'
import { renderWithProviders } from './test-utils'
import EnrichmentBanner from '../components/EnrichmentBanner'

describe('EnrichmentAnimation', () => {
  it('Should_ShowAnimatedBanner_WhenStatusIsProcessing', () => {
    renderWithProviders(
      <EnrichmentBanner status="processing" />
    )
    expect(screen.getByText('AI enrichment in progress...')).toBeInTheDocument()
  })

  it('Should_ShowAnimatedBanner_WhenStatusIsPending', () => {
    renderWithProviders(
      <EnrichmentBanner status="pending" />
    )
    expect(screen.getByText('AI enrichment in progress...')).toBeInTheDocument()
  })

  it('Should_NotShowBanner_WhenStatusIsCompleted', () => {
    renderWithProviders(
      <EnrichmentBanner status="completed" />
    )
    expect(screen.queryByText('AI enrichment in progress...')).not.toBeInTheDocument()
  })

  it('Should_NotShowBanner_WhenStatusIsNull', () => {
    renderWithProviders(
      <EnrichmentBanner status={null} />
    )
    expect(screen.queryByText('AI enrichment in progress...')).not.toBeInTheDocument()
  })

  it('Should_HavePulseAnimation_WhenStatusIsProcessing', () => {
    renderWithProviders(
      <EnrichmentBanner status="processing" />
    )
    const banner = screen.getByTestId('enrichment-banner')
    expect(banner.className).toContain('animate-pulse')
  })

  it('Should_NotShowBanner_WhenStatusIsError', () => {
    renderWithProviders(
      <EnrichmentBanner status="error" />
    )
    expect(screen.queryByText('AI enrichment in progress...')).not.toBeInTheDocument()
  })
})
