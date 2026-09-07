import { defineConfig, devices } from '@playwright/test'

/**
 * Playwright Configuration for Knowz Selfhosted Web E2E Tests
 *
 * Environment Usage:
 *   Default (local):       npx playwright test
 *   Custom URL:            PLAYWRIGHT_BASE_URL=https://... npx playwright test
 *
 * Notes:
 *   - Start your local stack before running tests (no automatic webServer).
 *   - Browsers are NOT auto-installed; run `npx playwright install chromium`
 *     before executing tests for the first time.
 *   - Remote environments require an explicit PLAYWRIGHT_BASE_URL override.
 */

const DEFAULT_BASE_URL = 'http://localhost:3000'

const baseURL = process.env.PLAYWRIGHT_BASE_URL || DEFAULT_BASE_URL

export default defineConfig({
  testDir: './tests',
  timeout: 60_000,
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  workers: process.env.CI ? 1 : 3,
  reporter: [['line'], ['html', { open: 'never' }]],
  use: {
    baseURL,
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
    ignoreHTTPSErrors: true,
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
})
