import { it, expect, vi, beforeEach } from 'vitest'
import { screen, act, fireEvent } from '@testing-library/react'
import { renderWithProviders } from './test-utils'
import { AuthProvider, useAuth } from '../lib/auth'
import LoginPage from '../pages/LoginPage'
import { api } from '../lib/api-client'
vi.mock('../lib/api-client', () => ({ api: { getMe: vi.fn(), login: vi.fn(), getUserTenants: vi.fn().mockResolvedValue([]), getSSOProviders: vi.fn().mockResolvedValue({data:[]}) }, ApiError: class extends Error {} }))
beforeEach(() => { localStorage.clear(); sessionStorage.clear(); vi.clearAllMocks() })
function Session() { const auth = useAuth(); return <span>{auth.isAuthenticated ? 'old session restored' : 'signed out'}</span> }
it('allows reset while saved-host authentication is pending and ignores a late old response', async () => {
  sessionStorage.setItem('authToken', 'old-token'); localStorage.setItem('apiUrl', 'https://old.example')
  let resolve!: (v: unknown) => void
  vi.mocked(api.getMe).mockImplementation(() => new Promise(r => { resolve = r }) as never)
  renderWithProviders(<AuthProvider><LoginPage /><Session /></AuthProvider>)
  fireEvent.click(screen.getByRole('button', {name: 'Use this instance'}))
  expect(screen.getByLabelText(/username/i)).toBeInTheDocument()
  await act(async () => resolve({ id: 'old', username: 'old', role: 2 }))
  expect(screen.queryByText('old session restored')).not.toBeInTheDocument()
  expect(localStorage.getItem('apiUrl')).toBeNull()
})

function SignInAndRefresh() { const auth = useAuth(); return <><button onClick={() => auth.login('admin','temporary')}>Sign in</button><button onClick={() => auth.refreshUser()}>Password changed</button></> }
it('waits for required password rotation before loading protected tenant data', async () => {
  vi.mocked(api.login).mockResolvedValue({token:'new-token', user:{id:'admin', username:'admin', role:0, mustChangePassword:true}, requiresTenantSelection:false} as never)
  vi.mocked(api.getMe).mockResolvedValue({id:'admin', username:'admin', role:0, mustChangePassword:false} as never)
  renderWithProviders(<AuthProvider><SignInAndRefresh /></AuthProvider>)
  await act(async () => fireEvent.click(screen.getByRole('button', {name:'Sign in'})))
  expect(api.getUserTenants).not.toHaveBeenCalled()
  await act(async () => fireEvent.click(screen.getByRole('button', {name:'Password changed'})))
  expect(api.getUserTenants).toHaveBeenCalledTimes(1)
})
