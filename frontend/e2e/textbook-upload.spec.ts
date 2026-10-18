import { expect, test, type Page, type Route } from '@playwright/test'
import AxeBuilder from '@axe-core/playwright'
import { readFileSync } from 'node:fs'
import { mockAntiforgery } from './support/antiforgery'

const bytes = readFileSync(new URL('./fixtures/tone.mp3', import.meta.url))
const version = (n: number) => `00000000-0000-0000-0000-${String(n).padStart(12, '0')}`
const row = (id = 'track') => ({ id, bookId: 'book', unitId: 'unit', title: `Recording ${id}`, displayOrder: 0, hasFile: true, durationSeconds: 1.25, isAvailable: true, version: version(1) })
function json(route: Route, data: unknown, status = 200) {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(status < 400 ? { success: true, data } : { success: false, message: data }) })
}
async function setup(page: Page) {
  const state = {
    rows: [row(), row('other')], policy: { enabled: true, maxUploadBytes: 67108864, maxDurationSeconds: 7200 },
    policyStatus: 200, readStatus: 200, reads: 0, totalCount: 40, calls: [] as { key: string; version: string; file: Buffer; fields: string[]; csrf: string | undefined; path: string }[],
    status: 200, lost: false, receipt: undefined as unknown, held: undefined as Route | undefined, hold: false,
    after: null as (() => void) | null
  }
  await page.addInitScript(() => {
    const runtime = window as unknown as { uploadRequestTimeouts: { path: string; timeout: number }[]; uploadStorageWrites: number }
    runtime.uploadRequestTimeouts = []
    runtime.uploadStorageWrites = 0
    const send = XMLHttpRequest.prototype.send
    const open = XMLHttpRequest.prototype.open
    const paths = new WeakMap<XMLHttpRequest, string>()
    XMLHttpRequest.prototype.open = function (...args: Parameters<typeof open>) { paths.set(this, String(args[1])); return open.apply(this, args) }
    XMLHttpRequest.prototype.send = function (...args: Parameters<typeof send>) {
      runtime.uploadRequestTimeouts.push({ path: paths.get(this) ?? '', timeout: this.timeout })
      return send.apply(this, args)
    }
    const setItem = Storage.prototype.setItem
    Storage.prototype.setItem = function (...args: Parameters<typeof setItem>) { runtime.uploadStorageWrites += 1; return setItem.apply(this, args) }
  })
  await mockAntiforgery(page)
  await page.route('**/admin/auth/session', route => json(route, { username: 'ci-admin', roles: ['admin'] }))
  await page.route('**/admin/auth/logout', route => json(route, {}))
  await page.route('**/admin/system/version', route => json(route, { version: '1.2.3', channel: 'release', revision: null }))
  await page.route('**/admin/vocabulary-books/*/content?*', route => json(route, { book: { id: 'book', bookName: 'Textbook', status: false }, items: [], totalCount: 0, totalPage: 0 }))
  await page.route('**/admin/vocabulary-books/*/units', route => json(route, { units: [
    { id: 'unit', number: 1, title: 'Listening', meaningCount: 0 }, { id: 'second', number: 2, title: 'Next', meaningCount: 0 }
  ] }))
  await page.route(/\/audio-tracks\?.*$/, route => {
    state.reads += 1
    return json(route, state.readStatus === 200 ? { items: state.rows, totalCount: state.totalCount, totalPage: Math.ceil(state.totalCount / 20) } : 'Read unavailable', state.readStatus)
  })
  await page.route('**/audio-tracks/upload-policy', route => json(route, state.policyStatus === 200 ? state.policy : 'Policy unavailable', state.policyStatus))
  await page.route('**/audio-tracks/*/content', async route => {
    const request = route.request()
    const body = request.postDataBuffer()!
    const boundary = request.headers()['content-type']!.split('boundary=')[1]!
    const parts = body.toString('latin1').split(`--${boundary}`).slice(1, -1)
    const fields = parts.map(p => /name="([^"]+)"/.exec(p)![1]!)
    const content = (name: string) => {
      const part = parts.find(p => p.includes(`name="${name}"`))!
      return Buffer.from(part.slice(part.indexOf('\r\n\r\n') + 4, -2), 'latin1')
    }
    state.calls.push({ key: request.headers()['idempotency-key']!, version: content('expectedVersion').toString(), file: content('file'), fields, csrf: request.headers()['x-signacore-csrf'], path: new URL(request.url()).pathname })
    if (state.hold) { state.held = route; await new Promise<void>(() => {}); return }
    if (state.status === 200 && state.receipt === undefined) state.rows[0] = { ...state.rows[0]!, version: version(2), durationSeconds: 0.5746938775510204, isAvailable: false }
    state.after?.()
    if (state.lost) return route.abort('failed')
    return json(route, state.status === 200 ? (state.receipt === undefined ? { trackId: 'track', version: version(2), durationSeconds: 0.5746938775510204 } : state.receipt) : 'Controlled refusal', state.status)
  })
  return state
}
async function open(page: Page) {
  await page.goto('/#/books/book/audio')
  await expect(page.getByText('音频上传已开启', { exact: false })).toBeVisible()
  await expect(page.getByText('正在读取录音目录…')).toHaveCount(0)
}
async function choose(page: Page, buffer = bytes) {
  await page.getByRole('row').filter({ has: page.getByRole('cell', { name: 'Recording track', exact: true }) }).getByRole('button', { name: '上传文件' }).click()
  await page.getByLabel('选择 MP3 文件').setInputFiles({ name: 'tone.mp3', mimeType: 'audio/mpeg', buffer })
}
const feedback = (page: Page) => page.locator('.upload-feedback')
async function start(page: Page) { await page.getByRole('button', { name: '开始上传', exact: true }).click() }
async function unknown(page: Page, state: Awaited<ReturnType<typeof setup>>) {
  state.lost = true
  await choose(page)
  await start(page)
  await expect(feedback(page)).toContainText('上传结果未知')
  await expect(page.getByRole('button', { name: '核对原上传结果' })).toBeEnabled()
}

test('policy is exact, disabled preserves metadata, failed policy retries, and new-file limits send no content', async ({ page }) => {
  const state = await setup(page)
  state.policyStatus = 503
  await page.goto('/#/books/book/audio')
  await expect(page.getByRole('alert')).toContainText('上传限制读取失败')
  await expect(page.getByRole('button', { name: '上传文件' }).first()).toBeDisabled()
  state.policyStatus = 200
  state.policy.enabled = false
  await page.getByRole('button', { name: '重新读取上传限制' }).click()
  await expect(page.getByText('音频上传已关闭（仍可维护目录）', { exact: false })).toBeVisible()
  await expect(page.getByRole('button', { name: '新增录音', exact: true })).toBeEnabled()
  await expect(page.getByRole('button', { name: '编辑', exact: true }).first()).toBeEnabled()
  state.policy.enabled = true
  state.policy.maxUploadBytes = 3
  state.policy.maxDurationSeconds = 2
  await page.getByRole('button', { name: '重新读取上传限制' }).click()
  await expect(page.getByText('文件上限 3 字节 · 时长上限 2 秒。', { exact: false })).toBeVisible()
  await choose(page)
  await expect(feedback(page)).toContainText('文件大小超出')
  await expect(page.getByRole('button', { name: '开始上传', exact: true })).toBeDisabled()
  expect(state.calls).toHaveLength(0)
})

test('one explicit upload has two multipart fields, original bytes/version and CSRF; duplicate clicks do not resend; duration and unavailable state come from reread', async ({ page }) => {
  const state = await setup(page)
  await open(page)
  await choose(page)
  await page.getByRole('button', { name: '开始上传', exact: true }).evaluate((button: HTMLButtonElement) => { button.click(); button.click() })
  await expect(feedback(page)).toContainText('本次上传已确认')
  await expect(page.getByRole('cell', { name: '0.57', exact: true })).toBeVisible()
  await expect(page.getByRole('row').filter({ has: page.getByRole('cell', { name: 'Recording track', exact: true }) })).toContainText('未可用')
  expect(state.calls).toHaveLength(1)
  expect(state.calls[0]!.fields.sort()).toEqual(['expectedVersion', 'file'])
  expect(state.calls[0]!.file.equals(bytes)).toBe(true)
  expect(state.calls[0]!.version).toBe(version(1))
  expect(state.calls[0]!.key).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/)
  expect(state.calls[0]!.csrf).toBe('synthetic-antiforgery-token')
  expect(state.reads).toBe(3)
  const runtime = await page.evaluate(() => { const data = window as unknown as { uploadRequestTimeouts: { path: string; timeout: number }[]; uploadStorageWrites: number }; return { uploadRequestTimeouts: data.uploadRequestTimeouts, uploadStorageWrites: data.uploadStorageWrites } })
  expect(runtime.uploadRequestTimeouts.filter(r => r.path.endsWith('/content') && r.path.includes('/audio-tracks/')).map(r => r.timeout)).toEqual([0])
  expect(runtime.uploadRequestTimeouts.filter(r => r.path.includes('/audio-tracks?')).every(r => r.timeout === 30000)).toBe(true)
  expect(runtime.uploadStorageWrites).toBe(0)
  expect(await page.evaluate(() => [localStorage.length, sessionStorage.length])).toEqual([0, 0])
})

for (const status of [400, 404, 413, 415, 422]) test(`content ${status} is a specific refusal, preserves existing metadata and allows explicit new selection`, async ({ page }) => {
  const state = await setup(page)
  state.status = status
  await open(page)
  await choose(page)
  await start(page)
  await expect(feedback(page)).toContainText('本次请求被拒绝')
  await expect(feedback(page)).not.toContainText('上传已确认')
  await expect(page.getByRole('button', { name: '核对原上传结果' })).toHaveCount(0)
  await expect(page.getByRole('cell', { name: 'Recording track', exact: true })).toBeVisible()
  expect(state.rows[0]!.hasFile).toBe(true)
  expect(state.rows[0]!.version).toBe(version(1))
  await choose(page)
  state.status = 200
  await start(page)
  await expect(feedback(page)).toContainText('本次上传已确认')
  expect(state.calls[1]!.key).not.toBe(state.calls[0]!.key)
})

for (const status of [409, 429, 500, 502, 503]) test(`${status} never proves an unknown original upload failed; exact replay survives policy tightening and row replacement`, async ({ page }) => {
  const state = await setup(page)
  await open(page)
  await unknown(page, state)
  state.lost = false
  state.status = status
  state.policy.maxUploadBytes = 1
  state.policy.enabled = false
  state.rows[0] = { ...row(), title: 'Later replacement', version: version(9), durationSeconds: 9 }
  await page.getByRole('button', { name: '重新读取上传限制' }).click()
  await expect(page.getByText('音频上传已关闭', { exact: false })).toBeVisible()
  await page.getByRole('button', { name: '核对原上传结果' }).click()
  await expect.poll(() => state.calls.length).toBe(2)
  await expect(page.getByRole('button', { name: '核对原上传结果' })).toBeEnabled()
  expect(state.calls[1]!.key).toBe(state.calls[0]!.key)
  expect(state.calls[1]!.version).toBe(version(1))
  expect(state.calls[1]!.file.equals(bytes)).toBe(true)
  await expect(feedback(page)).toContainText(status === 409 ? '结果尚未确认' : status === 429 ? '不能证明此前未知尝试已失败' : '上传结果未知')
  expect(state.calls).toHaveLength(2)
  state.status = 200
  state.receipt = { trackId: 'track', version: version(2), durationSeconds: 0.57 }
  await page.getByRole('button', { name: '核对原上传结果' }).click()
  await expect(feedback(page)).toContainText('本次上传已确认')
  await expect(page.getByRole('cell', { name: 'Later replacement', exact: true })).toBeVisible()
  await expect(page.getByRole('cell', { name: '9.00', exact: true })).toBeVisible()
  expect(state.rows[0]!.version).toBe(version(9))
  expect(state.calls[2]!.key).toBe(state.calls[0]!.key)
})

for (const receipt of [null, { trackId: 'other', version: version(2), durationSeconds: 1 }, { trackId: 'track', version: 'bad', durationSeconds: 1 }, { trackId: 'track', version: version(2), durationSeconds: 0 }, { trackId: 'track', version: version(2), durationSeconds: '1' }]) test(`unreadable success ${JSON.stringify(receipt)} stays unknown and never mutates catalog from its receipt`, async ({ page }) => {
  const state = await setup(page)
  state.receipt = receipt
  await open(page)
  await choose(page)
  await start(page)
  await expect(feedback(page)).toContainText('上传结果未知')
  await expect(page.getByRole('button', { name: '核对原上传结果' })).toBeEnabled()
  expect(state.calls).toHaveLength(1)
  expect(state.rows[0]!.version).toBe(version(1))
})

test('old receipt after target removal confirms only that operation; confirmed upload and failed read remain distinct', async ({ page }) => {
  const state = await setup(page)
  await open(page)
  await unknown(page, state)
  state.rows = []
  state.lost = false
  state.receipt = { trackId: 'track', version: version(2), durationSeconds: 0.57 }
  await page.getByRole('button', { name: '核对原上传结果' }).click()
  await expect(feedback(page)).toContainText('本次上传已确认')
  await expect(page.getByRole('cell', { name: 'Recording track', exact: true })).toHaveCount(0)
  expect(state.rows).toHaveLength(0)
  state.rows = [row()]
  await page.getByRole('button', { name: '重新读取目录' }).click()
  await choose(page)
  state.after = () => { state.readStatus = 503 }
  await start(page)
  await expect(feedback(page)).toContainText('本次上传已确认')
  await expect(feedback(page)).toContainText('当前目录读取失败')
  await expect(page.getByRole('button', { name: '核对原上传结果' })).toHaveCount(0)
})

for (const target of ['unit', 'page', 'size', 'track', 'book', 'leave']) test(`unknown attempt ${target} change asks before scope changes; cancellation preserves exact replay`, async ({ page }) => {
  const state = await setup(page)
  await open(page)
  await unknown(page, state)
  const navigate = async () => {
    if (target === 'unit') { await page.locator('.unit-select').click(); await page.getByRole('option', { name: '单元 2 · Next' }).click() }
    if (target === 'page') await page.getByRole('button', { name: '下一页' }).click()
    if (target === 'size') { await page.locator('.page-size').click(); await page.getByRole('option', { name: '10 条/页' }).click() }
    if (target === 'track') await page.getByRole('row').filter({ has: page.getByRole('cell', { name: 'Recording other', exact: true }) }).getByRole('button', { name: '上传文件' }).click()
    if (target === 'book') await page.evaluate(() => { location.hash = '/books/next/audio' })
    if (target === 'leave') await page.getByRole('button', { name: '教材词表', exact: true }).click()
  }
  await navigate()
  await expect(page.getByRole('dialog', { name: '放弃继续核对？' })).toContainText('不能撤销服务器')
  await page.getByRole('button', { name: '继续核对', exact: true }).click()
  await expect(page.getByRole('button', { name: '核对原上传结果' })).toBeEnabled()
  await expect(page).toHaveURL(/books\/book\/audio$/)
  state.lost = false
  state.status = 503
  await page.getByRole('button', { name: '核对原上传结果' }).click()
  await expect.poll(() => state.calls.length).toBe(2)
  await expect(page.getByRole('button', { name: '核对原上传结果' })).toBeEnabled()
  expect(state.calls[1]!.key).toBe(state.calls[0]!.key)
  expect(state.calls[1]!.path).toBe('/admin/vocabulary-books/book/units/unit/audio-tracks/track/content')
  await navigate()
  await page.getByRole('button', { name: '放弃核对并离开', exact: true }).click()
  await expect(page.getByRole('button', { name: '核对原上传结果' })).toHaveCount(0)
  expect(state.calls).toHaveLength(2)
})

test('explicit abandonment rereads before a new logical upload uses a fresh key and current version', async ({ page }) => {
  const state = await setup(page)
  await open(page)
  await unknown(page, state)
  await page.getByRole('button', { name: '放弃继续核对', exact: true }).click()
  await page.getByRole('button', { name: '放弃核对并离开', exact: true }).click()
  await expect(feedback(page)).toContainText('不代表撤销服务器结果')
  await expect(page.getByRole('button', { name: '核对原上传结果' })).toHaveCount(0)
  state.lost = false
  await choose(page)
  await start(page)
  await expect(feedback(page)).toContainText('本次上传已确认')
  expect(state.calls[1]!.key).not.toBe(state.calls[0]!.key)
  expect(state.calls[1]!.version).toBe(version(2))
})

for (const status of [200, 401, 403]) test(`in-flight content ${status} after accepted book switch cannot affect replacement view/session`, async ({ page }) => {
  const state = await setup(page)
  state.hold = true
  await open(page)
  await choose(page)
  await start(page)
  await expect.poll(() => Boolean(state.held)).toBe(true)
  await expect(page.getByRole('row').filter({ has: page.getByRole('cell', { name: 'Recording track', exact: true }) }).getByRole('button', { name: '编辑', exact: true })).toBeDisabled()
  await page.evaluate(() => { location.hash = '/books/next/audio' })
  await expect(page.getByRole('dialog', { name: '放弃继续核对？' })).toBeVisible()
  await page.getByRole('button', { name: '继续核对', exact: true }).click()
  await expect(page.getByRole('button', { name: '取消上传等待（结果可能未知）' })).toBeVisible()
  await expect(page.getByRole('dialog', { name: '放弃继续核对？' })).toHaveCount(0)
  await expect(page).toHaveURL(/books\/book\/audio$/)
  await page.evaluate(() => { location.hash = '/books/next/audio' })
  await page.getByRole('button', { name: '放弃核对并离开', exact: true }).click()
  await expect(page).toHaveURL(/books\/next\/audio$/)
  await state.held?.fulfill({ status, contentType: 'application/json', body: JSON.stringify(status === 200 ? { success: true, data: { trackId: 'track', version: version(2), durationSeconds: 1 } } : { success: false, message: 'Late refusal' }) }).catch(() => {})
  await expect(feedback(page)).toHaveText('')
  await expect(page.locator('.session')).toContainText('ci-admin')
  await expect(page).toHaveURL(/books\/next\/audio$/)
  expect(await page.evaluate(() => [localStorage.length, sessionStorage.length])).toEqual([0, 0])
})

for (const status of [401, 403]) test(`current content ${status} follows shared authentication/CSRF handling`, async ({ page }) => {
  const state = await setup(page)
  state.status = status
  await open(page)
  await choose(page)
  await start(page)
  await expect(page).toHaveURL(status === 401 ? /login/ : /forbidden/)
  expect(state.calls).toHaveLength(1)
  expect(state.calls[0]!.csrf).toBe('synthetic-antiforgery-token')
  await expect(page.getByRole('dialog', { name: '放弃继续核对？' })).toHaveCount(0)
})

test('cancel ends waiting promptly, retains original File despite cleared input, and replays no automatic requests', async ({ page }) => {
  const state = await setup(page)
  state.hold = true
  await open(page)
  await choose(page)
  await start(page)
  await expect.poll(() => Boolean(state.held)).toBe(true)
  await page.getByRole('button', { name: '取消上传等待（结果可能未知）' }).click()
  await expect(feedback(page)).toContainText('上传结果未知')
  expect(state.calls).toHaveLength(1)
  await state.held?.fulfill({ status: 503, body: '{}' }).catch(() => {})
  state.hold = false
  state.status = 503
  await page.getByRole('button', { name: '核对原上传结果' }).click()
  await expect.poll(() => state.calls.length).toBe(2)
  expect(state.calls[1]!.file.equals(bytes)).toBe(true)
  expect(state.calls[1]!.key).toBe(state.calls[0]!.key)
})

for (const pendingPrompt of [false, true]) test(`logout starts immediate cleanup${pendingPrompt ? ' even with an unanswered leave prompt' : ''}, late upload cannot affect a new login`, async ({ page }) => {
  const state = await setup(page)
  state.hold = true
  await open(page)
  await choose(page)
  await start(page)
  await expect.poll(() => Boolean(state.held)).toBe(true)
  if (pendingPrompt) {
    await page.getByRole('button', { name: '教材词表', exact: true }).click()
    await expect(page.getByRole('dialog', { name: '放弃继续核对？' })).toBeVisible()
  }
  let logout: Route | undefined
  await page.route('**/admin/auth/logout', async route => { logout = route; await new Promise<void>(() => {}) })
  await page.locator('.session').getByRole('button', { name: '退出登录' }).evaluate((button: HTMLButtonElement) => button.click())
  await expect.poll(() => Boolean(logout)).toBe(true)
  await expect(page.getByRole('button', { name: '取消上传等待（结果可能未知）' })).toHaveCount(0)
  await expect(page.getByRole('dialog', { name: '放弃继续核对？' })).toHaveCount(0)
  await logout?.fulfill({ status: 200, contentType: 'application/json', body: '{"success":true}' })
  await expect(page).toHaveURL(/login/)
  await page.goto('/?signed-in-again=1#/books/book/audio')
  await expect(page.locator('.session')).toContainText('ci-admin')
  await state.held?.fulfill({ status: 401, contentType: 'application/json', body: '{"success":false,"message":"Late session failure"}' }).catch(() => {})
  await expect(page).toHaveURL(/books\/book\/audio$/)
  await expect(feedback(page)).toHaveText('')
})

for (const width of [1440, 768]) test(`upload keyboard/focus/live feedback, axe and no document overflow at ${width}`, async ({ page }) => {
  await setup(page)
  await page.setViewportSize({ width, height: 1000 })
  await open(page)
  const button = page.getByRole('row').filter({ has: page.getByRole('cell', { name: 'Recording track', exact: true }) }).getByRole('button', { name: '上传文件' })
  await button.focus()
  await page.keyboard.press('Enter')
  await expect(page.getByLabel('选择 MP3 文件')).toBeFocused()
  await page.getByLabel('选择 MP3 文件').setInputFiles({ name: 'tone.mp3', mimeType: 'audio/mpeg', buffer: bytes })
  await expect(feedback(page)).toHaveAttribute('aria-live', 'polite')
  await page.waitForFunction(() => !document.querySelector('.upload-actions .el-button--primary')?.getAnimations({ subtree: true }).some(animation => animation.playState === 'running'))
  expect((await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze()).violations).toEqual([])
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  await page.screenshot({ path: `test-results/textbook-upload-${width}.png`, fullPage: true })
  await page.getByRole('button', { name: '关闭上传', exact: true }).click()
  await expect(button).toBeFocused()
})

for (const status of [200, 401, 403]) test(`late upload policy ${status} after unit change cannot affect the new policy or session`, async ({ page }) => {
  await setup(page)
  await open(page)
  let held: Route | undefined
  await page.route('**/units/unit/audio-tracks/upload-policy', async route => { held = route; await new Promise<void>(() => {}) })
  await page.getByRole('button', { name: '重新读取上传限制' }).click()
  await expect.poll(() => Boolean(held)).toBe(true)
  await page.locator('.unit-select').click()
  await page.getByRole('option', { name: '单元 2 · Next' }).click()
  await expect(page.getByText('音频上传已开启', { exact: false })).toBeVisible()
  await held?.fulfill({ status, contentType: 'application/json', body: JSON.stringify(status === 200 ? { success: true, data: { enabled: false, maxUploadBytes: 1, maxDurationSeconds: 1 } } : { success: false, message: 'Late policy refusal' }) }).catch(() => {})
  await expect(page).toHaveURL(/books\/book\/audio$/)
  await expect(page.getByText('文件上限 67108864 字节', { exact: false })).toBeVisible()
  await expect(page.getByRole('button', { name: '上传文件' }).first()).toBeEnabled()
})

test('canceled CSRF wait sends no content and preserves only the original attempt for explicit retry', async ({ page }) => {
  const state = await setup(page)
  let held: Route | undefined
  await page.route('**/admin/auth/csrf', async route => {
    if (!held) { held = route; await new Promise<void>(() => {}) }
    else await route.fulfill({ contentType: 'application/json', body: '{"token":"synthetic-antiforgery-token"}' })
  })
  await open(page)
  await choose(page)
  await start(page)
  await expect.poll(() => Boolean(held)).toBe(true)
  await page.getByRole('button', { name: '取消上传等待（结果可能未知）' }).click()
  await expect(feedback(page)).toContainText('上传结果未知', { timeout: 2000 })
  expect(state.calls).toHaveLength(0)
  await held?.fulfill({ contentType: 'application/json', body: '{"token":"synthetic-antiforgery-token"}' })
  await page.waitForTimeout(100)
  expect(state.calls).toHaveLength(0)
  await page.getByRole('button', { name: '核对原上传结果' }).click()
  await expect(feedback(page)).toContainText('本次上传已确认')
  expect(state.calls).toHaveLength(1)
  expect(state.calls[0]!.version).toBe(version(1))
  expect(state.calls[0]!.file.equals(bytes)).toBe(true)
})

for (const status of [401, 403]) test(`obsolete upload CSRF ${status} cannot clear another book's current attempt`, async ({ page }) => {
  const state = await setup(page)
  let held: Route | undefined
  let count = 0
  await page.route('**/admin/auth/csrf', async route => {
    count += 1
    if (count === 1) { held = route; await new Promise<void>(() => {}) }
    else await route.fulfill({ contentType: 'application/json', body: '{"token":"synthetic-antiforgery-token"}' })
  })
  await open(page)
  await choose(page)
  await start(page)
  await expect.poll(() => Boolean(held)).toBe(true)
  await page.evaluate(() => { location.hash = '/books/next/audio' })
  await page.getByRole('button', { name: '放弃核对并离开', exact: true }).click()
  await expect(page).toHaveURL(/books\/next\/audio$/)
  await choose(page)
  await start(page)
  await expect(feedback(page)).toContainText('本次上传已确认')
  await held?.fulfill({ status, contentType: 'application/json', body: '{"success":false,"message":"Obsolete CSRF failure"}' }).catch(() => {})
  await page.waitForTimeout(100)
  await expect(page).toHaveURL(/books\/next\/audio$/)
  expect(state.calls).toHaveLength(1)
  expect(state.calls[0]!.path).toContain('/vocabulary-books/next/')
})

test('browser refresh warns before discarding an unknown attempt, and confirmed abandonment removes that warning', async ({ page }) => {
  const state = await setup(page)
  await open(page)
  await unknown(page, state)
  const dismissed = page.waitForEvent('dialog').then(async dialog => { expect(dialog.type()).toBe('beforeunload'); await dialog.dismiss() })
  await page.evaluate(() => location.reload())
  await dismissed
  await expect(page.getByRole('button', { name: '核对原上传结果' })).toBeVisible()
  await page.getByRole('button', { name: '放弃继续核对', exact: true }).click()
  await page.getByRole('button', { name: '放弃核对并离开', exact: true }).click()
  await expect(feedback(page)).toContainText('已放弃继续核对')
  let dialogs = 0
  page.on('dialog', async dialog => { dialogs += 1; await dialog.accept() })
  await page.reload()
  await expect(page.getByText('音频上传已开启', { exact: false })).toBeVisible()
  expect(dialogs).toBe(0)
  expect(state.calls).toHaveLength(1)
})

test('abandonment with failed read-back blocks another new upload until the current catalog is read', async ({ page }) => {
  const state = await setup(page)
  await open(page)
  await unknown(page, state)
  state.readStatus = 503
  await page.getByRole('button', { name: '放弃继续核对', exact: true }).click()
  await page.getByRole('button', { name: '放弃核对并离开', exact: true }).click()
  await expect(page.getByText('目录读取失败：Read unavailable')).toBeVisible()
  await expect(page.getByRole('button', { name: '上传文件' }).first()).toBeDisabled()
  state.readStatus = 200
  await page.getByRole('button', { name: '重试目录', exact: true }).click()
  await expect(page.getByRole('button', { name: '上传文件' }).first()).toBeEnabled()
  expect(state.calls).toHaveLength(1)
})

for (const malformed of [
  { enabled: 'true', maxUploadBytes: 20, maxDurationSeconds: 20 },
  { enabled: true, maxUploadBytes: 0, maxDurationSeconds: 20 },
  { enabled: true, maxUploadBytes: 20, maxDurationSeconds: 0 },
  { enabled: true, maxUploadBytes: 20, maxDurationSeconds: 20, extra: 'not-a-policy-field' }
]) test(`unreadable policy ${JSON.stringify(malformed)} never enables new content`, async ({ page }) => {
  const state = await setup(page)
  await page.route('**/audio-tracks/upload-policy', route => json(route, malformed))
  await page.goto('/#/books/book/audio')
  await expect(page.getByRole('alert')).toContainText('上传限制读取失败')
  await expect(page.getByRole('button', { name: '上传文件' }).first()).toBeDisabled()
  expect(state.calls).toHaveLength(0)
})

test('empty selection is rejected, exact byte limit is accepted and single input hints do not certify MP3 bytes', async ({ page }) => {
  const state = await setup(page)
  state.policy.maxUploadBytes = bytes.length
  await open(page)
  await choose(page, Buffer.alloc(0))
  await expect(feedback(page)).toContainText('文件为空')
  await expect(page.getByRole('button', { name: '开始上传', exact: true })).toBeDisabled()
  await expect(page.getByLabel('选择 MP3 文件')).not.toHaveAttribute('multiple')
  await page.getByLabel('选择 MP3 文件').setInputFiles({ name: 'tone.mp3', mimeType: 'audio/mpeg', buffer: bytes })
  await start(page)
  await expect(feedback(page)).toContainText('本次上传已确认')
  expect(state.calls).toHaveLength(1)
})

for (const width of [1440, 768]) test(`unknown upload leave prompt preserves keyboard cancellation and accessible feedback at ${width}`, async ({ page }) => {
  const state = await setup(page)
  await page.setViewportSize({ width, height: 1000 })
  await open(page)
  await unknown(page, state)
  await page.getByRole('button', { name: '教材词表', exact: true }).click()
  await expect(page.getByRole('dialog', { name: '放弃继续核对？' })).toBeVisible()
  await page.waitForFunction(() => !document.querySelector('.fade-in-linear-enter-active, .msgbox-fade-enter-active'))
  expect((await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze()).violations).toEqual([])
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  await page.screenshot({ path: `test-results/textbook-upload-unknown-${width}.png`, fullPage: true })
  await page.keyboard.press('Escape')
  await expect(page.getByRole('dialog', { name: '放弃继续核对？' })).toHaveCount(0)
  await expect(page).toHaveURL(/books\/book\/audio$/)
  await expect(page.getByRole('button', { name: '核对原上传结果' })).toBeEnabled()
  expect(state.calls).toHaveLength(1)
})

test('a browser without secure UUID support gets actionable feedback and sends no content', async ({ page }) => {
  const state = await setup(page)
  await page.addInitScript(() => { Object.defineProperty(crypto, 'randomUUID', { value: undefined }) })
  await open(page)
  await choose(page)
  await start(page)
  await expect(feedback(page)).toContainText('请使用 HTTPS')
  await expect(page.getByRole('button', { name: '核对原上传结果' })).toHaveCount(0)
  expect(state.calls).toHaveLength(0)
})

test('read-back cannot silently move an unknown attempt off a removed last page; confirmation then permits the normal fallback', async ({ page }) => {
  const state = await setup(page)
  await open(page)
  await page.getByRole('button', { name: '下一页' }).click()
  await expect(page.locator('.el-pager .is-active')).toHaveText('2')
  await unknown(page, state)
  state.totalCount = 1
  state.rows = [state.rows[0]!]
  state.status = 503
  state.lost = false
  await page.getByRole('button', { name: '核对原上传结果' }).click()
  await expect(page.getByRole('alert')).toContainText('目录页码已变化，原上传仍待核对')
  await expect(page.locator('.el-pager .is-active')).toHaveText('2')
  await expect(page.getByRole('button', { name: '核对原上传结果' })).toBeEnabled()
  expect(state.calls).toHaveLength(2)
  expect(state.calls[1]!.key).toBe(state.calls[0]!.key)
  state.status = 200
  state.receipt = { trackId: 'track', version: version(2), durationSeconds: 0.57 }
  await page.getByRole('button', { name: '核对原上传结果' }).click()
  await expect(feedback(page)).toContainText('本次上传已确认')
  await expect(page.locator('.el-pager .is-active')).toHaveText('1')
  expect(state.calls[2]!.key).toBe(state.calls[0]!.key)
})


test('opening a new upload rereads its current row instead of freezing a previously displayed version', async ({ page }) => {
  const state = await setup(page)
  await open(page)
  state.rows[0] = { ...row(), version: version(9) }
  await choose(page)
  await expect(page.getByText(`原提交条件版本：${version(9)}`)).toBeVisible()
  await start(page)
  await expect(feedback(page)).toContainText('本次上传已确认')
  expect(state.calls[0]!.version).toBe(version(9))
})

for (const action of ['close', 'track']) test(`same-page ${action} abandonment rereads before another logical upload`, async ({ page }) => {
  const state = await setup(page)
  await open(page)
  await unknown(page, state)
  const reads = state.reads
  state.rows[0] = { ...row(), version: version(9) }
  state.rows[1] = { ...row('other'), version: version(8) }
  if (action === 'close') await page.getByRole('button', { name: '关闭上传', exact: true }).click()
  else await page.getByRole('row').filter({ has: page.getByRole('cell', { name: 'Recording other', exact: true }) }).getByRole('button', { name: '上传文件' }).click()
  await page.getByRole('button', { name: '放弃核对并离开', exact: true }).click()
  await expect.poll(() => state.reads).toBe(reads + 1)
  await expect(page.getByRole('button', { name: '核对原上传结果' })).toHaveCount(0)
  if (action === 'close') await choose(page)
  else await page.getByLabel('选择 MP3 文件').setInputFiles({ name: 'tone.mp3', mimeType: 'audio/mpeg', buffer: bytes })
  state.lost = false
  state.receipt = { trackId: action === 'close' ? 'track' : 'other', version: version(10), durationSeconds: 1 }
  await start(page)
  await expect(feedback(page)).toContainText('本次上传已确认')
  expect(state.calls[1]!.version).toBe(version(action === 'close' ? 9 : 8))
  expect(state.calls[1]!.key).not.toBe(state.calls[0]!.key)
})
