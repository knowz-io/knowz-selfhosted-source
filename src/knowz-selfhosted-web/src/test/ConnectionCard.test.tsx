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
    testPlatformConnectionById: vi.fn(),
    upsertPlatformConnection: vi.fn(),
    createPlatformConnection: vi.fn(),
    updatePlatformConnection: vi.fn(),
    deletePlatformConnection: vi.fn(),
    deletePlatformConnectionById: vi.fn(),
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
const mockTestById = vi.mocked(api.testPlatformConnectionById)
const mockCreate = vi.mocked(api.createPlatformConnection)
const mockUpdate = vi.mocked(api.updatePlatformConnection)
const mockDeleteById = vi.mocked(api.deletePlatformConnectionById)

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
    expect(screen.getByTestId('platform-connection-api-key')).toBeInTheDocument()
  })

  it('Should_RenderApiKeyInputAsPasswordType_WithAutoCompleteOff', () => {
    renderWithProviders(<ConnectionCard connection={null} linkCount={0} />)
    const apiKeyInput = screen.getByTestId('platform-connection-api-key') as HTMLInputElement
    expect(apiKeyInput.type).toBe('password')
    expect(apiKeyInput.getAttribute('autoComplete')).toBe('off')
  })

  it.each(['ukz_secret', 'kz_tenantkey'])('Should_CallTestCandidate_WithSupportedKey_%s', async (key) => {
    const user = userEvent.setup()
    renderWithProviders(<ConnectionCard connection={null} linkCount={0} />)
    await user.type(screen.getByPlaceholderText('https://api.knowz.io'), 'https://api.knowz.io')
    await user.type(screen.getByTestId('platform-connection-api-key'), key)
    await user.click(screen.getByRole('button', { name: /^test$/i }))
    await waitFor(() => {
      expect(mockTestCandidate).toHaveBeenCalledWith('https://api.knowz.io', key)
    })
  })

  it.each(['ukz_secret', 'kz_tenantkey'])('Should_CallUpsertAndClearApiKey_WhenSaving_%s', async (key) => {
    const user = userEvent.setup()
    renderWithProviders(<ConnectionCard connection={null} linkCount={0} />)
    const urlInput = screen.getByPlaceholderText('https://api.knowz.io')
    const keyInput = screen.getByTestId('platform-connection-api-key') as HTMLInputElement
    await user.type(urlInput, 'https://api.knowz.io')
    await user.type(keyInput, key)
    await user.click(screen.getByRole('button', { name: /save/i }))
    await waitFor(() => {
      expect(mockUpsert).toHaveBeenCalledWith({
        platformApiUrl: 'https://api.knowz.io',
        displayName: null,
        apiKey: key,
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
    expect(screen.queryByTestId('platform-connection-api-key')).not.toBeInTheDocument()
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

  it('Should_ExplainUserAndTenantIdentity_UnderTheApiKeyInput', () => {
    renderWithProviders(<ConnectionCard connection={null} linkCount={0} />)
    const guidance = screen.getByTestId('api-key-guidance')
    expect(guidance.textContent).toContain('ukz_')
    expect(guidance.textContent).toContain('kz_')
    expect(guidance.textContent).toMatch(/ukz_.*user/i)
    expect(guidance.textContent).toMatch(/kz_.*whole tenant/i)
    expect(guidance.textContent).toMatch(/higher blast radius/i)
    expect(guidance.textContent).not.toMatch(/rejected/i)
    expect(screen.getByTestId('platform-connection-api-key')).toHaveAttribute('placeholder', 'ukz_... or kz_...')
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
    await user.type(screen.getByTestId('platform-connection-api-key'), 'kz_tenantkey')
    await user.click(screen.getByRole('button', { name: /^test$/i }))

    const banner = await screen.findByTestId('connection-banner')
    expect(banner.textContent).toContain('The API key was rejected.')
    expect(banner.textContent).toContain('key is still active and issued by this destination')
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
    expect(banner.textContent).toContain('key is still active and issued by this destination')
  })

  it('Should_NotAppendKeyHint_WhenTestSucceeds', async () => {
    const user = userEvent.setup()
    renderWithProviders(<ConnectionCard connection={connectedDto} linkCount={0} />)
    await user.click(screen.getByRole('button', { name: /test connection/i }))

    const banner = await screen.findByTestId('connection-banner')
    expect(banner.textContent).not.toContain('key is still active and issued by this destination')
  })
})

// ---------- UI_SH_PeerDestination : peer variant on the /connections/{id} routes ----------
describe('ConnectionCard — peer variant (UI_SH_PeerDestination)', () => {
  const peerDto: PlatformConnectionDto = {
    ...connectedDto,
    id: 'c-peer',
    platformApiUrl: 'https://knowz.example.com',
    displayName: 'Office instance',
  }

  beforeEach(() => {
    mockTestById.mockResolvedValue({
      status: 'Ok',
      message: 'Peer reachable.',
      remoteTenantId: 'abc',
      schemaVersion: '1.0',
    })
    mockTestCandidate.mockResolvedValue({
      status: 'Ok',
      message: 'Connection test succeeded.',
      remoteTenantId: 'abc',
      schemaVersion: '1.0',
    })
    mockCreate.mockResolvedValue(peerDto)
    mockUpdate.mockResolvedValue(peerDto)
    mockDeleteById.mockResolvedValue(undefined as unknown as void)
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('Should_HeadTheCard_WithTheDestinationLabel_AndKindBadge', () => {
    renderWithProviders(<ConnectionCard connection={peerDto} linkCount={0} variant="peer" />)
    expect(screen.getByRole('heading', { name: 'Office instance' })).toBeInTheDocument()
    expect(screen.getByTestId('destination-kind').textContent).toBe('Allowlisted peer')
    expect(screen.queryByTestId('primary-badge')).toBeNull()
    expect(screen.getByText('https://knowz.example.com')).toBeInTheDocument()
  })

  it('Should_TestById_Update_AndDeleteById_ForAPeer', async () => {
    const user = userEvent.setup()
    renderWithProviders(<ConnectionCard connection={peerDto} linkCount={0} variant="peer" />)
    await user.click(screen.getByRole('button', { name: /test connection/i }))
    await waitFor(() => expect(mockTestById).toHaveBeenCalledWith('c-peer'))
    expect(mockTestExisting).not.toHaveBeenCalled()

    await user.click(screen.getByRole('button', { name: /edit/i }))
    const nameInput = screen.getByPlaceholderText('e.g. Office instance') as HTMLInputElement
    await user.clear(nameInput)
    await user.type(nameInput, 'Renamed peer')
    await user.click(screen.getByRole('button', { name: /^save$/i }))
    await waitFor(() =>
      expect(mockUpdate).toHaveBeenCalledWith('c-peer', {
        platformApiUrl: 'https://knowz.example.com',
        displayName: 'Renamed peer',
        apiKey: null,
      }),
    )
    expect(mockUpsert).not.toHaveBeenCalled()

    await user.click(screen.getByRole('button', { name: /^disconnect$/i }))
    expect(screen.getByText('Remove peer connection?')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: /^remove$/i }))
    await waitFor(() => expect(mockDeleteById).toHaveBeenCalledWith('c-peer'))
    expect(mockDelete).not.toHaveBeenCalled()
  })

  it.each(['ukz_peerkey', 'kz_tenantkey', 'ksh_selfhostedkey'])('Should_CreateThroughConnections_WithSupportedPeerKey_%s', async (key) => {
    const user = userEvent.setup()
    const onCreated = vi.fn()
    const onCancel = vi.fn()
    renderWithProviders(
      <ConnectionCard connection={null} linkCount={0} variant="peer" onCreated={onCreated} onCancel={onCancel} />,
    )
    expect(screen.getByRole('heading', { name: 'Add an allowlisted peer' })).toBeInTheDocument()
    const guidance = screen.getByTestId('peer-url-guidance').textContent ?? ''
    expect(guidance).toContain('Sync:AllowedPlatformHosts')
    expect(guidance).toMatch(/private and LAN peers require explicit operator opt-in/i)
    expect(screen.getByTestId('api-key-guidance').textContent).toContain('ukz_')
    expect(screen.getByTestId('api-key-guidance').textContent).toContain('kz_')
    expect(screen.getByTestId('api-key-guidance').textContent).toContain('ksh_')
    expect(screen.getByTestId('platform-connection-api-key')).toHaveAttribute('placeholder', 'ukz_... or kz_... (ksh_... for Self-Hosted peers)')

    await user.type(screen.getByPlaceholderText('https://knowz.example.com'), 'https://knowz.example.com')
    await user.type(screen.getByTestId('platform-connection-api-key'), key)
    await user.click(screen.getByRole('button', { name: /^test$/i }))
    await waitFor(() =>
      expect(mockTestCandidate).toHaveBeenCalledWith('https://knowz.example.com', key),
    )
    await user.click(screen.getByRole('button', { name: /add peer/i }))
    await waitFor(() =>
      expect(mockCreate).toHaveBeenCalledWith({
        platformApiUrl: 'https://knowz.example.com',
        displayName: null,
        apiKey: key,
      }),
    )
    await waitFor(() => expect(onCreated).toHaveBeenCalled())
    expect(mockUpsert).not.toHaveBeenCalled()
  })

  it('Should_CancelTheAddForm_ThroughTheCallback', async () => {
    const user = userEvent.setup()
    const onCancel = vi.fn()
    renderWithProviders(
      <ConnectionCard connection={null} linkCount={0} variant="peer" onCancel={onCancel} />,
    )
    await user.click(screen.getByRole('button', { name: /cancel/i }))
    expect(onCancel).toHaveBeenCalledTimes(1)
  })

  it('Should_AppendKeyHint_WhenAPeerTestIsUnauthorized', async () => {
    const user = userEvent.setup()
    mockTestById.mockResolvedValue({
      status: 'Unauthorized',
      message: 'The API key was rejected.',
      remoteTenantId: null,
      schemaVersion: null,
    })
    renderWithProviders(<ConnectionCard connection={peerDto} linkCount={0} variant="peer" />)
    await user.click(screen.getByRole('button', { name: /test connection/i }))
    const banner = await screen.findByTestId('connection-banner')
    expect(banner.textContent).toContain('key is still active and issued by this destination')
  })

  it('Should_KeepThePrimaryCloudCard_OnTheLegacyRoutes', async () => {
    const user = userEvent.setup()
    renderWithProviders(<ConnectionCard connection={connectedDto} linkCount={0} isPrimary />)
    expect(screen.getByTestId('primary-badge')).toBeInTheDocument()
    expect(screen.getByTestId('destination-kind').textContent).toBe('Knowz Cloud')
    await user.click(screen.getByRole('button', { name: /test connection/i }))
    await waitFor(() => expect(mockTestExisting).toHaveBeenCalled())
    expect(mockTestById).not.toHaveBeenCalled()
  })
})
