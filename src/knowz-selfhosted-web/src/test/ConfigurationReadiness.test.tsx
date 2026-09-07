import { describe, it, expect, vi, beforeEach } from 'vitest'
import { screen, fireEvent, waitFor } from '@testing-library/react'
import { renderWithProviders } from './test-utils'
import AdminSettingsPage from '../pages/admin/AdminSettingsPage'
import { api } from '../lib/api-client'

vi.mock('../lib/api-client', () => ({ api: {
  getConfigCategories: vi.fn(), getConfigStatus: vi.fn().mockResolvedValue({ restartRequired: false, startupTime: '2026-09-07T00:00:00Z' }),
  updateConfigCategory: vi.fn().mockResolvedValue({ success: true, restartRequired: true }), testConfigHealth: vi.fn(),
} }))
const entry = (key: string, value: string, isSecret = false) => ({ key, value, isSecret, editable: !isSecret, isSet: true, requiresRestart: true, effectiveIsSet: true, authority: 'environment' })
const categories = [
  { category: 'ConnectionStrings', displayName: 'Database', entries: [entry('McpDb', '****1234', true)] },
  { category: 'AzureOpenAI', displayName: 'Azure OpenAI', entries: [entry('Endpoint', 'https://azure.example'), entry('DeploymentName', 'chat'), entry('ApiKey', '****abcd', true)] },
  { category: 'OpenAiCompatible', displayName: 'OpenAI-compatible', entries: [entry('Endpoint', 'http://localhost:11434/v1'), entry('ChatModel', 'local')] },
]
beforeEach(() => { vi.clearAllMocks(); vi.mocked(api.getConfigCategories).mockResolvedValue(categories as never) })
describe('configuration readiness', () => {
  it('preserves independent category drafts and sends only dirty editable fields', async () => {
    renderWithProviders(<AdminSettingsPage />)
    fireEvent.click(await screen.findByRole('button', { name: 'Azure OpenAI', exact: true }))
    fireEvent.change(screen.getByDisplayValue('https://azure.example'), { target: { value: 'https://changed.example' } })
    fireEvent.click(screen.getByRole('button', { name: 'Database', exact: true }))
    fireEvent.click(screen.getByRole('button', { name: 'Azure OpenAI', exact: true }))
    expect(screen.getByDisplayValue('https://changed.example')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: /save changes/i }))
    await waitFor(() => expect(api.updateConfigCategory).toHaveBeenCalledWith('AzureOpenAI', [{ key: 'Endpoint', value: 'https://changed.example' }]))
  })
  it('renders native provider metadata and keeps managed secrets read-only', async () => {
    renderWithProviders(<AdminSettingsPage />)
    expect(await screen.findByRole('button', { name: 'OpenAI-compatible', exact: true })).toBeInTheDocument()
    expect(screen.getByDisplayValue('****1234')).toHaveAttribute('readonly')
    expect(screen.queryByTestId('sh-config-reveal-toggle')).not.toBeInTheDocument()
  })
})
it('selects the first advertised category when Database is absent', async () => {
  vi.mocked(api.getConfigCategories).mockResolvedValue([categories[2]] as never)
  renderWithProviders(<AdminSettingsPage />)
  expect(await screen.findByRole('textbox', { name: 'Endpoint' })).toHaveValue('http://localhost:11434/v1')
})
it('retains a failed draft through refresh until explicitly discarded', async () => {
  vi.mocked(api.updateConfigCategory).mockRejectedValueOnce(new Error('Concurrent modification detected'))
  const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)
  renderWithProviders(<AdminSettingsPage />)
  fireEvent.click(await screen.findByRole('button', {name:'Azure OpenAI', exact:true}))
  fireEvent.change(screen.getByDisplayValue('https://azure.example'), { target:{value:'https://draft.example'} })
  fireEvent.click(screen.getByRole('button',{name:/save changes/i}))
  expect(await screen.findByText('Concurrent modification detected')).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button',{name:'Refresh',exact:true}))
  expect(screen.getByDisplayValue('https://draft.example')).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button',{name:'Discard changes',exact:true}))
  await waitFor(() => expect(screen.getByDisplayValue('https://azure.example')).toBeInTheDocument())
  confirm.mockRestore()
})
it('clears restart guidance only when the server reports the saved settings are applied', async () => {
  vi.mocked(api.getConfigStatus).mockResolvedValue({restartRequired:true, startupTime:'2026-09-07T00:00:00Z', restartReasons:['AzureOpenAI:Endpoint']} as never)
  renderWithProviders(<AdminSettingsPage />)
  fireEvent.click(await screen.findByRole('button', {name:'Azure OpenAI', exact:true}))
  fireEvent.change(screen.getByDisplayValue('https://azure.example'), {target:{value:'https://changed.example'}})
  fireEvent.click(screen.getByRole('button',{name:/save changes/i}))
  await screen.findByText('Saved')
  expect(screen.getByText('Configuration changes require a restart to take effect.')).toBeInTheDocument()
  vi.mocked(api.getConfigStatus).mockResolvedValue({restartRequired:false, startupTime:'2026-09-07T00:00:00Z'} as never)
  fireEvent.click(screen.getByRole('button',{name:'Refresh',exact:true}))
  await waitFor(() => expect(screen.queryByText('Configuration changes require a restart to take effect.')).not.toBeInTheDocument())
})
