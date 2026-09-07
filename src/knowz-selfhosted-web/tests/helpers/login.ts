import type { Page } from '@playwright/test'
import { testAccounts } from '../test-accounts'

/**
 * Log in as the seeded admin account on the selfhosted web client.
 *
 * The selfhosted LoginPage uses label-based fields (no data-testid attributes),
 * so this helper relies on the `Username` / `Password` labels and the visible
 * "Sign In" button. If data-testid attributes are added later, switch to
 * stable testid selectors for reliability.
 */
export async function loginAsAdmin(page: Page): Promise<void> {
  const { baseUrl, username, password } = testAccounts.shTest

  await page.goto(`${baseUrl}/login`)

  await page.getByLabel(/username/i).fill(username)
  await page.getByLabel(/password/i).fill(password)
  await page.getByRole('button', { name: /sign in/i }).click()

  // The selfhosted app lands on the dashboard (or the last-visited route)
  // after login. Wait for navigation away from /login to confirm success.
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), {
    timeout: 15_000,
  })
}
