import { describe, it, expect, vi } from 'vitest'
import { screen } from '@testing-library/react'
import { renderWithProviders } from './test-utils'
import DataPortabilityPage from '../pages/DataPortabilityPage'

vi.mock('../lib/api-client', () => ({
  api: {
    getSchema: vi.fn(() => Promise.resolve({ compatibility: 'Schema v3 — compatible' })),
    exportData: vi.fn(),
    exportZip: vi.fn(),
    validateImport: vi.fn(),
    importData: vi.fn(),
    importZip: vi.fn(),
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

describe('DataPortabilityPage — reciprocal Destinations link (VERIFY-IA-4)', () => {
  it('Should_LinkBackToDestinations_FromTheTopInfoCard', () => {
    renderWithProviders(<DataPortabilityPage />)
    const link = screen.getByRole('link', { name: /destinations/i })
    expect(link).toHaveAttribute('href', '/sync')
  })

  it('Should_StillRenderExportAndImportSections', () => {
    renderWithProviders(<DataPortabilityPage />)
    expect(screen.getByRole('heading', { name: 'Export' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Import' })).toBeInTheDocument()
  })
})
