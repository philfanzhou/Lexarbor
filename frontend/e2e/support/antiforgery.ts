import type { Page } from '@playwright/test'

/**
 * Mocks the hosted-login client's public antiforgery token endpoint (B6): the
 * administration front end fetches `GET /admin/auth/csrf` once per token
 * lifetime and echoes the token in `X-SignaCore-CSRF` on every unsafe method.
 *
 * Every administration spec installs this mock together with its session mock,
 * for two reasons: without it a first write leaves the preview proxy for a
 * token request the CI proxy cannot answer, and the extra round trip makes
 * "assert the captured request right after the click" racy. With the mock the
 * captures can also assert the production CSRF model directly.
 */
export async function mockAntiforgery(page: Page, token = 'synthetic-antiforgery-token') {
  await page.route('**/admin/auth/csrf', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ token }) }))
}
