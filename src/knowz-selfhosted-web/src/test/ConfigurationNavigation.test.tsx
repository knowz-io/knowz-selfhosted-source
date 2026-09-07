import { beforeEach, describe, expect, it, vi } from 'vitest'
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryRouter, RouterProvider, useNavigate } from 'react-router-dom'
import AdminSettingsPage from '../pages/admin/AdminSettingsPage'
import { api } from '../lib/api-client'

vi.mock('../lib/api-client', () => ({ api: {
  getConfigCategories: vi.fn(), getConfigStatus: vi.fn(), updateConfigCategory: vi.fn(),
} }))
const categories = ['AzureOpenAI', 'OpenAiCompatible'].map((category) => ({
  category, displayName: category, entries: [{ key: 'Endpoint', value: `https://${category}.example`, isSecret: false, editable: true }],
}))
function SettingsRoute() {
  const navigate = useNavigate()
  return <><button onClick={() => navigate('/destination')}>Programmatic navigation</button><AdminSettingsPage /></>
}
function setup() {
  const router = createMemoryRouter([
    { path: '/admin/settings', element: <SettingsRoute /> },
    { path: '/destination', element: <h1>Destination</h1> },
  ], { initialEntries: ['/destination', '/admin/settings'], initialIndex: 1 })
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false } } })
  render(<QueryClientProvider client={client}><RouterProvider router={router} /></QueryClientProvider>)
  return router
}
async function edit(category = 'AzureOpenAI') {
  fireEvent.click(await screen.findByRole('button', { name: category, exact: true }))
  fireEvent.change(screen.getByRole('textbox', { name: 'Endpoint' }), { target: { value: `https://draft-${category}.example` } })
}
async function back(router: ReturnType<typeof setup>) { await act(async () => { await router.navigate(-1) }) }
async function dialog() { return within(await screen.findByRole('dialog', { name: 'Unsaved configuration changes' })) }
beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.getConfigCategories).mockResolvedValue(categories as never)
  vi.mocked(api.getConfigStatus).mockResolvedValue({ restartRequired: false, startupTime: '2026-09-07T00:00:00Z' } as never)
  vi.mocked(api.updateConfigCategory).mockResolvedValue({ success: true, restartRequired: true } as never)
  // jsdom has no native modal implementation; browser coverage verifies focus trapping.
  HTMLDialogElement.prototype.showModal = function () { this.setAttribute('open', ''); this.querySelector<HTMLElement>('[autofocus]')?.focus() }
  HTMLDialogElement.prototype.close = function () { this.removeAttribute('open') }
})
describe('configuration navigation protection', () => {
  it('blocks Back, keeps the draft on Stay, and never writes implicitly', async () => {
    const router = setup(); await edit(); await back(router)
    const prompt = await dialog()
    expect(router.state.location.pathname).toBe('/admin/settings')
    fireEvent.click(prompt.getByRole('button', { name: 'Stay' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(screen.getByRole('textbox', { name: 'Endpoint' })).toHaveValue('https://draft-AzureOpenAI.example')
    expect(api.updateConfigCategory).not.toHaveBeenCalled()
  })
  it('blocks programmatic navigation once for multiple dirty categories and leaves only on explicit discard', async () => {
    const router = setup(); await edit(); await edit('OpenAiCompatible')
    fireEvent.click(screen.getByRole('button', { name: 'Programmatic navigation' }))
    const prompt = await dialog(); expect(screen.getAllByRole('dialog')).toHaveLength(1)
    fireEvent.click(prompt.getByRole('button', { name: 'Leave without saving' }))
    expect(await screen.findByRole('heading', { name: 'Destination' })).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/destination')
    expect(api.updateConfigCategory).not.toHaveBeenCalled()
  })
  it('saves only each dirty category patch once before resuming the blocked Back', async () => {
    const router = setup(); await edit(); await edit('OpenAiCompatible'); await back(router)
    fireEvent.click((await dialog()).getByRole('button', { name: 'Save changes and leave' }))
    expect(await screen.findByRole('heading', { name: 'Destination' })).toBeInTheDocument()
    expect(api.updateConfigCategory).toHaveBeenCalledTimes(2)
    for (const category of ['AzureOpenAI', 'OpenAiCompatible']) expect(api.updateConfigCategory).toHaveBeenCalledWith(category, [{ key: 'Endpoint', value: `https://draft-${category}.example` }])
    expect(router.state.location.pathname).toBe('/destination')
  })
  it('keeps failed saves and the dialog for recovery, and restores focus on Stay', async () => {
    vi.mocked(api.updateConfigCategory).mockRejectedValueOnce(new Error('Concurrent modification detected'))
    setup(); await edit()
    const trigger = screen.getByRole('button', { name: 'Programmatic navigation' }); trigger.focus(); fireEvent.click(trigger)
    const prompt = await dialog(); fireEvent.click(prompt.getByRole('button', { name: 'Save changes and leave' }))
    expect(await prompt.findByRole('alert')).toHaveTextContent('Concurrent modification detected')
    expect(screen.queryByRole('heading', { name: 'Destination' })).not.toBeInTheDocument()
    fireEvent.click(prompt.getByRole('button', { name: 'Stay' }))
    await waitFor(() => expect(trigger).toHaveFocus())
    expect(screen.getByRole('textbox', { name: 'Endpoint' })).toHaveValue('https://draft-AzureOpenAI.example')
  })
  it('stays after a later category fails and retries only the remaining dirty category', async () => {
    vi.mocked(api.updateConfigCategory)
      .mockResolvedValueOnce({ success: true } as never)
      .mockRejectedValueOnce(new Error('Second category conflict'))
    const router = setup(); await edit(); await edit('OpenAiCompatible'); await back(router)
    const prompt = await dialog(); fireEvent.click(prompt.getByRole('button', { name: 'Save changes and leave' }))
    expect(await prompt.findByRole('alert')).toHaveTextContent('Second category conflict')
    expect(router.state.location.pathname).toBe('/admin/settings')
    expect(screen.getByRole('textbox', { name: 'Endpoint' })).toHaveValue('https://draft-OpenAiCompatible.example')
    fireEvent.click(prompt.getByRole('button', { name: 'Save changes and leave' }))
    expect(await screen.findByRole('heading', { name: 'Destination' })).toBeInTheDocument()
    expect(vi.mocked(api.updateConfigCategory).mock.calls.map(call => call[0])).toEqual(['AzureOpenAI', 'OpenAiCompatible', 'OpenAiCompatible'])
  })
  it('does not leave or issue duplicate writes while a save is pending', async () => {
    let resolve!: (value: never) => void
    vi.mocked(api.updateConfigCategory).mockReturnValueOnce(new Promise(resolvePromise => { resolve = resolvePromise }))
    const router = setup(); await edit(); await back(router)
    const prompt = await dialog(); fireEvent.click(prompt.getByRole('button', { name: 'Save changes and leave' }))
    await waitFor(() => expect(api.updateConfigCategory).toHaveBeenCalledTimes(1))
    expect(prompt.getByRole('button', { name: 'Leave without saving' })).toBeDisabled()
    await act(async () => { resolve({ success: true } as never) })
    expect(await screen.findByRole('heading', { name: 'Destination' })).toBeInTheDocument()
  })
  it('allows Stay during an in-flight save without navigating when that save completes', async () => {
    let resolve!: (value: never) => void
    vi.mocked(api.updateConfigCategory).mockReturnValueOnce(new Promise(resolvePromise => { resolve = resolvePromise }))
    const router = setup(); await edit(); await back(router)
    const prompt = await dialog(); fireEvent.click(prompt.getByRole('button', { name: 'Save changes and leave' }))
    await waitFor(() => expect(api.updateConfigCategory).toHaveBeenCalledTimes(1))
    fireEvent.click(prompt.getByRole('button', { name: 'Stay' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    await act(async () => { resolve({ success: true } as never) })
    expect(router.state.location.pathname).toBe('/admin/settings')
    expect(screen.queryByRole('heading', { name: 'Destination' })).not.toBeInTheDocument()
  })
  it('allows successfully saved navigation without prompting', async () => {
    const router = setup(); await edit()
    fireEvent.click(screen.getByRole('button', { name: 'Save Changes', exact: true }))
    await screen.findByText('Saved'); await back(router)
    expect(await screen.findByRole('heading', { name: 'Destination' })).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })
  it('allows explicit discard then normal Back without another prompt', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    const router = setup(); await edit()
    fireEvent.click(screen.getByRole('button', { name: 'Discard changes' })); await back(router)
    expect(await screen.findByRole('heading', { name: 'Destination' })).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    vi.mocked(window.confirm).mockRestore()
  })
  it('retains drafts during same-page hash navigation and warns only while dirty on reload', async () => {
    const router = setup(); await edit()
    await act(async () => { await router.navigate('/admin/settings#details') })
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByRole('textbox', { name: 'Endpoint' })).toHaveValue('https://draft-AzureOpenAI.example')
    const event = new Event('beforeunload', { cancelable: true }); window.dispatchEvent(event)
    expect(event.defaultPrevented).toBe(true)
    fireEvent.click(screen.getByRole('button', { name: 'Save Changes', exact: true })); await screen.findByText('Saved')
    const cleanEvent = new Event('beforeunload', { cancelable: true }); window.dispatchEvent(cleanEvent)
    expect(cleanEvent.defaultPrevented).toBe(false)
  })
})
