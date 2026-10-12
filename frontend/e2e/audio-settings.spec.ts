import { expect, test, type Page, type Route } from '@playwright/test'
import AxeBuilder from '@axe-core/playwright'
import { mockAntiforgery } from './support/antiforgery'

const url = '**/admin/system/settings/audio-storage'
const revision = '"1234567890abcdef1234567890abcdef"'
const nextRevision = '"abcdef1234567890abcdef1234567890"'
const textKeys = ['internalEndpoint', 'publicEndpoint', 'region', 'bucket', 'keyPrefix'] as const
function state() {
  return { revision, enabled: false, internalEndpoint: '', publicEndpoint: '', region: 'us-east-1', bucket: 'lexarbor-audio', keyPrefix: 'audio/', hasAccessKey: false, hasSecretKey: false,
    fields: Object.fromEntries(['enabled', ...textKeys, 'accessKey', 'secretKey'].map(key => [key, { source: 'default', editable: true, reason: null }])) }
}
function json(route: Route, body: unknown, status = 200) { return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) }) }
async function admin(page: Page) {
  await mockAntiforgery(page)
  await page.route('**/admin/auth/session', route => json(route, { username: 'ci-admin', roles: ['admin'] }))
  await page.route('**/admin/system/version', route => json(route, { version: '1.0.0', revision: null, channel: 'release' }))
  await page.route('**/admin/auth/logout', route => json(route, {}))
  await page.route('**/api/vocabulary-books/all', route => json(route, { success: true, data: { books: [] } }))
  await page.route('**/admin/system/settings/rate-limits', route => json(route, { revision, adminLogin: { enabled: false, permitLimit: 10, windowSeconds: 300, source: 'default', editable: true }, publicApi: { enabled: false, permitLimit: 300, windowSeconds: 60, source: 'default', editable: true } }))
}
const save = (page: Page) => page.getByRole('button', { name: '保存音频存储设置' })
const notice = (page: Page) => page.getByRole('status', { name: '音频存储状态' })
const access = (page: Page) => page.getByLabel('AccessKey', { exact: true })
const secret = (page: Page) => page.getByLabel('SecretKey', { exact: true })
async function change(page: Page) { await page.getByLabel('区域 Region', { exact: true }).fill('test-region') }

test('first setup sends changed fields with CSRF/revision; one PUT, passwords cleared and reload persisted', async ({ page }) => {
  await admin(page)
  let current = state(), puts = 0
  await page.route(url, async route => {
    if (route.request().method() === 'GET') return json(route, current)
    puts++
    expect(route.request().headers()['if-match']).toBe(revision)
    expect(route.request().headers()['x-signacore-csrf']).toBeTruthy()
    expect(route.request().postDataJSON()).toEqual({ enabled: true, internalEndpoint: 'http://storage.test/', publicEndpoint: 'https://audio.test/', accessKey: { action: 'replace', value: 'synthetic-new-access' }, secretKey: { action: 'replace', value: 'synthetic-new-secret' } })
    await new Promise(resolve => setTimeout(resolve, 150))
    current = { ...current, revision: nextRevision, enabled: true, internalEndpoint: 'http://storage.test/', publicEndpoint: 'https://audio.test/', hasAccessKey: true, hasSecretKey: true }
    return json(route, current)
  })
  await page.goto('/#/settings')
  await page.getByLabel('服务访问地址 InternalEndpoint', { exact: true }).fill('http://storage.test/')
  await page.getByLabel('浏览器播放地址 PublicEndpoint', { exact: true }).fill('https://audio.test/')
  await access(page).fill('synthetic-new-access'); await secret(page).fill('synthetic-new-secret')
  await page.getByRole('checkbox', { name: '音频存储草稿开关' }).check()
  await save(page).click()
  await expect(save(page)).toBeDisabled(); await expect(access(page)).toHaveValue('')
  await expect(notice(page)).toContainText('已保存到配置文件并立即生效')
  await expect(secret(page)).toHaveValue('')
  expect(puts).toBe(1)
  await page.reload()
  await expect(page.getByRole('checkbox', { name: '音频存储草稿开关' })).toBeChecked()
  await expect(page.getByText('已有密钥（不返回原值）')).toHaveCount(2)
})

test('blank keeps existing keys; explicit replacement/clear and disabled incomplete save', async ({ page }) => {
  await admin(page)
  const payloads: unknown[] = []
  await page.route(url, route => {
    if (route.request().method() === 'PUT') payloads.push(route.request().postDataJSON())
    return json(route, { ...state(), hasAccessKey: true, hasSecretKey: true })
  })
  await page.goto('/#/settings'); await change(page); await save(page).click()
  await expect(notice(page)).toContainText('已保存')
  expect(payloads[0]).toEqual({ region: 'test-region', accessKey: { action: 'keep' }, secretKey: { action: 'keep' } })
  await page.getByRole('combobox', { name: 'secretKey操作' }).selectOption('clear')
  await page.getByRole('combobox', { name: 'accessKey操作' }).selectOption('replace')
  await expect(save(page)).toBeDisabled()
  await access(page).fill('synthetic-replacement'); await save(page).click()
  await expect(notice(page)).toContainText('已保存')
  expect(payloads[1]).toEqual({ accessKey: { action: 'replace', value: 'synthetic-replacement' }, secretKey: { action: 'clear' } })
})

for (const status of [400, 409, 412, 428, 503]) {
  test(`safe ${status} feedback never echoes secret; conflict requires reread`, async ({ page }) => {
    await admin(page)
    let puts = 0
    await page.route(url, route => {
      if (route.request().method() === 'GET') return json(route, state())
      puts++; return json(route, { message: 'synthetic-secret-do-not-echo' }, status)
    })
    await page.goto('/#/settings'); await change(page); await secret(page).fill('synthetic-secret-do-not-echo'); await save(page).click()
    await expect(notice(page)).toContainText(`保存失败（${status}）`)
    await expect(page.locator('body')).not.toContainText('synthetic-secret-do-not-echo')
    expect(puts).toBe(1)
    if ([409, 412, 428].includes(status)) {
      await expect(save(page)).toHaveCount(0)
      await page.getByRole('button', { name: '重新读取音频存储' }).click()
      await expect(save(page)).toBeEnabled()
    }
    await expect(secret(page)).toHaveValue('')
  })
}

test('lost PUT response performs GET only, clears secrets, exposes actual state', async ({ page }) => {
  await admin(page)
  let current = state(), puts = 0, gets = 0
  await page.route(url, route => {
    if (route.request().method() === 'GET') { gets++; return json(route, current) }
    puts++; current = { ...state(), region: 'test-region', hasSecretKey: true, revision: nextRevision }; return route.abort('failed')
  })
  await page.goto('/#/settings'); await change(page); await secret(page).fill('synthetic-new-secret'); await save(page).click()
  await expect(notice(page)).toContainText('已回读当前生效配置')
  await expect(secret(page)).toHaveValue(''); expect(puts).toBe(1); expect(gets).toBe(2)
  await expect(save(page)).toBeDisabled()
})

test('lost response and failed read stay unknown until explicit retry; no replay', async ({ page }) => {
  await admin(page)
  let reads = 0, puts = 0
  await page.route(url, route => {
    if (route.request().method() === 'PUT') { puts++; return route.abort('timedout') }
    reads++; return reads === 2 ? route.abort('failed') : json(route, state())
  })
  await page.goto('/#/settings'); await change(page); await secret(page).fill('synthetic-new-secret'); await save(page).click()
  await expect(notice(page)).toContainText('保存结果未知，回读失败')
  await expect(save(page)).toHaveCount(0)
  await page.getByRole('button', { name: '重新读取音频存储' }).click()
  await expect(secret(page)).toHaveValue(''); expect(puts).toBe(1)
})

test('failed/malformed GET stays unknown; readonly deployment fields omitted from PUT', async ({ page }) => {
  await admin(page)
  let reads = 0
  await page.route(url, route => {
    if (route.request().method() === 'PUT') {
      expect(route.request().postDataJSON()).toEqual({ region: 'test-region', secretKey: { action: 'keep' } })
      return json(route, state())
    }
    reads++
    const value = state()
    value.fields.internalEndpoint = { source: 'environment', editable: false, reason: 'Overridden by deployment configuration.' }
    value.fields.accessKey = { source: 'commandLine', editable: false, reason: 'Overridden by deployment configuration.' }
    return json(route, reads === 1 ? {} : reads === 2 ? { revision } : value, reads === 1 ? 503 : 200)
  })
  await page.goto('/#/settings'); await expect(notice(page)).toContainText('配置状态未知')
  await page.getByRole('button', { name: '重新读取音频存储' }).click(); await expect(notice(page)).toContainText('配置状态未知')
  await page.getByRole('button', { name: '重新读取音频存储' }).click()
  await expect(page.getByLabel('服务访问地址 InternalEndpoint', { exact: true })).toBeDisabled()
  await expect(access(page)).toBeDisabled(); await expect(page.getByText('覆盖，网页只读；请修改部署配置。')).toHaveCount(2)
  await change(page); await save(page).click(); await expect(notice(page)).toContainText('已保存')
})

for (const status of [401, 403]) {
  test(`current ${status} respects global authentication navigation`, async ({ page }) => {
    await admin(page); await page.route(url, route => json(route, {}, status))
    await page.goto('/#/settings'); await expect(page).toHaveURL(status === 401 ? /#\/login/ : /#\/forbidden/)
  })
}
for (const leave of ['navigate', 'logout'] as const) {
  test(`${leave} destroys sensitive draft and stale request cannot change next session`, async ({ page }) => {
    await admin(page)
    let pending: Route | undefined
    await page.route(url, route => route.request().method() === 'GET' ? json(route, state()) : (pending = route, undefined))
    await page.goto('/#/settings'); await change(page); await secret(page).fill('synthetic-private-draft'); await save(page).click()
    await expect.poll(() => Boolean(pending)).toBe(true)
    if (leave === 'navigate') await page.getByRole('link', { name: '单条导入', exact: true }).click()
    else await page.getByRole('button', { name: '退出登录' }).click()
    await json(pending!, { message: 'Unauthorized' }, 401).catch(() => undefined)
    await expect(page).toHaveURL(leave === 'navigate' ? /#\/import$/ : /#\/login/)
    await expect(page.getByRole('heading', { name: '音频存储（S3）' })).toHaveCount(0)
    if (leave === 'navigate') {
      await expect(page.locator('.session')).toContainText('ci-admin')
      await page.getByRole('link', { name: '系统设置' }).click(); await expect(secret(page)).toHaveValue('')
    }
  })
}

test('narrow screen, keyboard, accessible labels and no secret in storage/URL/logs', async ({ page }) => {
  await admin(page); await page.route(url, route => json(route, state()))
  const logs: string[] = []; page.on('console', message => logs.push(message.text()))
  await page.setViewportSize({ width: 375, height: 812 }); await page.goto('/#/settings')
  await page.getByRole('checkbox', { name: '音频存储草稿开关' }).focus(); await page.keyboard.press('Space')
  await expect(page.getByRole('checkbox', { name: '音频存储草稿开关' })).toBeChecked()
  await access(page).fill('synthetic-memory-access'); await secret(page).fill('synthetic-memory-secret')
  await expect(access(page)).toHaveAttribute('type', 'password'); await expect(secret(page)).toHaveAttribute('type', 'password')
  expect(await page.evaluate(() => ({ local: { ...localStorage }, session: { ...sessionStorage }, url: location.href }))).toEqual({ local: {}, session: {}, url: page.url() })
  expect(page.url() + logs.join()).not.toContain('synthetic-memory-')
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  const results = await new AxeBuilder({ page }).include('.audio-settings').withTags(['wcag2a', 'wcag2aa']).analyze()
  expect(results.violations).toEqual([])
})

test('late successful PUT cannot populate a replacement settings panel', async ({ page }) => {
  await admin(page)
  let pending: Route | undefined
  await page.route(url, route => route.request().method() === 'GET' ? json(route, state()) : (pending = route, undefined))
  await page.goto('/#/settings'); await change(page); await secret(page).fill('synthetic-private-draft'); await save(page).click()
  await expect.poll(() => Boolean(pending)).toBe(true)
  await page.getByRole('link', { name: '单条导入', exact: true }).click()
  await page.getByRole('link', { name: '系统设置' }).click()
  await expect(page.getByLabel('区域 Region', { exact: true })).toHaveValue('us-east-1')
  await json(pending!, { ...state(), enabled: true, region: 'late-region', hasSecretKey: true, revision: nextRevision }).catch(() => undefined)
  await expect(page.getByLabel('区域 Region', { exact: true })).toHaveValue('us-east-1')
  await expect(page.getByRole('checkbox', { name: '音频存储草稿开关' })).not.toBeChecked()
  await expect(secret(page)).toHaveValue('')
  await expect(page.locator('.session')).toContainText('ci-admin')
})

test('412 requires explicit read and new submission with the fresh revision and no old secret', async ({ page }) => {
  await admin(page)
  let reads = 0, puts = 0
  await page.route(url, route => {
    if (route.request().method() === 'GET') {
      reads++; return json(route, { ...state(), revision: reads === 1 ? revision : nextRevision })
    }
    puts++
    if (puts === 1) return json(route, { message: 'Settings changed.' }, 412)
    expect(route.request().headers()['if-match']).toBe(nextRevision)
    expect(route.request().postDataJSON()).toEqual({ region: 'test-region', accessKey: { action: 'keep' }, secretKey: { action: 'keep' } })
    return json(route, { ...state(), region: 'test-region', revision: nextRevision })
  })
  await page.goto('/#/settings'); await change(page); await secret(page).fill('synthetic-do-not-replay'); await save(page).click()
  await expect(notice(page)).toContainText('保存失败（412）'); expect(puts).toBe(1)
  await page.getByRole('button', { name: '重新读取音频存储' }).click()
  await expect(page.getByLabel('区域 Region', { exact: true })).toHaveValue('test-region')
  await expect(secret(page)).toHaveValue(''); expect(puts).toBe(1)
  await save(page).click(); await expect(notice(page)).toContainText('已保存到配置文件'); expect(puts).toBe(2)
})
