import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, screen, waitFor } from '@testing-library/react'
import { renderWithProviders } from './test-utils'

const { loginWithToken, changePassword } = vi.hoisted(() => ({
  loginWithToken: vi.fn(),
  changePassword: vi.fn(),
}))

vi.mock('../lib/auth', () => ({
  useAuth: () => ({ loginWithToken }),
}))

vi.mock('../lib/api-client', () => ({
  api: { changePassword },
  ApiError: class ApiError extends Error { status = 400 },
}))

import FirstLoginPasswordPage from '../pages/FirstLoginPasswordPage'

describe('FirstLoginPasswordPage', () => {
  beforeEach(() => vi.clearAllMocks())

  it('explains the generated credential and requires current/new/confirm values', () => {
    renderWithProviders(<FirstLoginPasswordPage />, { initialEntries: ['/first-login'] })

    expect(screen.getByRole('heading', { name: /choose your permanent password/i })).toBeInTheDocument()
    expect(screen.getByText(/installer generated a temporary password/i)).toBeInTheDocument()
    expect(screen.getByLabelText(/current password/i)).toBeInTheDocument()
    expect(screen.getByLabelText(/^new password/i)).toBeInTheDocument()
    expect(screen.getByLabelText(/confirm new password/i)).toBeInTheDocument()
  })

  it('stores the refreshed normal token after a valid change', async () => {
    changePassword.mockResolvedValue({ token: 'normal-token', user: { mustChangePassword: false } })
    renderWithProviders(<FirstLoginPasswordPage />, { initialEntries: ['/first-login'] })

    fireEvent.change(screen.getByLabelText(/current password/i), { target: { value: 'temporary' } })
    fireEvent.change(screen.getByLabelText(/^new password/i), { target: { value: 'N3w!River-Stone-84' } })
    fireEvent.change(screen.getByLabelText(/confirm new password/i), { target: { value: 'N3w!River-Stone-84' } })
    fireEvent.click(screen.getByRole('button', { name: /change password/i }))

    await waitFor(() => expect(changePassword).toHaveBeenCalledWith('temporary', 'N3w!River-Stone-84'))
    expect(loginWithToken).toHaveBeenCalledWith('normal-token')
  })
})
