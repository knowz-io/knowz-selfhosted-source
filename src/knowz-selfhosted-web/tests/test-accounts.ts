/**
 * E2E targets default to the local Compose stack. Remote deployments require
 * explicit PLAYWRIGHT_BASE_URL and SH_TEST_API_URL values; credentials always
 * come from the environment and have no reusable fallback secret.
 */
export const testAccounts = {
  shTest: {
    baseUrl: process.env.PLAYWRIGHT_BASE_URL || 'http://localhost:3000',
    apiUrl: process.env.SH_TEST_API_URL || 'http://localhost:5000',
    username: process.env.SH_TEST_ADMIN_USERNAME || 'admin',
    password: process.env.SH_TEST_ADMIN_PASSWORD || '',
    apiKey: process.env.SH_TEST_API_KEY || '',
  },
} as const
