import { expect, test, type Page, type Route } from '@playwright/test'

const admin = {
  username: 'ci-admin',
  roles: ['admin']
}

const starterBook = {
  id: '11111111-1111-1111-1111-111111111111',
  bookName: 'CI Starter Book',
  description: 'Browser test fixture',
  publisher: 'Lexarbor',
  educationLevel: 'Secondary',
  grade: 'Grade 7',
  category: 'English',
  displayOrder: 1,
  status: true,
  iconUrl: ''
}

function json(route: Route, data: unknown, status = 200) {
  return route.fulfill({
    status,
    contentType: 'application/json',
    body: JSON.stringify(data)
  })
}

/** Everything the administration shell reads once a session is live. */
async function mockAdministration(page: Page) {
  await page.route('**/admin/system/version', (route) =>
    json(route, {
      success: true,
      data: { version: '1.2.3', revision: null, channel: 'release' }
    }))
  await page.route('**/admin/vocabulary-books/categories', (route) =>
    json(route, { success: true, data: { items: ['English'] } }))
  await page.route('**/admin/vocabulary-books/education-levels', (route) =>
    json(route, { success: true, data: { items: ['Secondary'] } }))
  await page.route(/\/admin\/vocabulary-books(?:\?.*)?$/, (route) =>
    json(route, {
      success: true,
      data: { items: [starterBook], totalCount: 1, totalPage: 1 }
    }))
  await page.route(/\/api\/vocabulary-books\/all$/, (route) =>
    json(route, { success: true, data: { books: [starterBook] } }))
}

/**
 * The session endpoint behind a mutable flag: the hosted callback "commits"
 * the session by flipping it, exactly like the real backend signing in before
 * its redirect, so the same route answers 401 before and success after.
 */
function useSessionFlag(page: Page, state: { sessionLive: boolean }) {
  return page.route('**/admin/auth/session', (route) =>
    state.sessionLive
      ? json(route, { success: true, data: admin })
      : json(route, { success: false, message: 'Unauthorized' }, 401))
}

/**
 * Fulfills a navigation request with a minimal document that immediately
 * replaces itself with `target`. Playwright does not re-intercept the redirect
 * continuation of a fulfilled 302, so every simulated server-side hop hands the
 * browser to the next URL through a script navigation instead, which routing
 * sees again as a fresh request — the same browser-visible effect as the
 * backend's fixed 302s.
 */
function hopTo(route: Route, target: string) {
  const href = new URL(target, route.request().url()).href
  return route.fulfill({
    status: 200,
    contentType: 'text/html',
    body: `<!doctype html><html lang="en"><head><meta charset="utf-8"><title>hop</title></head><body><script>location.replace(${JSON.stringify(href)})</script></body></html>`
  })
}

/**
 * Models the server-side hosted round trip as the browser sees it: the start
 * route hops through a synthetic provider authorization response onto the
 * fixed callback route, and the callback hops to the stored return target. A
 * `failure` state hops to the login page with that fixed reason instead and
 * never establishes a session.
 */
async function useHostedRoundTrip(
  page: Page,
  state: { sessionLive: boolean; failure?: string }
) {
  const startUrls: string[] = []
  const callbackUrls: string[] = []
  let returnTarget = '/#/books'

  await page.route(/\/admin\/auth\/start(?:\?.*)?$/, (route) => {
    const url = new URL(route.request().url())
    startUrls.push(url.pathname + url.search)
    const returnUrl = url.searchParams.get('returnUrl')
    returnTarget = `/#${returnUrl ?? '/books'}`
    return hopTo(route, '/admin/auth/callback?code=synthetic-code&state=synthetic-state&iss=http%3A%2F%2Fidp.test')
  })
  await page.route(/\/admin\/auth\/callback(?:\?.*)?$/, (route) => {
    callbackUrls.push(route.request().url())
    if (!state.failure) {
      state.sessionLive = true
    }

    return hopTo(route, state.failure ? `/#/login?reason=${state.failure}` : returnTarget)
  })
  return { startUrls, callbackUrls }
}

/** Every request the page makes, for the network-material assertions. */
function trackRequests(page: Page) {
  const requests: Array<{ url: string; method: string; body: string | null }> = []
  page.on('request', (request) => {
    requests.push({
      url: request.url(),
      method: request.method(),
      body: request.postData()
    })
  })
  return requests
}

function expectNoCredentialMaterial(
  requests: Array<{ url: string; method: string; body: string | null }>
) {
  for (const request of requests) {
    expect(request.method === 'POST' && request.url.includes('/admin/auth/login')).toBe(false)
    for (const forbidden of ['access_token', 'id_token', 'client_secret', 'code_verifier']) {
      expect(request.url).not.toContain(forbidden)
    }

    if (request.body) {
      expect(request.body).not.toContain('"password"')
      expect(request.body).not.toContain('client_secret')
    }
  }
}

test('the login page offers only the SignaCore navigation and never probes a method', async ({ page }) => {
  const requests = trackRequests(page)
  await page.goto('/#/login')

  await expect(page.getByRole('button', { name: '使用 SignaCore 登录' })).toBeVisible()
  await expect(page.locator('input[autocomplete="username"]')).toHaveCount(0)
  await expect(page.locator('input[type="password"]')).toHaveCount(0)
  await expect(requests.some((request) => request.url.includes('/admin/auth/method'))).toBe(false)
  await expect(requests.some((request) => request.url.includes('/admin/auth/login'))).toBe(false)
})

test('the hosted button sends one start navigation with the allowlisted return route', async ({ page }) => {
  const startUrls: string[] = []
  let held: Route | null = null
  await page.route(/\/admin\/auth\/start(?:\?.*)?$/, (route) => {
    const url = new URL(route.request().url())
    startUrls.push(url.pathname + url.search)
    // Held pending so the page stays alive and a second click can be proven
    // not to start a second navigation.
    held = route
    return new Promise<void>(() => {})
  })

  await page.goto('/#/login?redirect=/vocabulary')
  const button = page.locator('.auth-card__hosted button')
  await button.waitFor()

  // Two synchronous activations in one task — the tightest possible race,
  // before any re-render could disable the button. The guard must make the
  // second one a no-op, so exactly one start navigation is ever begun.
  await page.evaluate(() => {
    const element = document.querySelector<HTMLButtonElement>('.auth-card__hosted button')
    element?.click()
    element?.click()
  })

  await expect.poll(() => startUrls.length).toBe(1)
  await page.waitForTimeout(200)
  expect(startUrls).toEqual(['/admin/auth/start?returnUrl=%2Fvocabulary'])

  // Release the held navigation so the page can tear down cleanly.
  await held?.fulfill({ status: 200, body: 'navigating to the provider' }).catch(() => undefined)
})

for (const { redirect, expected } of [
  { redirect: '/books/abc-123_X/words', expected: '/admin/auth/start?returnUrl=%2Fbooks%2Fabc-123_X%2Fwords' },
  { redirect: '/secret', expected: '/admin/auth/start' },
  { redirect: '/import?x=1', expected: '/admin/auth/start' },
  { redirect: '/login', expected: '/admin/auth/start' }
]) {
  test(`the start navigation keeps the allowlist for redirect ${redirect}`, async ({ page }) => {
    const startUrls: string[] = []
    await page.route(/\/admin\/auth\/start(?:\?.*)?$/, (route) => {
      const url = new URL(route.request().url())
      startUrls.push(url.pathname + url.search)
      return route.fulfill({ status: 200, body: 'navigating to the provider' })
    })

    await page.goto(`/#/login?redirect=${encodeURIComponent(redirect)}`)
    await page.getByRole('button', { name: '使用 SignaCore 登录' }).click()

    await expect.poll(() => startUrls.length).toBe(1)
    expect(startUrls[0]).toBe(expected)
  })
}

for (const { reason, text } of [
  { reason: 'canceled', text: '已取消登录，可重新发起。' },
  { reason: 'denied', text: '当前账户没有管理员权限。' },
  { reason: 'sign_in_failed', text: '登录失败，请重试。' },
  { reason: 'provider_unavailable', text: '身份提供方暂时不可用，请稍后重试。' },
  { reason: 'logged_out', text: '已退出登录。' },
  { reason: 'logout_failed', text: 'Lexarbor 已退出，但身份提供方的登出未确认完成，它可能仍保留会话。' }
]) {
  test(`the login page reads reason ${reason} as its fixed notice`, async ({ page }) => {
    await page.goto(`/#/login?reason=${reason}`)

    await expect(page.locator('.auth-card__reason')).toContainText(text)
    await expect(page.getByRole('button', { name: '使用 SignaCore 登录' })).toBeVisible()
  })
}

test('an unknown reason stays harmless', async ({ page }) => {
  await page.goto('/#/login?reason=%3Cscript%3E')

  await expect(page.locator('.auth-card__reason')).toHaveCount(0)
  await expect(page.locator('.el-message--error')).toHaveCount(0)
  await expect(page.getByRole('button', { name: '使用 SignaCore 登录' })).toBeVisible()
})

test('the provider round trip returns to the original page and keeps token material out of the browser', async ({ page }) => {
  const requests = trackRequests(page)
  const state = { sessionLive: false }
  await useSessionFlag(page, state)
  await useHostedRoundTrip(page, state)
  await mockAdministration(page)
  const versionRequests: string[] = []
  await page.route('**/admin/system/version', (route) => {
    versionRequests.push(route.request().url())
    return json(route, {
      success: true,
      data: { version: '1.2.3', revision: null, channel: 'release' }
    })
  })

  await page.goto('/#/import')
  // The expired or missing session lands on the login page with the return
  // route preserved for the start navigation.
  await expect(page).toHaveURL(/#\/login\?redirect=(%2F|\/)import$/)
  await page.getByRole('button', { name: '使用 SignaCore 登录' }).click()

  await expect(page).toHaveURL(/#\/import$/)
  await expect(page.locator('.session')).toContainText(admin.username)
  expectNoCredentialMaterial(requests)
  expect(page.url()).not.toContain('code=')

  // One version request for the one established session, and nothing at all
  // in the two storages.
  await expect(page.locator('.app-header__version-button')).toHaveText('v1.2.3')
  expect(versionRequests).toHaveLength(1)
  const stored = await page.evaluate(() => ({
    local: window.localStorage.length,
    session: window.sessionStorage.length
  }))
  expect(stored).toEqual({ local: 0, session: 0 })
})

test('a canceled provider login lands back on the login page without a session', async ({ page }) => {
  await useSessionFlag(page, { sessionLive: false })
  await useHostedRoundTrip(page, { sessionLive: false, failure: 'canceled' })
  await mockAdministration(page)

  await page.goto('/#/books')
  await expect(page).toHaveURL(/#\/login\?redirect=(%2F|\/)books$/)
  await page.getByRole('button', { name: '使用 SignaCore 登录' }).click()

  await expect(page).toHaveURL(/#\/login\?reason=canceled$/)
  await expect(page.locator('.auth-card__reason')).toContainText('已取消登录，可重新发起。')
  // The failure created no identity: the shell stays hidden and the hosted
  // navigation can be started again.
  await expect(page.locator('.session')).toHaveCount(0)
  await expect(page.getByRole('button', { name: '使用 SignaCore 登录' })).toBeVisible()
})

test('a non-administrator sign-in is denied with the denied notice', async ({ page }) => {
  await useSessionFlag(page, { sessionLive: false })
  await useHostedRoundTrip(page, { sessionLive: false, failure: 'denied' })
  await mockAdministration(page)

  await page.goto('/#/books')
  await expect(page).toHaveURL(/#\/login\?redirect=(%2F|\/)books$/)
  await page.getByRole('button', { name: '使用 SignaCore 登录' }).click()

  await expect(page).toHaveURL(/#\/login\?reason=denied$/)
  await expect(page.locator('.auth-card__reason')).toContainText('当前账户没有管理员权限。')
  await expect(page.locator('.session')).toHaveCount(0)
})

test('a reload restores the hosted session without the login page', async ({ page }) => {
  const state = { sessionLive: true }
  await useSessionFlag(page, state)
  await mockAdministration(page)

  await page.goto('/#/books')
  await expect(page.locator('.session')).toContainText(admin.username)

  await page.reload()

  await expect(page).toHaveURL(/#\/books$/)
  await expect(page.locator('.session')).toContainText(admin.username)
  await expect(page.locator('input[type="password"]')).toHaveCount(0)
})

test('an expired session sends the administrator back through the provider', async ({ page }) => {
  const state = { sessionLive: true }
  await useSessionFlag(page, state)
  await useHostedRoundTrip(page, state)
  await mockAdministration(page)

  await page.goto('/#/books')
  await expect(page.locator('.session')).toContainText(admin.username)

  // The 15-minute access token expired server-side: the next load's restore
  // answers 401 and the login page offers the provider navigation again.
  state.sessionLive = false
  await page.reload()
  await expect(page).toHaveURL(/#\/login\?redirect=(%2F|\/)books$/)
  await page.getByRole('button', { name: '使用 SignaCore 登录' }).click()

  await expect(page).toHaveURL(/#\/books$/)
  await expect(page.locator('.session')).toContainText(admin.username)
})

test('logout navigates the browser to the prepared provider logout URI', async ({ page }) => {
  const state = { sessionLive: true }
  const logoutUrl = 'https://idp.test/oauth2/logout?logout_handle=synthetic-one-time-handle'
  await useSessionFlag(page, state)
  await mockAdministration(page)
  const logoutRequests: string[] = []
  await page.route('**/admin/auth/logout', (route) => {
    logoutRequests.push(route.request().method())
    return json(route, { success: true, data: { logoutUrl } })
  })
  await page.route('https://idp.test/**', (route) =>
    route.fulfill({ status: 200, body: 'provider signed-out page' }))

  await page.goto('/#/books')
  await expect(page.locator('.session')).toContainText(admin.username)
  await page.locator('.session').getByRole('button', { name: '退出登录' }).click()

  // The top-level navigation left the administration for the provider URI;
  // no local login navigation raced or overrode it.
  await expect(page).toHaveURL(logoutUrl)
  await expect(page.locator('body')).toContainText('provider signed-out page')
  expect(logoutRequests).toEqual(['POST'])
})

test('a hosted local-only logout says the provider may still hold a session', async ({ page }) => {
  const state = { sessionLive: true }
  await useSessionFlag(page, state)
  await mockAdministration(page)
  await page.route('**/admin/auth/logout', (route) => json(route, { success: true }))

  await page.goto('/#/books')
  await expect(page.locator('.session')).toContainText(admin.username)
  await page.locator('.session').getByRole('button', { name: '退出登录' }).click()

  // The local session ended, the upstream outcome is unknown: the notice must
  // name the possibility and must never claim SignaCore signed out.
  const warning = page.locator('.el-message--warning')
  await expect(warning).toContainText('已退出 Lexarbor')
  await expect(warning).toContainText('可能仍保留您的会话')
  await expect(page).toHaveURL(/#\/login$/)
  // The login page renders the hosted navigation again for a fresh sign-in.
  await expect(page.getByRole('button', { name: '使用 SignaCore 登录' })).toBeVisible()
})

test('the hosted logout fetches the antiforgery token and echoes it in the header', async ({ page }) => {
  const state = { sessionLive: true }
  const logoutUrl = 'https://idp.test/oauth2/logout?logout_handle=synthetic-one-time-handle'
  await useSessionFlag(page, state)
  await mockAdministration(page)
  const csrfRequests: string[] = []
  const logoutRequests: Array<{ method: string; headers: Record<string, string> }> = []
  await page.route('**/admin/auth/csrf', (route) => {
    csrfRequests.push(route.request().url())
    return json(route, { token: 'synthetic-antiforgery-token' })
  })
  await page.route('**/admin/auth/logout', (route) => {
    logoutRequests.push({
      method: route.request().method(),
      headers: route.request().headers()
    })
    return json(route, { success: true, data: { logoutUrl } })
  })

  await page.goto('/#/books')
  await expect(page.locator('.session')).toContainText(admin.username)
  await page.locator('.session').getByRole('button', { name: '退出登录' }).click()

  await expect(page).toHaveURL(logoutUrl)
  expect(csrfRequests).toHaveLength(1)
  expect(logoutRequests).toEqual([
    { method: 'POST', headers: expect.objectContaining({ 'x-signacore-csrf': 'synthetic-antiforgery-token' }) }
  ])
})

