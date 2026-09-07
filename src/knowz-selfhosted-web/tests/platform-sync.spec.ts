// SCAFFOLD — Playwright E2E tests for Platform Sync feature.
//
// STATUS: Playwright infrastructure is now set up (playwright.config.ts,
// tests/test-accounts.ts, tests/helpers/login.ts) and the @playwright/test
// dependency is installed, so `npx playwright test --list` enumerates these
// cases cleanly. Tests remain `test.skip()` until the following are in place:
//
//   (a) The sh-test deployment exposes the Platform Sync admin routes and
//       the feature flag is enabled for the seeded admin account.
//   (b) Stable `data-testid` attributes are added to ConnectionCard,
//       VaultLinksTable, the Connect/Browse dialogs, and the sync-history
//       table so selectors don't rely on fragile role/text regexes.
//   (c) A dedicated non-admin account is seeded so the authorization test
//       (`non-admin cannot access platform sync page`) has a second identity
//       to log in as, and a beforeAll seeds an existing connection for the
//       browse-modal and sync-history tests.
//   (d) Browsers have been installed (`npx playwright install chromium`) and
//       a runner has access to the sh-test environment.
//
// Remove the `test.skip` and enable each case once its prerequisites land.
// See main web client (src/knowz-web-client/tests/) for reference patterns.

import { test, expect, type Page } from '@playwright/test';
import { loginAsAdmin } from './helpers/login';

const ADMIN_ROUTE = '/admin/platform-sync';

async function stubUserAndEmptySync(page: Page): Promise<void> {
  const user = {
    id: 'stub-user',
    username: 'stub-role-0',
    email: null,
    displayName: 'Stub User',
    role: 0,
    tenantId: 'stub-tenant',
    tenantName: 'Stub Tenant',
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
  await page.route('**/api/v1/sync/connection', (route) => {
    if (route.request().method() === 'GET') {
      return route.fulfill({
        status: 404,
        contentType: 'application/json',
        body: JSON.stringify({ error: 'Not found' }),
      })
    }
    return route.continue()
  })
  await page.route('**/api/v1/sync/links', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  )
  await page.route('**/api/v1/sync/history**', (route) =>
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

test.describe('Platform Sync connect form (VERIFY-M6)', () => {
  test('User sees connect form when GET /connection is 404', async ({ page }) => {
    await stubUserAndEmptySync(page)
    await page.setViewportSize({ width: 1440, height: 900 })
    await page.goto('/sync')

    await expect(page.getByTestId('platform-connection-card')).toBeVisible()
    await expect(page.getByTestId('platform-connection-empty')).toBeVisible()
    await expect(page.getByTestId('platform-connection-url')).toBeVisible()
    await expect(page.getByTestId('platform-connection-api-key')).toHaveAttribute('type', 'password')
    await expect(page.getByRole('button', { name: /^save$/i })).toBeVisible()
  })
})

test.describe('Platform Sync', () => {
  test.beforeEach(async ({ page }) => {
    // Enable once the sh-test admin account can see the Platform Sync route.
    // await loginAsAdmin(page);
    // await page.goto(ADMIN_ROUTE);
    void loginAsAdmin; // silence unused-import warning until activation
    void ADMIN_ROUTE;
    void page;
  });

  test.skip('shows empty state when not connected', async ({ page }) => {
    // Verify ConnectionCard shows "Not connected" state
    await expect(page.getByText(/not connected/i)).toBeVisible();
    // Verify the Connect button is visible
    await expect(page.getByRole('button', { name: /connect/i })).toBeVisible();
    // Verify VaultLinksTable is empty or shows "no links" empty state
    await expect(page.getByText(/no vault links|no links configured/i)).toBeVisible();
  });

  test.skip('admin can save platform connection (UI flow)', async ({ page }) => {
    // Open the connect/edit dialog
    await page.getByRole('button', { name: /connect|edit/i }).click();

    // Fill in connection details
    await page.getByLabel(/platform url/i).fill('https://api.dev.knowz.io');
    await page.getByLabel(/api key/i).fill('ukz_test_key_for_e2e_only');
    await page.getByLabel(/display name/i).fill('Dev Platform');

    // API key field must be masked (type=password)
    const apiKeyInput = page.getByLabel(/api key/i);
    await expect(apiKeyInput).toHaveAttribute('type', 'password');

    // Test Connection (candidate round-trip)
    await page.getByRole('button', { name: /test connection/i }).click();
    await expect(page.getByText(/connection successful|reachable/i)).toBeVisible();

    // Save
    await page.getByRole('button', { name: /^save$/i }).click();

    // Verify connection card now shows the display name and masked key (ukz_****XXXX)
    await expect(page.getByText('Dev Platform')).toBeVisible();
    await expect(page.getByText(/ukz_\*{4}/)).toBeVisible();
  });

  test.skip('browse modal shows platform vaults', async ({ page }) => {
    // Assumes a connection already exists (seeded in test beforeAll)
    await page.getByRole('button', { name: /browse platform/i }).click();

    // Vaults list loads
    await expect(page.getByRole('heading', { name: /vaults/i })).toBeVisible();
    const firstVault = page.getByRole('listitem').filter({ hasText: /.+/ }).first();
    await expect(firstVault).toBeVisible();

    // Click into a vault
    await firstVault.click();

    // Knowledge items render
    await expect(page.getByRole('heading', { name: /knowledge|items/i })).toBeVisible();

    // Search debounces and updates results
    const searchBox = page.getByPlaceholder(/search/i);
    await searchBox.fill('test query');
    await page.waitForTimeout(400); // debounce window
    // Results container should reflect the filter (exact assertion depends on fixture data)
    await expect(page.getByRole('list')).toBeVisible();
  });

  test.skip('overwrite pull requires explicit confirmation', async ({ page }) => {
    await page.getByRole('button', { name: /browse platform/i }).click();

    // Select an item
    await page.getByRole('checkbox').first().check();

    // Change strategy to Overwrite
    await page.getByLabel(/strategy/i).selectOption('overwrite');

    // Confirmation checkbox must appear
    const confirmCheckbox = page.getByLabel(/i understand|confirm overwrite/i);
    await expect(confirmCheckbox).toBeVisible();

    // Pull button disabled until confirmation is checked
    const pullButton = page.getByRole('button', { name: /^pull$/i });
    await expect(pullButton).toBeDisabled();

    // Check confirmation — Pull becomes enabled
    await confirmCheckbox.check();
    await expect(pullButton).toBeEnabled();

    // Proceed
    await pullButton.click();
    await expect(page.getByText(/pull started|operation queued/i)).toBeVisible();
  });

  test.skip('sync history shows operations', async ({ page }) => {
    // Scroll to / click into history section
    await page.getByRole('heading', { name: /sync history/i }).scrollIntoViewIfNeeded();

    // Table renders with expected columns
    const table = page.getByRole('table');
    await expect(table).toBeVisible();
    for (const col of ['Operation', 'Direction', 'Status', 'Items', 'Started', 'Duration', 'User']) {
      await expect(table.getByRole('columnheader', { name: new RegExp(col, 'i') })).toBeVisible();
    }

    // Expand a failed row and verify sanitized error (no stack frames)
    const failedRow = table.getByRole('row').filter({ hasText: /failed/i }).first();
    await failedRow.getByRole('button', { name: /expand|details/i }).click();

    const errorDetail = page.getByTestId('sync-error-detail');
    await expect(errorDetail).toBeVisible();
    await expect(errorDetail).not.toContainText(/at \w+\.\w+.*:\d+:\d+/); // no stack trace
    await expect(errorDetail).not.toContainText(/System\.\w+Exception/); // no raw .NET exceptions
  });

  test.skip('non-admin cannot access platform sync page', async ({ page, context }) => {
    // Log out admin, log in as regular user
    await context.clearCookies();
    // TODO: loginAsUser(page);

    // Attempt to navigate to the admin page
    await page.goto(ADMIN_ROUTE);

    // Expect redirect away OR a 403/forbidden indicator
    await expect(page).not.toHaveURL(new RegExp(ADMIN_ROUTE + '$'));
    // Alternatively, if the app renders an inline 403:
    // await expect(page.getByText(/forbidden|not authorized|access denied/i)).toBeVisible();
  });
});
