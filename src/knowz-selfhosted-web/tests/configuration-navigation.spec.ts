import { readFileSync } from 'node:fs'
import { test, expect, type Page } from '@playwright/test'

// Opt-in: an isolated real instance with an already rotated administrator password.
// Credentials are read from a private file and never written to test artifacts.
const category = process.env.KNOWZ_E2E_CONFIG_CATEGORY || 'OpenAiCompatible'
const categoryLabel = process.env.KNOWZ_E2E_CONFIG_LABEL || 'OpenAI-compatible'
const fieldName = process.env.KNOWZ_E2E_CONFIG_FIELD || 'ChatModel'
const credentialFile = process.env.KNOWZ_E2E_CREDENTIAL_FILE
const credentials = credentialFile ? JSON.parse(readFileSync(credentialFile, 'utf8')) as { username: string; password: string } : null

async function configuration(page: Page) {
  await page.getByTestId('sh-user-menu').click()
  await page.getByTestId('sh-user-menu-admin-settings').click()
  await page.getByRole('button', { name: categoryLabel, exact: true }).click()
}
for (const [name, viewport] of Object.entries({ desktop: { width: 1365, height: 1000 }, mobile: { width: 390, height: 844 } })) {
  test(`real configuration navigation preserves unsaved drafts — ${name}`, async ({ page }, testInfo) => {
    test.skip(!credentials, 'Set KNOWZ_E2E_CREDENTIAL_FILE for an isolated real instance')
    await page.setViewportSize(viewport)
    const errors: string[] = []
    page.on('pageerror', error => errors.push(error.message))
    let writes = 0
    page.on('request', request => { if (request.method() === 'PUT' && request.url().includes('/api/v1/admin/config/')) writes++ })
    await page.goto('/login')
    await page.getByLabel('Username', { exact: true }).fill(credentials!.username)
    await page.getByLabel('Password', { exact: true }).fill(credentials!.password)
    await page.getByRole('button', { name: 'Sign In', exact: true }).click()
    await expect(page).toHaveURL(/\/$/)
    await configuration(page)
    const model = page.getByRole('textbox', { name: fieldName, exact: true })
    await expect(model, 'Choose an advertised editable nonsecret field for this instance policy').toBeEditable()
    const baseline = await model.inputValue()
    await model.fill('unsaved-navigation-proof')
    await page.evaluate(() => history.back())
    const prompt = page.getByRole('dialog', { name: 'Unsaved configuration changes' })
    await expect(prompt).toBeVisible()
    await expect(page).toHaveURL(/\/admin\/settings$/)
    await expect(prompt.getByRole('button', { name: 'Stay', exact: true })).toBeFocused()
    const bounds = await prompt.boundingBox()
    expect(bounds!.x).toBeGreaterThanOrEqual(0)
    expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(viewport.width)
    expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(viewport.height)
    expect(Math.abs(bounds!.y + bounds!.height / 2 - viewport.height / 2)).toBeLessThan(2)
    await page.keyboard.press('Shift+Tab')
    await expect(prompt.getByRole('button', { name: 'Save changes and leave' })).toBeFocused()
    await page.screenshot({ path: testInfo.outputPath(`navigation-${name}.png`), fullPage: false })
    await page.keyboard.press('Escape')
    await expect(prompt).not.toBeVisible()
    await expect(model).toHaveValue('unsaved-navigation-proof')
    await expect(model).toBeFocused()
    expect(writes).toBe(0)
    // A programmatically triggered application link uses the same router guard.
    await page.getByTestId('sh-logo-link').evaluate(element => (element as HTMLElement).click())
    await expect(prompt).toBeVisible()
    await prompt.getByRole('button', { name: 'Stay', exact: true }).click()
    await expect(model).toHaveValue('unsaved-navigation-proof')
    await page.evaluate(() => history.back())
    await prompt.getByRole('button', { name: 'Leave without saving' }).click()
    await expect(page).toHaveURL(/\/$/)
    expect(writes).toBe(0)
    await page.goForward()
    await page.getByRole('button', { name: categoryLabel, exact: true }).click()
    await expect(model).toHaveValue(baseline)
    // Save the unchanged baseline: exercise the real authenticated API without changing instance behavior.
    await model.fill(baseline + '-draft')
    await model.fill(baseline)
    await page.route(`**/api/v1/admin/config/${category}`, async route => {
      if (route.request().method() === 'PUT') await route.fulfill({ status: 409, contentType: 'application/json', body: JSON.stringify({ message: 'Navigation conflict fixture' }) })
      else await route.continue()
    })
    await page.evaluate(() => history.back())
    await prompt.getByRole('button', { name: 'Save changes and leave' }).click()
    await expect(prompt.getByRole('alert')).toContainText('remaining edits are still here')
    await expect(page).toHaveURL(/\/admin\/settings$/)
    await prompt.getByRole('button', { name: 'Stay', exact: true }).click()
    await expect(model).toHaveValue(baseline)
    await page.unroute(`**/api/v1/admin/config/${category}`)
    await page.evaluate(() => history.back())
    await prompt.getByRole('button', { name: 'Save changes and leave' }).click()
    await expect(page).toHaveURL(/\/$/)
    expect(writes).toBe(2) // One intentional conflict plus one real save; no implicit or duplicate writes.
    await page.goto('/admin/settings')
    await expect(page.getByRole('heading', { name: 'Instance configuration' })).toBeVisible()
    await expect(page).toHaveTitle(/knowz/i)
    await page.getByTestId('sh-logo-link').click()
    await expect(page).toHaveURL(/\/$/)
    expect(errors).toEqual([])
    await testInfo.attach('navigation-evidence', { body: JSON.stringify({ viewport, category, fieldName, writes, errors, auth: 'real', realSave: true, injectedConflict: true, browserBackForward: true, nativeFocusTrap: true }), contentType: 'application/json' })
  })
}
