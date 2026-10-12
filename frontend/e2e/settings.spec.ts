import { expect, test, type Page, type Route } from '@playwright/test'
import { mockAntiforgery } from './support/antiforgery'

const url = '**/admin/system/settings/rate-limits'
const revision = '"1234567890abcdef1234567890abcdef"'
const nextRevision = '"abcdef1234567890abcdef1234567890"'
function state(enabled = false, source = 'default', editable = true) {
  return { revision, adminLogin: { enabled: false, permitLimit: 7, windowSeconds: 120, source, editable },
    publicApi: { enabled, permitLimit: 2, windowSeconds: 60, source: 'file', editable: true } }
}
function json(route: Route, value: unknown, status = 200) {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(value) })
}
async function admin(page: Page) {
  await mockAntiforgery(page)
  await page.route('**/admin/auth/session', route => json(route, { username: 'ci-admin', roles: ['admin'] }))
  await page.route('**/admin/system/version', route => json(route, { version: '1.0.0', revision: null, channel: 'release' }))
  await page.route('**/admin/auth/logout', route => json(route, {}))
  await page.route('**/api/vocabulary-books/all', route => json(route, { success: true, data: { books: [] } }))
}
const publicSwitch = (page: Page) => page.getByRole('checkbox', { name: '公共 API草稿开关' })
const save = (page: Page) => page.getByRole('button', { name: '保存限流设置' })

test('actual custom values, draft, If-Match and CSRF, GET confirmation and one submission', async ({ page }) => {
  await admin(page)
  let current = state()
  let puts = 0
  let gets = 0
  await page.route(url, async route => {
    if (route.request().method() === 'GET') { gets++; return json(route, current) }
    puts++
    expect(route.request().postDataJSON()).toEqual({ publicApiEnabled: true })
    expect(route.request().headers()['if-match']).toBe(revision)
    expect(route.request().headers()['x-signacore-csrf']).toBeTruthy()
    await new Promise(resolve => setTimeout(resolve, 200))
    current = { ...state(true), revision: nextRevision }
    return json(route, current)
  })
  await page.goto('/#/settings')
  await expect(page.getByText('7 次 / 120 秒')).toBeVisible()
  await publicSwitch(page).focus()
  await page.keyboard.press('Space')
  await expect(page.getByText('当前生效：关闭')).toHaveCount(2)
  await save(page).click()
  await expect(save(page)).toBeDisabled()
  await expect(page.getByRole('status')).toHaveText('保存后已回读确认当前生效状态。')
  expect(puts).toBe(1)
  expect(gets).toBe(2)
  await expect(page.getByText('当前生效：开启')).toBeVisible()
  await expect(save(page)).toBeDisabled()
})
for (const status of [400, 413, 503]) {
  test(`HTTP ${status} retains draft and actual state`, async ({ page }) => {
    await admin(page)
    await page.route(url, route => route.request().method() === 'GET' ? json(route, state()) : json(route, { message: 'Settings unavailable.' }, status))
    await page.goto('/#/settings')
    await publicSwitch(page).check()
    await save(page).click()
    await expect(page.getByRole('status')).toContainText(`保存失败（${status}）`)
    await expect(publicSwitch(page)).toBeChecked()
    await expect(page.getByText('当前生效：关闭')).toHaveCount(2)
    await expect(save(page)).toBeEnabled()
  })
}
test('version conflict requires a fresh read and preserves draft', async ({ page }) => {
  await admin(page)
  let reads = 0
  await page.route(url, route => {
    if (route.request().method() === 'PUT') return json(route, { message: 'Settings changed.' }, 412)
    reads++
    return json(route, { ...state(), revision: reads > 1 ? nextRevision : revision })
  })
  await page.goto('/#/settings')
  await publicSwitch(page).check()
  await save(page).click()
  await expect(page.getByText('当前生效状态：未知')).toBeVisible()
  await expect(save(page)).toBeDisabled()
  await page.getByRole('button', { name: '重新读取' }).click()
  await expect(publicSwitch(page)).toBeChecked()
  await expect(save(page)).toBeEnabled()
})
test('failed or malformed reads show unknown; retry shows actual and readonly sources', async ({ page }) => {
  await admin(page)
  let reads = 0
  await page.route(url, route => {
    reads++
    return reads === 1 ? json(route, {}, 503) : reads === 2 ? json(route, { revision }) : json(route, state(true, 'environment', false))
  })
  await page.goto('/#/settings')
  await expect(page.getByText('当前生效状态：未知')).toBeVisible()
  await page.getByRole('button', { name: '重新读取' }).click()
  await expect(page.getByRole('status')).toContainText('状态未知')
  await page.getByRole('button', { name: '重新读取' }).click()
  await expect(publicSwitch(page)).toBeChecked()
  await expect(page.getByRole('checkbox', { name: '管理员登录草稿开关' })).toBeDisabled()
  await expect(page.getByText('环境变量覆盖（网页只读）')).toBeVisible()
})
test('lost PUT response is checked with GET without resubmitting', async ({ page }) => {
  await admin(page)
  let current = state()
  let puts = 0
  await page.route(url, route => {
    if (route.request().method() === 'GET') return json(route, current)
    puts++
    current = { ...state(true), revision: nextRevision }
    return route.abort('failed')
  })
  await page.goto('/#/settings')
  await publicSwitch(page).check()
  await save(page).click()
  await expect(page.getByRole('status')).toContainText('保存响应未确认，已重新读取')
  await expect(page.getByText('当前生效：开启')).toBeVisible()
  expect(puts).toBe(1)
})
test('lost save and failed confirmation stay unknown until retry', async ({ page }) => {
  await admin(page)
  let reads = 0
  let puts = 0
  await page.route(url, route => {
    if (route.request().method() === 'PUT') { puts++; return route.abort('failed') }
    reads++
    if (reads === 2) return route.abort('failed')
    return json(route, state(reads > 2))
  })
  await page.goto('/#/settings')
  await publicSwitch(page).check()
  await save(page).click()
  await expect(page.getByRole('status')).toContainText('保存结果未知，回读失败')
  await expect(save(page)).toBeDisabled()
  await page.getByRole('button', { name: '重新读取' }).click()
  await expect(publicSwitch(page)).toBeChecked()
  await expect(page.getByText('当前生效：开启')).toBeVisible()
  expect(puts).toBe(1)
})
test('narrow screen and keyboard controls fit viewport', async ({ page }) => {
  await admin(page)
  await page.route(url, route => json(route, state(false, 'commandLine', false)))
  await page.setViewportSize({ width: 375, height: 812 })
  await page.goto('/#/settings')
  await expect(page.getByRole('link', { name: '系统设置' })).toBeVisible()
  await publicSwitch(page).focus()
  await page.keyboard.press('Space')
  await expect(publicSwitch(page)).toBeChecked()
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
})
test('leaving cancels reads; late 401 cannot clear session', async ({ page }) => {
  await admin(page)
  let pending: Route | undefined
  await page.route(url, route => { pending = route })
  await page.goto('/#/settings')
  await expect.poll(() => Boolean(pending)).toBe(true)
  await page.getByRole('link', { name: '单条导入', exact: true }).click()
  await expect(page).toHaveURL(/#\/import$/)
  await json(pending!, { message: 'Unauthorized' }, 401).catch(() => undefined)
  await expect(page.locator('.session')).toContainText('ci-admin')
  await expect(page).toHaveURL(/#\/import$/)
})
test('logout clears pending settings and late results cannot restore panel', async ({ page }) => {
  await admin(page)
  let pending: Route | undefined
  await page.route(url, route => { pending = route })
  await page.goto('/#/settings')
  await expect.poll(() => Boolean(pending)).toBe(true)
  await page.getByRole('button', { name: '退出登录' }).click()
  await expect(page).toHaveURL(/#\/login/)
  await json(pending!, state(true)).catch(() => undefined)
  await expect(page.getByRole('heading', { name: '请求限流' })).toHaveCount(0)
})
test('unauthorized navigation returns to login with exact settings target', async ({ page }) => {
  await page.route('**/admin/auth/session', route => json(route, { message: 'Unauthorized' }, 401))
  await page.route('**/admin/auth/start?returnUrl=%2Fsettings', route => route.fulfill({ contentType: 'text/html', body: '<p>Sign in</p>' }))
  await page.goto('/#/settings')
  await expect(page).toHaveURL(/#\/login\?redirect=\/settings/)
  await page.getByRole('button', { name: '使用 SignaCore 登录' }).click()
  await expect(page).toHaveURL(/admin\/auth\/start\?returnUrl=%2Fsettings/)
})
test('forbidden session has no settings navigation', async ({ page }) => {
  await page.route('**/admin/auth/session', route => json(route, { message: 'Forbidden' }, 403))
  await page.goto('/#/settings')
  await expect(page).toHaveURL(/#\/forbidden/)
  await expect(page.getByRole('link', { name: '系统设置' })).toHaveCount(0)
})


test('successful PUT with unavailable GET confirmation does not claim save failure', async ({ page }) => {
  await admin(page)
  let reads = 0
  await page.route(url, route => {
    if (route.request().method() === 'PUT') return json(route, { ...state(true), revision: nextRevision })
    reads++
    return reads === 1 ? json(route, state()) : json(route, { message: 'Unavailable.' }, 503)
  })
  await page.goto('/#/settings')
  await publicSwitch(page).check()
  await save(page).click()
  await expect(page.getByRole('status')).toContainText('保存结果未知，回读失败')
  await expect(page.getByRole('status')).not.toContainText('保存失败')
  await expect(save(page)).toBeDisabled()
})
