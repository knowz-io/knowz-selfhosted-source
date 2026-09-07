import { it, expect } from 'vitest'
import { screen } from '@testing-library/react'
import { renderWithProviders } from './test-utils'
import AiUnavailableNotice from '../components/AiUnavailableNotice'
import { AuthContext } from '../lib/auth'
it('offers provider configuration to superadmin and capture/search to everyone', () => {
  renderWithProviders(<AuthContext.Provider value={{ user: { role: 2 } } as never}><AiUnavailableNotice /></AuthContext.Provider>)
  expect(screen.getByRole('link', { name: /configure ai/i })).toHaveAttribute('href', '/admin/settings')
  expect(screen.getByRole('link', { name: /search/i })).toHaveAttribute('href', '/search')
})
it('gives non-admins useful actions without sending them to connection settings', () => {
  renderWithProviders(<AiUnavailableNotice />)
  expect(screen.queryByRole('link', { name: /configure ai/i })).not.toBeInTheDocument()
  expect(screen.getByRole('link', { name: /capture/i })).toHaveAttribute('href', '/knowledge/new')
  expect(screen.queryByText(/Settings.*Connection/)).not.toBeInTheDocument()
})
