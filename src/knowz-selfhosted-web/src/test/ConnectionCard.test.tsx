import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { renderWithProviders } from './test-utils'
import ConnectionCard from '../components/platform-sync/ConnectionCard'
import type { PlatformConnectionDto } from '../lib/types'

vi.mock('../lib/api-client', () => ({
  api: {
    testPlatformConnectionCandidate: vi.fn(),
    testPlatformConnection: vi.fn(),
    upsertPlatformConnection: vi.fn(),
    deletePlatformConnection: vi.fn(),
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

import { api } from '../lib/api-client'

const mockTestCandidate = vi.mocked(api.testPlatformConnectionCandidate)
const mockTestExisting = vi.mocked(api.testPlatformConnection)
const mockUpsert = vi.mocked(api.upsertPlatformConnection)
const mockDelete = vi.mocked(api.deletePlatformConnection)

const connectedDto: PlatformConnectionDto = {
  platformApiUrl: 'https://api.knowz.io',
  displayName: 'Production Knowz',
  hasApiKey: true,
  apiKeyMask: 'ukz_****ABCD',
  remoteTenantId: '12345678-1234-1234-1234-123456789abc',
  lastTestedAt: '2026-03-01T10:00:00Z',
  lastTestStatus: 'Ok',
  lastTestError: null,
  updatedAt: '2026-03-01T10:00:00Z',
}

describe('ConnectionCard', () => {
  beforeEach(() => {
    mockTestCandidate.mockResolvedValue({
      status: 'Ok',
      message: 'Connection test succeeded.',
      remoteTenantId: 'abc',
      schemaVersion: '1.0',
    })
    mockTestExisting.mockResolvedValue({
      status: 'Ok',
      message: 'Connection is healthy.',
      remoteTenantId: 'abc',
      schemaVersion: '1.0',
    })
    mockUpsert.mockResolvedValue(connectedDto)
    mockDelete.mockResolvedValue(undefined as unknown as void)
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('Should_RenderNotConnectedState_WhenNoConnection', () => {
    renderWithProviders(<ConnectionCard connection={null} linkCount={0} />)
    expect(screen.getByText('Not Connected')).toBeInTheDocument()
    expect(screen.getByPlaceholderText('https://api.knowz.io')).toBeInTheDocument()
  })

  it('Should_RenderConnectedDetailsWithMask_WhenConnected', () => {
    renderWithProviders(<ConnectionCard connection={connectedDto} linkCount={2} />)
    expect(screen.getByText('Connected')).toBeInTheDocument()
    expect(screen.getByText('https://api.knowz.io')).toBeInTheDocument()
    expect(screen.getByText('ukz_****ABCD')).toBeInTheDocument()
    expect(screen.getByText('Production Knowz')).toBeInTheDocument()
  })

  it('Should_ShowEditForm_WhenEditClicked', async () => {
    const user = userEvent.setup()
    renderWithProviders(<ConnectionCard connection={connectedDto} linkCount={0} />)
    await user.click(screen.getByRole('button', { name: /edit/i }))
    expect(screen.getByPlaceholderText('https://api.knowz.io')).toBeInTheDocument()
    expect(screen.getByPlaceholderText('ukz_...')).toBeInTheDocument()
  })

  it('Should_RenderApiKeyInputAsPasswordType_WithAutoCompleteOff', () => {
    renderWithProviders(<ConnectionCard connection={null} linkCount={0} />)
    const apiKeyInput = screen.getByPlaceholderText('ukz_...') as HTMLInputElement
    expect(apiKeyInput.type).toBe('password')
    expect(apiKeyInput.getAttribute('autoComplete')).toBe('off')
  })

  it('Should_CallTestCandidate_WhenTestClickedInEditForm', async () => {
    const user = userEvent.setup()
    renderWithProviders(<ConnectionCard connection={null} linkCount={0} />)
    await user.type(screen.getByPlaceholderText('https://api.knowz.io'), 'https://api.knowz.io')
    await user.type(screen.getByPlaceholderText('ukz_...'), 'ukz_secret')
    await user.click(screen.getByRole('button', { name: /^test$/i }))
    await waitFor(() => {
      expect(mockTestCandidate).toHaveBeenCalledWith('https://api.knowz.io', 'ukz_secret')
    })
  })

  it('Should_CallUpsertAndClearApiKey_WhenSaveClicked', async () => {
    const user = userEvent.setup()
    renderWithProviders(<ConnectionCard connection={null} linkCount={0} />)
    const urlInput = screen.getByPlaceholderText('https://api.knowz.io')
    const keyInput = screen.getByPlaceholderText('ukz_...') as HTMLInputElement
    await user.type(urlInput, 'https://api.knowz.io')
    await user.type(keyInput, 'ukz_secret')
    await user.click(screen.getByRole('button', { name: /save/i }))
    await waitFor(() => {
      expect(mockUpsert).toHaveBeenCalledWith({
        platformApiUrl: 'https://api.knowz.io',
        displayName: null,
        apiKey: 'ukz_secret',
      })
    })
    // V-SEC-04: Plaintext cleared after save
    await waitFor(() => {
      expect(screen.getByText('Connection saved.')).toBeInTheDocument()
    })
  })

  it('Should_ShowDisconnectModal_WhenDisconnectClicked', async () => {
    const user = userEvent.setup()
    renderWithProviders(<ConnectionCard connection={connectedDto} linkCount={3} />)
    await user.click(screen.getByRole('button', { name: /^disconnect$/i }))
    expect(screen.getByText('Disconnect Platform?')).toBeInTheDocument()
  })

  it('Should_ShowLinkCountInDisconnectModal_WhenLinksExist', async () => {
    const user = userEvent.setup()
    renderWithProviders(<ConnectionCard connection={connectedDto} linkCount={3} />)
    await user.click(screen.getByRole('button', { name: /^disconnect$/i }))
    expect(screen.getByText(/3 vault sync links/)).toBeInTheDocument()
  })

  it('Should_DisableSave_WhenUrlOrKeyMissing', () => {
    renderWithProviders(<ConnectionCard connection={null} linkCount={0} />)
    expect(screen.getByRole('button', { name: /save/i })).toBeDisabled()
  })

  it('Should_CallTestExisting_WhenTestConnectionClickedOnConnectedView', async () => {
    const user = userEvent.setup()
    renderWithProviders(<ConnectionCard connection={connectedDto} linkCount={0} />)
    await user.click(screen.getByRole('button', { name: /test connection/i }))
    await waitFor(() => {
      expect(mockTestExisting).toHaveBeenCalled()
    })
  })

  it('Should_ShowViewMode_WhenConnectionLoadsAsync', async () => {
    // Regression guard: the parent's query resolves async, flipping `connection` from
    // null → connected. Because `isEditing` was initialized from !isConnected at mount
    // time, the card used to stay stuck in edit mode after reload. The useEffect sync
    // must flip to view mode as soon as the connected prop arrives.
    const { rerender } = renderWithProviders(
      <ConnectionCard connection={null} linkCount={0} />,
    )
    // Initially not connected → edit mode (URL input visible).
    expect(screen.getByPlaceholderText('https://api.knowz.io')).toBeInTheDocument()

    // Simulate async prop resolution after the query settles.
    rerender(<ConnectionCard connection={connectedDto} linkCount={0} />)

    // Now should be in view mode: masked key visible, URL input gone.
    await waitFor(() => {
      expect(screen.getByText('ukz_****ABCD')).toBeInTheDocument()
    })
    expect(screen.queryByPlaceholderText('ukz_...')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: /edit/i })).toBeInTheDocument()
  })
})

// ---------- UI_SH_DestinationsPolish : S8 key-kind + host guidance ----------
describe('ConnectionCard — key-kind and host guidance (S8, VERIFY-SH-7)', () => {
  // `vi.clearAllMocks()` clears calls but keeps implementations, so both test
  // doubles are re-seeded per test to stop an Unauthorized result leaking forward.
  beforeEach(() => {
    mockTestCandidate.mockResolvedValue({
      status: 'Ok',
      message: 'Connection test succeeded.',
      remoteTenantId: 'abc',
      schemaVersion: '1.0',
    })
    mockTestExisting.mockResolvedValue({
      status: 'Ok',
      message: 'Connection is healthy.',
      remoteTenantId: 'abc',
      schemaVersion: '1.0',
    })
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('Should_ExplainPersonalKeyRequirement_UnderTheApiKeyInput', () => {
    renderWithProviders(<ConnectionCard connection={null} linkCount={0} />)
    const guidance = screen.getByTestId('api-key-guidance')
    expect(guidance.textContent).toContain('ukz_')
    expect(guidance.textContent).toContain('kz_')
    expect(guidance.textContent).toMatch(/rejected/i)
    // The existing security note must survive alongside the new guidance.
    expect(
      screen.getByText(/API key is never displayed after save/i),
    ).toBeInTheDocument()
  })

  it('Should_StateTheAllowlistedHosts_UnderTheUrlInput', () => {
    renderWithProviders(<ConnectionCard connection={null} linkCount={0} />)
    const guidance = screen.getByTestId('cloud-url-guidance')
    expect(guidance.textContent).toContain('https://api.knowz.io')
    expect(guidance.textContent).toContain('https://api.dev.knowz.io')
    expect(guidance.textContent).toMatch(/not allowed/i)
  })

  it('Should_AppendKeyHintToBanner_WhenCandidateTestIsUnauthorized', async () => {
    const user = userEvent.setup()
    mockTestCandidate.mockResolvedValue({
      status: 'Unauthorized',
      message: 'The API key was rejected.',
      remoteTenantId: null,
      schemaVersion: null,
    })
    renderWithProviders(<ConnectionCard connection={null} linkCount={0} />)
    await user.type(screen.getByPlaceholderText('https://api.knowz.io'), 'https://api.knowz.io')
    await user.type(screen.getByPlaceholderText('ukz_...'), 'kz_tenantkey')
    await user.click(screen.getByRole('button', { name: /^test$/i }))

    const banner = await screen.findByTestId('connection-banner')
    expect(banner.textContent).toContain('The API key was rejected.')
    expect(banner.textContent).toContain('personal ukz_ key')
  })

  it('Should_AppendKeyHintToBanner_WhenExistingConnectionTestIsUnauthorized', async () => {
    const user = userEvent.setup()
    mockTestExisting.mockResolvedValue({
      status: 'Unauthorized',
      message: 'The API key was rejected.',
      remoteTenantId: null,
      schemaVersion: null,
    })
    renderWithProviders(<ConnectionCard connection={connectedDto} linkCount={0} />)
    await user.click(screen.getByRole('button', { name: /test connection/i }))

    const banner = await screen.findByTestId('connection-banner')
    expect(banner.textContent).toContain('personal ukz_ key')
  })

  it('Should_NotAppendKeyHint_WhenTestSucceeds', async () => {
    const user = userEvent.setup()
    renderWithProviders(<ConnectionCard connection={connectedDto} linkCount={0} />)
    await user.click(screen.getByRole('button', { name: /test connection/i }))

    const banner = await screen.findByTestId('connection-banner')
    expect(banner.textContent).not.toContain('personal ukz_ key')
  })
})
