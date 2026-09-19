import { describe, it, expect, vi, beforeEach } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import { readFileSync, readdirSync } from 'node:fs'
import { resolve, join } from 'node:path'
import { renderWithProviders } from './test-utils'
import { UserRole } from '../lib/types'
import { toUserError } from '../lib/toUserError'
import { getPageMeta, isPageMetaSuppressed } from '../components/page-meta-config'
import { NAV_ITEMS } from '../components/nav-config'
import { categoryLabel, canRevealSecrets } from '../pages/admin/AdminSettingsPage'
import { APP_VERSION } from '../components/AppVersionFooter'
import Layout from '../components/Layout'

let tenantList: { id: string; name: string }[] = [{ id: 't', name: 'Test Tenant' }]

vi.mock('../lib/auth', () => ({
  useAuth: () => ({
    user: {
      id: 'u', username: 'testuser', email: null, displayName: 'Test User',
      role: UserRole.SuperAdmin, tenantId: 't', tenantName: 'Test Tenant',
      isActive: true, apiKey: null, createdAt: '2026-01-01T00:00:00Z', lastLoginAt: null,
    },
    isAuthenticated: true,
    logout: vi.fn(),
    activeTenantId: null,
    setActiveTenantId: vi.fn(),
    availableTenants: tenantList,
    login: vi.fn(),
  }),
}))

vi.mock('../lib/theme', () => ({ useTheme: () => ({ theme: 'light', toggle: vi.fn() }) }))

vi.mock('../lib/api-client', () => ({
  api: {
    listTenants: vi.fn(() => Promise.resolve(tenantList)),
    getConfigCategories: vi.fn(() => Promise.resolve([])),
    getConfigStatus: vi.fn(() => Promise.resolve({})),
  },
  ApiError: class ApiError extends Error {
    status: number
    constructor(status: number, message: string) { super(message); this.status = status; this.name = 'ApiError' }
  },
}))

import Header from '../components/Header'

const read = (p: string) => readFileSync(resolve(__dirname, p), 'utf8')

const SRC_ROOT = resolve(__dirname, '..')
function* walkSrc(dir: string = SRC_ROOT): Generator<string> {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const full = join(dir, entry.name)
    if (entry.isDirectory()) yield* walkSrc(full)
    else if (/\.(ts|tsx)$/.test(entry.name)) yield full
  }
}

// ---------- VERIFY-C1 / C8 : one name ----------
describe('SH_InstanceCopyHygiene — one name for the Destinations feature (C1, C8)', () => {
  const NAME = 'Destinations'

  it('Should_UseTheSameLiteral_AtNavLabel_PageTitle_And_H1', () => {
    const navItem = NAV_ITEMS.find((i) => i.path === '/sync')
    expect(navItem?.label).toBe(NAME)
    expect(getPageMeta('/sync').title).toBe(NAME)
    expect(read('../pages/admin/PlatformSyncPage.tsx')).toContain(`<h1 className="text-2xl font-bold">${NAME}</h1>`)
    expect(read('../components/platform-sync/BrowsePlatformModal.tsx')).toContain('Browse Knowz Cloud')
    // GAP-3 lead ruling: only the page <h1> carries the feature name. The card
    // heading is a plain section heading so the route renders exactly one.
    expect(read('../components/platform-sync/ConnectionCard.tsx'))
      .toContain('<h2 className="text-lg font-semibold">Connection</h2>')
    expect(read('../components/platform-sync/ConnectionCard.tsx')).not.toContain(NAME)
  })

  it('Should_RenderExactlyOneFeatureHeading_OnTheDestinationsRoute', () => {
    // VERIFY-C8 / M7: page-meta strip suppressed + card heading demoted, so the
    // literal feature name appears as a heading exactly once on /sync.
    const pageSource = read('../pages/admin/PlatformSyncPage.tsx')
    const cardSource = read('../components/platform-sync/ConnectionCard.tsx')
    const headings = [...pageSource.matchAll(/<h[12][^>]*>([^<]*Destinations[^<]*)<\/h[12]>/g)]
    expect(headings).toHaveLength(1)
    expect(cardSource).not.toMatch(/<h[12][^>]*>[^<]*Destinations/)
    expect(isPageMetaSuppressed('/sync')).toBe(true)
  })

  it('Should_NotLeakTheWordPlatform_IntoUserVisibleCopy', () => {
    // GAP-2 regression lock. Case-insensitive so `Platform connection` and
    // `Platform Connection` are both caught, plus the `Knowz Platform` brand.
    const pattern = /platform (sync|connection)|browse platform|connect knowz|\bknowz platform\b/i
    const offenders: string[] = []
    for (const file of walkSrc()) {
      if (file.includes('/test/') || file.includes('.test.')) continue
      const line = readFileSync(file, 'utf8')
        .split('\n')
        .findIndex((l) => pattern.test(l))
      if (line >= 0) offenders.push(`${file}:${line + 1}`)
    }
    expect(offenders).toEqual([])
  })

  it('Should_SuppressPageMetaStrip_OnRoutesOwningTheirH1', () => {
    expect(isPageMetaSuppressed('/sync')).toBe(true)
    expect(isPageMetaSuppressed('/destinations')).toBe(true)
    expect(isPageMetaSuppressed('/admin/platform-sync')).toBe(true)
    expect(isPageMetaSuppressed('/knowledge')).toBe(false)
  })

  it('Should_NotRenderPageMetaStrip_OnTheDestinationsRoute', () => {
    renderWithProviders(<Header />, { initialEntries: ['/sync'] })
    expect(screen.queryByTestId('sh-pagemeta')).toBeNull()
  })
})

// ---------- VERIFY-C5 : single-tenant chrome ----------
describe('SH_InstanceCopyHygiene — single-tenant chrome gate (C5)', () => {
  beforeEach(() => { tenantList = [{ id: 't', name: 'Test Tenant' }] })

  it('Should_HideCrossTenantSelectAndViewingPill_WhenOnlyOneTenant', async () => {
    renderWithProviders(<Header />, { initialEntries: ['/'] })
    await waitFor(() => expect(screen.getByTestId('sh-header')).toBeInTheDocument())
    expect(screen.queryByTestId('sh-superadmin-tenant-select')).toBeNull()
    expect(screen.queryByTestId('sh-superadmin-viewing-pill')).toBeNull()
  })

  it('Should_KeepTenantRoutesRegistered', () => {
    const appSource = read('../App.tsx')
    expect(appSource).toContain('/admin/tenants')
  })
})

// ---------- VERIFY-C11 : document.title ----------
describe('SH_InstanceCopyHygiene — per-route document.title (C11)', () => {
  it('Should_SetDistinctTitlesEndingInKnowzSelfHosted', () => {
    renderWithProviders(<Header />, { initialEntries: ['/chat'] })
    const chatTitle = document.title
    renderWithProviders(<Header />, { initialEntries: ['/search'] })
    const searchTitle = document.title
    expect(chatTitle).not.toBe(searchTitle)
    expect(chatTitle.endsWith('Knowz Self-Hosted')).toBe(true)
    expect(searchTitle.endsWith('Knowz Self-Hosted')).toBe(true)
  })
})

// ---------- VERIFY-C12 : toUserError ----------
describe('SH_InstanceCopyHygiene — toUserError (C12)', () => {
  it('Should_StripDatabaseInternals_FromRawError', () => {
    const copy = toUserError(new Error('42P01: relation "Knowledge" does not exist'))
    expect(copy).not.toContain('42P01')
    expect(copy).not.toContain('relation')
    expect(copy.length).toBeGreaterThan(0)
  })

  it('Should_MapKnownHttpStatuses_ToSentences', () => {
    const notFound = toUserError({ status: 404, message: 'boom' })
    expect(notFound.toLowerCase()).toMatch(/find|found/)
    const forbidden = toUserError({ status: 403, message: 'boom' })
    expect(forbidden.toLowerCase()).toMatch(/permission|not allowed/)
  })

  it('Should_BeAppliedAtHighVisibilitySurfaces', () => {
    for (const f of ['../pages/KnowledgeListPage.tsx', '../pages/ChatPage.tsx',
                     '../components/platform-sync/ConnectionCard.tsx', '../pages/AskPage.tsx',
                     '../pages/SearchPage.tsx']) {
      expect(read(f)).toContain('toUserError')
    }
  })
})

// ---------- VERIFY-C13 / C14 : admin config presentation ----------
describe('SH_InstanceCopyHygiene — admin config presentation (C13, C14)', () => {
  it('Should_RenderProductLabels_ForConfigCategories', () => {
    expect(categoryLabel('ConnectionStrings')).toBe('Database')
    expect(categoryLabel('KnowzPlatform')).toBe('Knowz Cloud')
    expect(categoryLabel('SelfHosted')).toBe('Instance')
    expect(categoryLabel('AzureKeyVault')).toBe('Key Vault')
    // A displayName echoing the raw key is not a product label.
    expect(categoryLabel('ConnectionStrings', 'ConnectionStrings')).toBe('Database')
    // Unknown categories stay honest.
    expect(categoryLabel('SomethingNew')).toBe('SomethingNew')
  })

  it('Should_NeverAllowRevealing_ConnectionStrings', () => {
    expect(canRevealSecrets('ConnectionStrings')).toBe(false)
    expect(canRevealSecrets('AzureOpenAI')).toBe(true)
  })

  it('Should_TitleThePage_InstanceConfiguration', () => {
    const src = read('../pages/admin/AdminSettingsPage.tsx')
    expect(src).toContain('Instance configuration')
    expect(src).not.toContain('>Settings</h1>')
  })
})

// ---------- VERIFY-C9 / C10 : login copy + version footer ----------
describe('SH_InstanceCopyHygiene — login copy and version footer (C9, C10)', () => {
  const loginSource = read('../pages/LoginPage.tsx')

  it('Should_RenderOneFirstRunSentence_NoRegister_NoSecret', () => {
    expect(loginSource).toContain('Sign in as')
    expect(loginSource).toContain('with the password from setup')
    expect(loginSource).not.toContain('generated password was shown once')
    expect(loginSource).not.toMatch(/>\s*Register\s*</)
    expect(loginSource).not.toContain('changeme')
  })

  it('Should_RenderVersionFooter_OnLoginAndAuthenticatedShell', () => {
    expect(loginSource).toContain('sh-version-footer')
    expect(loginSource).toContain('Knowz Self-Hosted')
    expect(`Knowz Self-Hosted \u00b7 v${APP_VERSION}`).toMatch(/Knowz Self-Hosted \u00b7 v\d/)
    // GAP-1: render through the authenticated shell so this cannot pass with
    // AppVersionFooter mounted nowhere.
    renderWithProviders(<Layout><div>page</div></Layout>, { initialEntries: ['/'] })
    expect(screen.getByTestId('sh-version-footer').textContent).toMatch(/Knowz Self-Hosted \u00b7 v\d/)
  })
})
