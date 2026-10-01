import { expect, test, type Page, type Route } from '@playwright/test'

const admin = {
  username: 'ci-admin',
  roles: ['admin']
}

const longUsername = 'a-very-long-administrator-name-for-the-header'

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

const fullSha = 'a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0'

function json(route: Route, data: unknown, status = 200) {
  return route.fulfill({
    status,
    contentType: 'application/json',
    body: JSON.stringify(data)
  })
}

async function mockCatalog(page: Page) {
  await page.route('**/admin/vocabulary-books/categories', (route) =>
    json(route, { success: true, data: { items: ['English'] } }))
  await page.route('**/admin/vocabulary-books/education-levels', (route) =>
    json(route, { success: true, data: { items: ['Secondary'] } }))
  await page.route(/\/admin\/vocabulary-books(?:\?.*)?$/, (route) => {
    if (route.request().method() === 'GET') {
      return json(route, {
        success: true,
        data: { items: [starterBook], totalCount: 1, totalPage: 1 }
      })
    }

    return route.fallback()
  })
}

/** A signed-in administrator with a session, login, logout, and catalog. */
async function mockAdministrator(page: Page, username = admin.username) {
  const session = { username, roles: admin.roles }
  await page.route('**/admin/auth/session', (route) =>
    json(route, { success: true, data: session }))
  await page.route('**/admin/auth/logout', (route) =>
    json(route, { success: true }))
  await mockCatalog(page)
}

/**
 * Routes the version endpoint through a queue of deferred answers. A test
 * decides when each request is answered and in which order the answers
 * arrive, which is how the generation races below are orchestrated instead
 * of only checking the final string. Fulfilling a request the page has
 * already aborted (its session ended while it waited) can no longer deliver
 * anything, which is itself the outcome under test, so that failure is
 * swallowed instead of failing the run.
 */
function useDeferredVersion(page: Page) {
  const pending: Array<(answer: { body: unknown; status: number }) => void> = []
  const requests: string[] = []

  void page.route('**/admin/system/version', (route) => {
    requests.push(route.request().url())
    return new Promise<void>((resolve) => {
      pending.push((answer) => {
        json(route, answer.body, answer.status).catch(() => undefined)
        resolve()
      })
    })
  })

  return {
    requests,
    answerNext(body: unknown, status = 200) {
      const answer = pending.shift()
      if (!answer) {
        throw new Error('No version request is waiting for an answer.')
      }

      answer({ body, status })
    }
  }
}

async function openBooks(page: Page, username = admin.username) {
  await mockAdministrator(page, username)
  await page.goto('/#/books')
  await expect(page.locator('.session')).toContainText(username)
}

/** Fulfills a navigation with a document that replaces itself with `target`. */
function hopTo(route: Route, target: string) {
  const href = new URL(target, route.request().url()).href
  return route.fulfill({
    status: 200,
    contentType: 'text/html',
    body: `<!doctype html><html lang="en"><head><meta charset="utf-8"><title>hop</title></head><body><script>location.replace(${JSON.stringify(href)})</script></body></html>`
  })
}

/**
 * Models the hosted round trip as the browser sees it after a logout: the
 * SignaCore button leaves the SPA, the start and callback routes hop back, and
 * the fresh page load restores the session like a real provider return.
 */
async function signInAgain(page: Page, username = admin.username) {
  await page.route(/\/admin\/auth\/start(?:\?.*)?$/, (route) =>
    hopTo(route, '/admin/auth/callback?code=c&state=s&iss=http%3A%2F%2Fidp.test'))
  await page.route(/\/admin\/auth\/callback(?:\?.*)?$/, (route) => hopTo(route, '/#/books'))
  await page.getByRole('button', { name: '使用 SignaCore 登录' }).click()
  await expect(page).toHaveURL(/#\/books$/)
  await expect(page.locator('.session')).toContainText(username)
}

async function focusedText(page: Page) {
  return page.evaluate(() => {
    const element = document.activeElement as HTMLElement | null
    return element?.innerText.trim() ?? ''
  })
}

const buildCases = [
  {
    name: 'release',
    payload: { version: '1.2.3', revision: fullSha, channel: 'release' },
    label: 'v1.2.3',
    detail: ['1.2.3', fullSha, 'release']
  },
  {
    name: 'prerelease',
    payload: { version: '1.3.0-rc.1', revision: null, channel: 'release' },
    label: 'v1.3.0-rc.1',
    detail: ['1.3.0-rc.1', '未知', 'release']
  },
  {
    name: 'edge build',
    payload: { version: '0.0.0-dev', revision: fullSha, channel: 'edge' },
    label: `edge · ${fullSha.slice(0, 7)}`,
    detail: ['0.0.0-dev', fullSha, 'edge']
  },
  {
    name: 'edge build without a commit',
    payload: { version: '0.0.0-dev', revision: null, channel: 'edge' },
    label: 'edge · 提交未知',
    detail: ['未知', 'edge']
  },
  {
    name: 'development build',
    payload: { version: '0.0.0-dev', revision: null, channel: 'development' },
    label: '开发版本',
    detail: ['0.0.0-dev', '未知', 'development']
  },
  {
    name: 'unreadable version',
    payload: { version: 'unknown', revision: null, channel: 'development' },
    label: '版本未知',
    detail: ['未知', 'development']
  }
]

for (const { name, payload, label, detail } of buildCases) {
  test(`displays the ${name} beside the brand and its full identity on demand`, async ({ page }) => {
    await mockAdministrator(page)
    await page.route('**/admin/system/version', (route) =>
      json(route, { success: true, data: payload }))

    await page.goto('/#/books')
    const button = page.locator('.app-header__version-button')
    await expect(button).toHaveText(label)

    // Reachable from the keyboard: Tab passes the logout button first, then
    // the version button; Enter opens the detail, Escape closes it and keeps
    // focus on the button that owns it.
    await page.keyboard.press('Tab')
    expect(await focusedText(page)).toBe('退出登录')
    await page.keyboard.press('Tab')
    expect(await focusedText(page)).toBe(label)

    await page.keyboard.press('Enter')
    const dialog = page.locator('.version-detail')
    await expect(dialog).toBeVisible()
    for (const text of detail) {
      await expect(dialog).toContainText(text)
    }

    await page.keyboard.press('Escape')
    await expect(dialog).toHaveCount(0)
    expect(await focusedText(page)).toBe(label)
  })
}

test('also opens the detail from a pointer click and closes it from an outside click', async ({ page }) => {
  await mockAdministrator(page)
  await page.route('**/admin/system/version', (route) =>
    json(route, { success: true, data: { version: '1.2.3', revision: fullSha, channel: 'release' } }))

  await page.goto('/#/books')
  await page.locator('.app-header__version-button').click()
  const dialog = page.locator('.version-detail')
  await expect(dialog).toBeVisible()

  await page.getByRole('navigation', { name: '主导航' }).getByRole('link', { name: '教材管理' }).click()
  await expect(dialog).toHaveCount(0)
})

test('fetches once per session, not on route changes, and again after a reload', async ({ page }) => {
  const version = useDeferredVersion(page)
  await openBooks(page)

  expect(version.requests).toHaveLength(1)

  await page.getByRole('navigation', { name: '主导航' }).getByRole('link', { name: '单条导入' }).click()
  await expect(page).toHaveURL(/#\/import$/)
  expect(version.requests).toHaveLength(1)

  // A route change must not cancel the session-scoped request: answering it
  // now, on another page, still paints the header.
  version.answerNext({ version: '1.0.0', revision: null, channel: 'release' })
  await expect(page.locator('.app-header__version-button')).toHaveText('v1.0.0')

  await page.reload()
  await expect(page.locator('.session')).toContainText(admin.username)
  expect(version.requests).toHaveLength(2)

  version.answerNext({ version: '2.0.0', revision: null, channel: 'release' })
  await expect(page.locator('.app-header__version-button')).toHaveText('v2.0.0')
})

test('a first login sends one version request even though the guard restores the same session', async ({ page }) => {
  const version = useDeferredVersion(page)
  await mockAdministrator(page)

  // Entering on the public login page leaves the restore flag unset, so the
  // navigation after logging in restores the session once more; that is the
  // same administrator session and must not fetch the version twice.
  await page.goto('/#/login')
  await signInAgain(page)
  await expect(page).toHaveURL(/#\/books$/)

  expect(version.requests).toHaveLength(1)
  version.answerNext({ version: '1.2.3', revision: fullSha, channel: 'release' })
  await expect(page.locator('.app-header__version-button')).toHaveText('v1.2.3')
})

test('the guest pages never ask for the version', async ({ page }) => {
  const version = useDeferredVersion(page)
  await page.route('**/admin/auth/session', (route) =>
    json(route, { success: false, message: 'Unauthorized' }, 401))
  await page.goto('/#/login')
  await expect(page.locator('.auth-card')).toBeVisible()
  await page.goto('/#/forbidden')
  await expect(page.locator('.auth-card')).toBeVisible()

  expect(version.requests).toHaveLength(0)
})

for (const { name, answer } of [
  { name: 'a 500', answer: (route: Route) => json(route, { success: false, message: 'Broken.' }, 500) },
  { name: 'a malformed success body', answer: (route: Route) => json(route, { success: true, data: { version: 123, revision: null, channel: 'release' } }) },
  { name: 'a network failure', answer: (route: Route) => route.abort() }
]) {
  test(`${name} reads as 版本未知 and the administration keeps working`, async ({ page }) => {
    await mockAdministrator(page)
    await page.route('**/admin/system/version', answer)

    await page.goto('/#/books')
    const button = page.locator('.app-header__version-button')
    await expect(button).toHaveText('版本未知')
    await expect(page.locator('.el-table')).toContainText(starterBook.bookName)
    await expect(page.locator('.el-message--error')).toHaveCount(0)

    // A failed fetch does not retry within the same session.
    await page.getByRole('navigation', { name: '主导航' }).getByRole('link', { name: '单条导入' }).click()
    await expect(page).toHaveURL(/#\/import$/)
    await page.waitForTimeout(300)
    await expect(button).toHaveText('版本未知')
  })
}

test('a 401 of the current version request still clears the session and redirects', async ({ page }) => {
  await mockAdministrator(page)
  await page.route('**/admin/system/version', (route) =>
    json(route, { success: false, message: 'Unauthorized' }, 401))

  await page.goto('/#/books')

  await expect(page).toHaveURL(/#\/login/)
  await expect(page.locator('.app-nav')).toHaveCount(0)
})

test('a 403 of the current version request still redirects to the forbidden page', async ({ page }) => {
  await mockAdministrator(page)
  await page.route('**/admin/system/version', (route) =>
    json(route, { success: false, message: 'Forbidden' }, 403))

  await page.goto('/#/books')

  await expect(page).toHaveURL(/#\/forbidden/)
})

test('a late success from a signed-out session cannot paint the new one', async ({ page }) => {
  const version = useDeferredVersion(page)
  await openBooks(page)

  await page.locator('.session').getByRole('button', { name: '退出登录' }).click()
  await expect(page).toHaveURL(/#\/login$/)

  // Signing in again as the very same username is a new session generation.
  await signInAgain(page)
  expect(version.requests).toHaveLength(2)
  const button = page.locator('.app-header__version-button')
  await expect(button).toHaveText('版本获取中')

  version.answerNext({ version: '9.9.9', revision: null, channel: 'release' })
  await page.waitForTimeout(200)
  await expect(button).toHaveText('版本获取中')

  version.answerNext({ version: '1.2.3', revision: fullSha, channel: 'release' })
  await expect(button).toHaveText('v1.2.3')
})

test('a late 401 from a signed-out session does not clear the new session', async ({ page }) => {
  const version = useDeferredVersion(page)
  await openBooks(page)

  await page.locator('.session').getByRole('button', { name: '退出登录' }).click()
  await expect(page).toHaveURL(/#\/login$/)

  await signInAgain(page)
  await expect(page).toHaveURL(/#\/books$/)

  // The stale request's 401 is dropped before the global auth handling, so
  // the administrator who just signed in stays signed in.
  version.answerNext({ success: false, message: 'Unauthorized' }, 401)
  await page.waitForTimeout(200)
  await expect(page.locator('.session')).toContainText(admin.username)
  await expect(page.locator('.app-nav')).toBeVisible()
  await expect(page).toHaveURL(/#\/books$/)

  version.answerNext({ version: '1.2.3', revision: fullSha, channel: 'release' })
  await expect(page.locator('.app-header__version-button')).toHaveText('v1.2.3')
})

test('keeps the version out of localStorage and sessionStorage', async ({ page }) => {
  await mockAdministrator(page)
  await page.route('**/admin/system/version', (route) =>
    json(route, { success: true, data: { version: '1.2.3', revision: fullSha, channel: 'release' } }))

  await page.goto('/#/books')
  await expect(page.locator('.app-header__version-button')).toHaveText('v1.2.3')

  const stored = await page.evaluate(() => ({
    local: window.localStorage.length,
    session: window.sessionStorage.length
  }))
  expect(stored).toEqual({ local: 0, session: 0 })
})

test('a long prerelease and a long username fit the 375 px header', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 667 })
  await mockAdministrator(page, longUsername)
  await page.route('**/admin/system/version', (route) =>
    json(route, {
      success: true,
      data: {
        version: '1.3.0-rc.1-with-a-very-long-prerelease-suffix-1234567890abcdef',
        revision: fullSha,
        channel: 'release'
      }
    }))

  await page.goto('/#/books')
  await expect(page.locator('.app-header__version-button')).toContainText('v1.3.0-rc.1')

  const overflow = await page.evaluate(() =>
    document.documentElement.scrollWidth - document.documentElement.clientWidth)
  expect(overflow).toBeLessThanOrEqual(0)

  const logout = page.locator('.session').getByRole('button', { name: '退出登录' })
  await expect(logout).toBeVisible()
})
