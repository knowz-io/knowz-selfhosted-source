// Header navigation smoke — first functional Playwright coverage for the
// selfhosted shell nav.
//
// FIXTURE APPROACH
// ----------------
// The selfhosted stack only has ONE seeded test account today (the admin in
// tests/test-accounts.ts). Dedicated User and SuperAdmin accounts per
// SH_NavE2ESmoke VERIFY criteria do not yet exist on sh-test and are tracked
// as a DEBT item in knowzcode/specs/SH_HeaderNavigation.md §Debt.
//
// Until those accounts are seeded, this spec uses TWO mechanisms:
//   1. Real login flow for Admin (via loginAsAdmin helper).
//   2. localStorage-stub fixtures for User and SuperAdmin — page.addInitScript
//      seeds an authToken + pre-resolved user payload so <AuthProvider>
//      short-circuits to authenticated on mount (mirrors the Vitest pattern
//      used in src/test/Layout.test.tsx for the same reason).
//
// The stubs are not a substitute for real accounts on prod — they verify the
// client-side nav contract only. When seeded roles exist, replace the
// addInitScript blocks with helper logins and remove this note.

import { test, expect, type Page } from '@playwright/test'
import { loginAsAdmin } from './helpers/login'

const VIEWPORTS = {
  mobile: { width: 768, height: 900 },
  tablet: { width: 1024, height: 900 },
  desktop: { width: 1440, height: 900 },
} as const

async function stubAuth(
  page: Page,
  opts: { role: 0 | 1 | 2; displayName?: string; tenantId?: string; tenantName?: string },
): Promise<void> {
  // NOTE: the selfhosted AuthProvider reads sessionStorage.authToken at mount
  // and hydrates the user via api.getMe(). We stub api.getMe() via route
  // interception and seed the token so the provider hits the 'authenticated'
  // branch. addInitScript runs BEFORE any page JS.
  const user = {
    id: `stub-${opts.role}`,
    username: `stub-role-${opts.role}`,
    email: null,
    displayName: opts.displayName ?? `Stub User ${opts.role}`,
    role: opts.role,
    tenantId: opts.tenantId ?? 'stub-tenant',
    tenantName: opts.tenantName ?? 'Stub Tenant',
    isActive: true,
    apiKey: null,
    createdAt: '2026-01-01T00:00:00Z',
    lastLoginAt: null,
  }

  await page.route('**/api/v1/auth/me', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(user) }),
  )
  await page.route('**/api/v1/auth/tenants', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  )
  await page.route('**/api/v1/admin/tenants', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  )

  await page.addInitScript(() => {
    try {
      window.sessionStorage.setItem('authToken', 'stub-token')
    } catch {
      /* ignore */
    }
  })
}

// SH_LookAndFeelParity: visible primary strip is Knowledge / Chat / Search.
// Vaults/Files/Inbox/Organize live in overflow "More"; Admin stays in UserMenu.
const EXPECTED_PRIMARY_NAV = [
  'nav-link-knowledge',
  'nav-link-chat',
  'nav-link-search',
] as const

test.describe('Header nav smoke', () => {
  test.beforeEach(async ({ page }) => {
    await page.setViewportSize(VIEWPORTS.desktop)
  })

  // Regression guard for the v0.14.0 logo/nav overlap bug (sh.knowz.io header
  // previously rendered nav items on top of the Pacifico logo at desktop widths).
  // See knowzcode/planning/greedy-waddling-fox.md — fix restructured the row to a
  // two-column layout mirroring src/knowz-web-client/src/components/Layout.tsx.
  for (const [vpName, viewport] of [
    ['tablet', VIEWPORTS.tablet],
    ['desktop', VIEWPORTS.desktop],
  ] as const) {
    test(`logo and first nav link do not horizontally overlap — ${vpName} viewport`, async ({ page }) => {
      await stubAuth(page, { role: 1 /* Admin — all 8 items visible */ })
      await page.setViewportSize(viewport)
      await page.goto('/')

      const logo = page.getByTestId('sh-logo-link')
      const firstNav = page.getByTestId('nav-link-knowledge')
      await expect(logo).toBeVisible()
      await expect(firstNav).toBeVisible()

      const logoBox = await logo.boundingBox()
      const firstNavBox = await firstNav.boundingBox()
      expect(logoBox).not.toBeNull()
      expect(firstNavBox).not.toBeNull()

      // Logo's right edge MUST be strictly left of first nav item's left edge.
      expect(logoBox!.x + logoBox!.width).toBeLessThan(firstNavBox!.x)
    })
  }

  test('primary Knowledge Chat Search are visible without 9px uppercase labels', async ({ page }) => {
    await stubAuth(page, { role: 0 })
    await page.goto('/')

    await expect(page.getByTestId('sh-header')).toBeVisible()
    for (const testid of EXPECTED_PRIMARY_NAV) {
      await expect(page.getByTestId(testid)).toBeVisible()
    }
    await expect(page.getByTestId('nav-link-admin')).toHaveCount(0)

    const headerHtml = await page.getByTestId('sh-header').innerHTML()
    expect(headerHtml).not.toContain('text-[9px]')
  })

  for (const roleName of ['user', 'admin', 'superadmin'] as const) {
    test.skip(`renders role-appropriate primary nav — ${roleName}`, async ({ page }) => {
      // Marked skip until (a) sh-test has User + SuperAdmin seeded accounts, or
      // (b) route-interception stubs are validated against the live API.
      // Kept for contract visibility — delete skip when ready.
      const role = roleName === 'user' ? 0 : roleName === 'admin' ? 1 : 2
      await stubAuth(page, { role })
      await page.goto('/')

      await expect(page.getByTestId('sh-header')).toBeVisible()
      for (const testid of EXPECTED_PRIMARY_NAV) {
        await expect(page.getByTestId(testid)).toBeVisible()
      }
      await expect(page.getByTestId('nav-link-admin')).toHaveCount(0)
    })
  }

  test.skip('primary nav items navigate and mark active with aria-current', async ({ page }) => {
    await loginAsAdmin(page)
    await page.setViewportSize(VIEWPORTS.desktop)

    // Visible primary items are plain NavLinks — click routes directly.
    for (const testid of [
      'nav-link-knowledge',
      'nav-link-chat',
      'nav-link-search',
    ]) {
      const link = page.getByTestId(testid)
      await link.click()
      await page.waitForLoadState('networkidle')
      await expect(link).toHaveAttribute('aria-current', 'page')
    }
  })

  test.skip('mobile hamburger opens drawer and drawer closes on route change', async ({ page }) => {
    await loginAsAdmin(page)
    await page.setViewportSize(VIEWPORTS.mobile)
    await page.goto('/')

    await expect(page.getByTestId('sh-mobile-drawer')).toHaveCount(0)
    await page.getByTestId('sh-mobile-hamburger').click()
    await expect(page.getByTestId('sh-mobile-drawer')).toBeVisible()

    // Mobile drawer exposes all 8 flat items — tap Files directly.
    await page.getByTestId('nav-link-files-mobile').click()
    await expect(page).toHaveURL(/\/files/)
    await expect(page.getByTestId('sh-mobile-drawer')).toHaveCount(0)
  })

  test.skip('knowz logo routes to dashboard', async ({ page }) => {
    await loginAsAdmin(page)
    await page.setViewportSize(VIEWPORTS.desktop)
    await page.goto('/knowledge')
    await page.getByTestId('sh-logo-link').click()
    await expect(page).toHaveURL(/\/$/)
  })

  test.skip('SuperAdmin cross-tenant select + viewing pill interactions', async ({ page }) => {
    await stubAuth(page, { role: 2 })
    // Provide two tenants so the select renders.
    await page.route('**/api/v1/admin/tenants', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([
          { id: 'stub-tenant', name: 'Stub Tenant', slug: 'stub', description: null, isActive: true, userCount: 0, createdAt: '2026-01-01T00:00:00Z' },
          { id: 'other-tenant', name: 'Other Tenant', slug: 'other', description: null, isActive: true, userCount: 0, createdAt: '2026-01-01T00:00:00Z' },
        ]),
      }),
    )
    await page.goto('/')

    await expect(page.getByTestId('sh-superadmin-viewing-pill')).toHaveCount(0)
    const selectEl = page.getByTestId('sh-superadmin-tenant-select')
    await expect(selectEl).toBeVisible()
    await selectEl.selectOption('other-tenant')
    await expect(page.getByTestId('sh-superadmin-viewing-pill')).toBeVisible()
  })

  test.skip('sticky header remains at top:0 after scroll — scroll-dock regression', async ({ page }) => {
    await loginAsAdmin(page)
    await page.goto('/')
    await page.evaluate(() => window.scrollTo(0, 2000))
    const rect = await page.evaluate(() => {
      const el = document.querySelector('[data-testid="sh-header"]') as HTMLElement | null
      return el ? el.getBoundingClientRect().top : null
    })
    expect(rect).toBe(0)
  })

  for (const vpName of ['mobile', 'tablet', 'desktop'] as const) {
    test.skip(`renders shell at ${vpName} viewport`, async ({ page }) => {
      await loginAsAdmin(page)
      await page.setViewportSize(VIEWPORTS[vpName])
      await page.goto('/')
      await expect(page.getByTestId('sh-header')).toBeVisible()
      if (vpName === 'mobile') {
        await expect(page.getByTestId('sh-mobile-hamburger')).toBeVisible()
      }
    })
  }
})
