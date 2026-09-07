import { describe, it, expect, vi, beforeEach } from 'vitest'
import { screen } from '@testing-library/react'
import { renderWithProviders } from './test-utils'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

vi.mock('../lib/auth', () => ({
  useAuth: () => ({
    login: vi.fn(),
    selectTenant: vi.fn(),
    isAuthenticated: false,
    isLoading: false,
  }),
}))

vi.mock('../lib/api-client', () => ({
  api: {
    getSSOProviders: vi.fn().mockResolvedValue({
      data: [{ provider: 'Microsoft', displayName: 'Microsoft' }],
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

import LoginPage from '../pages/LoginPage'

async function renderLogin() {
  const view = renderWithProviders(<LoginPage />, { initialEntries: ['/login'] })
  await screen.findByRole('button', { name: /microsoft/i })
  return view
}

describe('VERIFY-L5 Login lockup', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    localStorage.clear()
  })

  it('Should_UsePacificoWordmark_AndBrainIcon', async () => {
    await renderLogin()
    const wordmark = screen.getByTestId('sh-login-wordmark')
    expect(wordmark.textContent).toMatch(/knowz/i)
    expect(wordmark.getAttribute('style') ?? wordmark.className).toMatch(/Pacifico/)
    expect(screen.getByTestId('sh-login-brain-icon')).toBeInTheDocument()
  })

  it('Should_UseDarkSlateGradientSurface', async () => {
    await renderLogin()
    const surface = screen.getByTestId('sh-login-surface')
    const style = surface.getAttribute('style') ?? ''
    const className = surface.className
    expect(`${style} ${className}`).toMatch(/#0f172a|rgb\(15,\s*23,\s*42\)|slate-9/)
  })

  it('Should_KeepUsernamePasswordAndSsoFields', async () => {
    await renderLogin()
    expect(screen.getByLabelText(/username/i)).toBeInTheDocument()
    expect(screen.getByLabelText(/password/i)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /microsoft/i })).toBeInTheDocument()
  })

  it('Should_NotUseBookOpenAsBrand', () => {
    const source = readFileSync(
      resolve(__dirname, '../pages/LoginPage.tsx'),
      'utf8',
    )
    expect(source).not.toMatch(/\bBookOpen\b/)
    expect(source).toMatch(/\bBrain\b/)
    expect(source).toMatch(/Pacifico/)
    expect(source).toContain('#0f172a')
  })

  it('Should_OmitRegisterOtpHrdAndMarketplace', async () => {
    await renderLogin()
    expect(screen.queryByRole('tab', { name: /register/i })).not.toBeInTheDocument()
    expect(screen.queryByText(/one-time code|otp/i)).not.toBeInTheDocument()
    expect(screen.queryByText(/home realm|marketplace|partner/i)).not.toBeInTheDocument()
  })

  it('Should_ExplainTheSetupCreatedAdminWithoutOpeningRegistration', async () => {
    await renderLogin()
    expect(screen.getByText(/first time here/i)).toBeInTheDocument()
    // SH_InstanceCopyHygiene R5: one sentence — username `admin`, password
    // provenance "from setup", no secret value, no Register control.
    expect(screen.getByText(/sign in as/i).textContent).toMatch(/admin/i)
    expect(screen.getByText(/with the password from setup/i)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /register|sign up/i })).not.toBeInTheDocument()
  })

  it('keeps a fresh login focused on sign-in and only offers reset for a saved connection', async () => {
    const view = await renderLogin()
    expect(screen.queryByRole('button', { name: 'Use this instance' })).not.toBeInTheDocument()
    view.unmount()
    localStorage.setItem('apiUrl', 'https://wrong.example')
    await renderLogin()
    expect(screen.getByRole('button', { name: 'Use this instance' })).toBeInTheDocument()
  })
})
