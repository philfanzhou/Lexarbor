import { expect, test, type Page, type Route } from '@playwright/test'
import AxeBuilder from '@axe-core/playwright'
import { readFileSync } from 'node:fs'
import { mockAntiforgery } from './support/antiforgery'

const tone = readFileSync(new URL('./fixtures/tone.mp3', import.meta.url))
const row = (id: string, hasFile = true) => ({ id, bookId: 'book', unitId: 'unit', title: `Recording ${id}`, displayOrder: 0, hasFile, durationSeconds: hasFile ? 0.57 : null, isAvailable: false, version: '00000000-0000-0000-0000-000000000001' })
const link = (trackId = 'first', expiresAt = new Date(Date.now() + 60000).toISOString(), url = 'http://127.0.0.1:4173/fixture/tone.mp3') => ({ trackId, url, expiresAt })
function json(route: Route, data: unknown, status = 200) {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(status < 400 ? { success: true, data } : { success: false, message: data }) })
}
type Runtime = Window & { __media: HTMLAudioElement[]; __decoded: number; __settle: (() => void)[]; __writes: number; __old: (() => void)[] }
async function instrument(page: Page, mode = 'playing') {
  await page.addInitScript(mode => {
    const runtime = window as unknown as Runtime
    runtime.__media = []; runtime.__decoded = 0; runtime.__settle = []; runtime.__writes = 0; runtime.__old = []
    const OriginalAudio = window.Audio
    window.Audio = class extends OriginalAudio {
      constructor() {
        super(); runtime.__media.push(this)
        this.addEventListener('loadedmetadata', () => { if (this.duration > 0) runtime.__decoded += 1 })
      }
    }
    const originalPlay = HTMLMediaElement.prototype.play
    HTMLMediaElement.prototype.play = function () {
      const audio = this as HTMLAudioElement
      const handlers = [audio.onplaying, audio.onended, audio.onerror]
      runtime.__old.push(() => handlers.forEach(handler => handler?.call(audio, new Event('synthetic'))))
      if (mode === 'normal') return originalPlay.call(audio)
      if (mode === 'error') { queueMicrotask(() => audio.dispatchEvent(new Event('error'))); return Promise.resolve() }
      if (mode === 'reject' || mode === 'blocked') return Promise.reject(new DOMException('synthetic', mode === 'blocked' ? 'NotAllowedError' : 'NotSupportedError'))
      audio.dispatchEvent(new Event('playing'))
      if (mode === 'pending' || mode === 'pending-reject') return new Promise<void>((resolve, reject) => runtime.__settle.push(mode === 'pending' ? resolve : () => reject(new DOMException('synthetic', 'NotSupportedError'))))
      return Promise.resolve()
    }
    const setItem = Storage.prototype.setItem
    Storage.prototype.setItem = function (...args) { runtime.__writes += 1; return setItem.apply(this, args) }
  }, mode)
}
async function media(page: Page) {
  return page.evaluate(() => (window as unknown as Runtime).__media.map(audio => ({ src: audio.getAttribute('src'), paused: audio.paused, handlers: [audio.onplaying, audio.onended, audio.onerror].every(handler => handler === null), preload: audio.preload })))
}
async function setup(page: Page) {
  const state = { calls: 0, policy: { enabled: true, maxUploadBytes: 67108864, maxDurationSeconds: 7200 }, policyStatus: 200, status: 200, response: undefined as unknown, hold: false, held: [] as Route[], rows: [row('first'), row('second'), row('empty', false)], uploadStatus: 200 }
  await mockAntiforgery(page)
  await page.route('**/admin/auth/session', route => json(route, { username: 'preview-admin', roles: ['admin'] }))
  await page.route('**/admin/auth/config', route => json(route, { mode: 'hosted', loginUrl: '/hosted-login' }))
  await page.route('**/admin/auth/logout', route => json(route, {}))
  await page.route('**/admin/system/version', route => json(route, { version: 'unknown', channel: 'development', revision: null }))
  await page.route('**/admin/vocabulary-books/*/content?*', route => json(route, { book: { id: 'book', bookName: 'Disabled textbook', status: false }, items: [], totalCount: 0, totalPage: 0 }))
  await page.route('**/admin/vocabulary-books/*/units', route => json(route, { units: [{ id: 'unit', number: 1, title: 'Empty unit', meaningCount: 0 }, { id: 'other', number: 2, title: 'Other', meaningCount: 0 }] }))
  await page.route(/\/audio-tracks\?.*$/, route => json(route, { items: state.rows, totalCount: 40, totalPage: 2 }))
  await page.route('**/audio-tracks/upload-policy', route => json(route, state.policy, state.policyStatus))
  await page.route('**/audio-tracks/*/playback', async route => {
    state.calls += 1
    if (state.hold) { state.held.push(route); await new Promise<void>(() => {}); return }
    const id = new URL(route.request().url()).pathname.split('/').at(-2)!
    return json(route, state.response === undefined ? link(id) : state.response, state.status)
  })
  await page.route('**/fixture/tone.mp3', route => route.fulfill({ contentType: 'audio/mpeg', body: tone }))
  await page.route('**/audio-tracks/*/content', route => json(route, { trackId: 'first', version: row('first').version, durationSeconds: .57 }, state.uploadStatus))
  return state
}
async function open(page: Page) {
  await page.goto('/#/books/book/audio')
  await expect(page.getByText('音频上传已开启', { exact: false })).toBeVisible()
  await expect(page.getByText('正在读取录音目录…')).toHaveCount(0)
}
const play = (page: Page, id = 'first') => page.getByRole('button', { name: `试听录音 Recording ${id}`, exact: true })
const trackRow = (page: Page, id = 'first') => page.getByRole('row').filter({ has: page.getByRole('cell', { name: `Recording ${id}`, exact: true }) })
const feedback = (page: Page) => page.locator('.preview-feedback')

test('click-only unavailable tracks in disabled empty books decode real MP3 and release at ended', async ({ page }) => {
  await instrument(page, 'normal'); const state = await setup(page); await open(page)
  expect(state.calls).toBe(0)
  await expect(play(page, 'empty')).toBeDisabled()
  await play(page).focus(); await page.keyboard.press('Enter')
  await expect.poll(() => page.evaluate(() => (window as unknown as Runtime).__decoded)).toBe(1)
  await expect.poll(async () => (await media(page)).every(item => item.src === null && item.paused && item.handlers)).toBe(true)
  expect(state.calls).toBe(1)
  expect(await page.evaluate(() => [(window as unknown as Runtime).__writes, localStorage.length, sessionStorage.length])).toEqual([0, 0, 0])
})

test('one page player switches, stops while signing, and never preloads', async ({ page }) => {
  await instrument(page); const state = await setup(page); await open(page)
  await play(page).click(); await expect(feedback(page)).toContainText('正在播放录音')
  await play(page, 'second').click(); await expect(feedback(page)).toContainText('正在播放录音')
  expect((await media(page))[0]).toEqual({ src: null, paused: true, handlers: true, preload: 'none' })
  await page.getByRole('button', { name: '停止录音 Recording second', exact: true }).click()
  state.hold = true
  await play(page).click(); await expect.poll(() => state.held.length).toBe(1)
  await page.getByRole('button', { name: '停止录音 Recording first', exact: true }).click()
  await json(state.held[0]!, link()).catch(() => {})
  await expect(feedback(page)).toHaveText('')
  expect((await media(page)).every(item => item.src === null && item.handlers)).toBe(true)
})

test('real Chromium switching pauses and removes the first MP3 before playing the second', async ({ page }) => {
  await instrument(page, 'normal'); await setup(page); await open(page)
  await play(page).click(); await expect(feedback(page)).toContainText('正在播放录音')
  await play(page, 'second').click(); await expect(feedback(page)).toContainText('正在播放录音')
  const current = await media(page)
  expect(current[0]).toEqual({ src: null, paused: true, handlers: true, preload: 'none' })
  expect(current.filter(item => !item.paused && item.src).length).toBe(1)
  await page.getByRole('button', { name: '停止录音 Recording second', exact: true }).click()
  expect((await media(page)).every(item => item.paused && item.src === null && item.handlers)).toBe(true)
})

for (const mode of ['expired', 'error', 'reject', 'blocked']) test(`${mode} renewal budget and explicit second click`, async ({ page }) => {
  await instrument(page, mode === 'expired' ? 'playing' : mode); const state = await setup(page)
  if (mode === 'expired') state.response = link('first', new Date(Date.now() - 1000).toISOString())
  await open(page); await play(page).click()
  await expect(feedback(page)).toContainText(mode === 'blocked' ? '浏览器阻止' : mode === 'expired' ? '已过期' : '播放失败')
  expect(state.calls).toBe(mode === 'blocked' ? 1 : 2)
  await page.waitForTimeout(100); expect(state.calls).toBe(mode === 'blocked' ? 1 : 2)
  await play(page).click()
  await expect.poll(() => state.calls).toBe(mode === 'blocked' ? 2 : 4)
})

test('first expired signature renews once and real decoding failure can recover once', async ({ page }) => {
  await instrument(page, 'normal'); await setup(page)
  let calls = 0
  await page.route('**/audio-tracks/*/playback', route => json(route, link('first', new Date(++calls === 1 ? Date.now() - 1 : Date.now() + 60000).toISOString())))
  await open(page); await play(page).click()
  await expect.poll(() => page.evaluate(() => (window as unknown as Runtime).__decoded)).toBe(1)
  expect(calls).toBe(2)
  await expect.poll(async () => (await media(page)).every(item => item.src === null)).toBe(true)
  calls = 0
  await page.route('**/fixture/broken.mp3', route => route.fulfill({ contentType: 'audio/mpeg', body: 'invalid bytes' }))
  await page.route('**/audio-tracks/*/playback', route => json(route, link('first', undefined, ++calls === 1 ? 'http://127.0.0.1:4173/fixture/broken.mp3' : undefined)))
  await play(page).click()
  await expect.poll(() => page.evaluate(() => (window as unknown as Runtime).__decoded)).toBe(2)
  expect(calls).toBe(2)
})

for (const [index, value] of [null, {}, { ...link(), trackId: 'wrong' }, { ...link(), url: '/relative.mp3' }, { ...link(), url: 'javascript:alert(1)' }, { ...link(), url: 'data:audio/mpeg,bytes' }, { ...link(), url: 'not a URL' }, { ...link(), expiresAt: 'bad' }, { ...link(), expiresAt: '2099-01-01T12:00:00+08:00' }, { ...link(), locale: 'en-GB' }].entries()) test(`malformed three-field playback case ${index} never creates media`, async ({ page }) => {
  await instrument(page); const state = await setup(page); state.response = value
  await open(page); await play(page).click(); await expect(feedback(page)).toContainText('暂时无法获取试听')
  expect(state.calls).toBe(1); expect(await media(page)).toEqual([])
})

for (const status of [404, 503, 401, 403]) test(`current ${status} preserves error/authorization handling`, async ({ page }) => {
  await instrument(page); const state = await setup(page); state.status = status
  await open(page); await play(page).click()
  if (status === 401 || status === 403) await expect(page).toHaveURL(status === 401 ? /login/ : /forbidden/)
  else {
    await expect(feedback(page)).toContainText(status === 404 ? '已不存在' : '暂时无法获取试听')
    state.status = 200; await play(page).click(); await expect(feedback(page)).toContainText('正在播放录音')
  }
})

test('unknown or disabled policy forbids signing and explicit retry restores it', async ({ page }) => {
  await instrument(page); const state = await setup(page); state.policyStatus = 503
  await page.goto('/#/books/book/audio'); await expect(page.getByText('上传限制读取失败', { exact: false })).toBeVisible()
  await expect(play(page)).toBeDisabled(); expect(state.calls).toBe(0)
  state.policyStatus = 200; state.policy.enabled = false
  await page.getByRole('button', { name: '重新读取上传限制' }).click(); await expect(play(page)).toBeDisabled()
  state.policy.enabled = true; await page.getByRole('button', { name: '重新读取上传限制' }).click()
  await play(page).click(); await expect(feedback(page)).toContainText('正在播放录音')
  state.policy.enabled = false; await page.getByRole('button', { name: '重新读取上传限制' }).click()
  expect((await media(page)).every(item => item.src === null && item.handlers)).toBe(true)
})

for (const action of ['edit', 'delete', 'create', 'upload', 'content', 'refresh', 'unit', 'page', 'book', 'leave', 'logout']) test(`${action} immediately invalidates media and captured late handlers`, async ({ page }) => {
  await instrument(page, 'pending-reject'); await setup(page); await open(page)
  if (action === 'content') {
    await trackRow(page).getByRole('button', { name: '上传文件', exact: true }).click()
    await page.getByLabel('选择 MP3 文件').setInputFiles({ name: 'tone.mp3', mimeType: 'audio/mpeg', buffer: tone })
  }
  await play(page).click(); await expect(feedback(page)).toContainText('正在播放录音')
  if (['edit', 'delete', 'upload'].includes(action)) await trackRow(page).getByRole('button', { name: action === 'edit' ? '编辑' : action === 'delete' ? '移除' : '上传文件', exact: true }).click()
  if (action === 'create') await page.getByRole('button', { name: '新增录音', exact: true }).click()
  if (action === 'content') await page.getByRole('button', { name: '开始上传', exact: true }).click()
  if (action === 'refresh') await page.getByRole('button', { name: '重新读取目录', exact: true }).click()
  if (action === 'unit') { await page.locator('.unit-select').click(); await page.getByRole('option', { name: '单元 2 · Other' }).click() }
  if (action === 'page') await page.getByRole('button', { name: '下一页' }).click()
  if (action === 'book' || action === 'leave') await page.evaluate(action => { location.hash = action === 'book' ? '/books/replacement/audio' : '/login' }, action)
  if (action === 'logout') {
    await page.route('**/admin/auth/logout', async () => { await new Promise<void>(() => {}) })
    await page.getByRole('button', { name: '退出登录' }).click()
  }
  await expect.poll(async () => (await media(page)).every(item => item.src === null && item.paused && item.handlers)).toBe(true)
  await page.evaluate(() => { const runtime = window as unknown as Runtime; runtime.__old.forEach(invoke => invoke()); runtime.__settle.forEach(invoke => invoke()) })
  await expect(page.getByText('录音播放失败，请检查网络或稍后重新点击试听。', { exact: true })).toHaveCount(0)
})

for (const mode of ['pending', 'pending-reject']) test(`old ${mode} and media callbacks cannot clear later media`, async ({ page }) => {
  await instrument(page, mode); const state = await setup(page); await open(page)
  await play(page).click(); await play(page, 'second').click()
  await page.evaluate(() => { const runtime = window as unknown as Runtime; runtime.__old[0]!(); runtime.__settle[0]!() })
  await expect(page.getByRole('button', { name: '停止录音 Recording second', exact: true })).toBeVisible()
  await expect(feedback(page)).toContainText('正在播放录音')
  expect(state.calls).toBe(2)
  const items = await media(page); expect(items[0]?.src).toBeNull(); expect(items[1]?.src).toContain('/fixture/tone.mp3')
})

for (const action of ['unit', 'page', 'book', 'leave', 'logout', 'edit', 'upload']) for (const status of [200, 401, 403]) test(`late signature ${status} across ${action} cannot play or redirect`, async ({ page }) => {
  await instrument(page); const state = await setup(page); await open(page); state.hold = true
  await play(page).click(); await expect.poll(() => state.held.length).toBe(1)
  if (action === 'unit') { await page.locator('.unit-select').click(); await page.getByRole('option', { name: '单元 2 · Other' }).click() }
  if (action === 'page') await page.getByRole('button', { name: '下一页' }).click()
  if (action === 'book' || action === 'leave') await page.evaluate(action => { location.hash = action === 'book' ? '/books/replacement/audio' : '/login' }, action)
  if (action === 'edit' || action === 'upload') await trackRow(page).getByRole('button', { name: action === 'edit' ? '编辑' : '上传文件', exact: true }).click()
  if (action === 'logout') { await page.route('**/admin/auth/logout', async () => { await new Promise<void>(() => {}) }); await page.getByRole('button', { name: '退出登录' }).click() }
  const url = page.url()
  await json(state.held[0]!, status === 200 ? link() : 'Late refusal', status).catch(() => {})
  await page.waitForTimeout(100)
  expect(page.url()).toBe(url); expect(await media(page)).toEqual([])
  await expect(page.getByText('暂时无法获取试听，音频可能已关闭或存储不可用；请稍后重试。', { exact: true })).toHaveCount(0)
})

for (const status of [200, 401, 403]) test(`same-username new session ignores old playback ${status}`, async ({ page }) => {
  await instrument(page); const state = await setup(page); await open(page); state.hold = true
  await play(page).click(); await expect.poll(() => state.held.length).toBe(1)
  await page.getByRole('button', { name: '退出登录' }).click(); await expect(page).toHaveURL(/login/)
  state.hold = false
  // Hosted sign-in creates a new document; changing only a hash cannot restore
  // a session after the router has already attempted its one initial restore.
  await page.reload()
  await page.goto('/#/books/book/audio')
  await expect(play(page)).toBeEnabled(); await play(page).click()
  await expect(feedback(page)).toContainText('正在播放录音')
  await json(state.held[0]!, status === 200 ? link() : 'Late refusal', status).catch(() => {})
  await page.waitForTimeout(100)
  await expect(page).toHaveURL(/books\/book\/audio$/)
  await expect(page.locator('.session')).toContainText('preview-admin')
  await expect(feedback(page)).toContainText('正在播放录音')
  expect((await media(page)).length).toBe(1)
})

test('same-username new session owns its media despite prior play promise and handlers', async ({ page }) => {
  await instrument(page, 'pending-reject'); await setup(page); await open(page)
  await play(page).click(); await expect(feedback(page)).toContainText('正在播放录音')
  await page.getByRole('button', { name: '退出登录' }).click(); await expect(page).toHaveURL(/login/)
  expect((await media(page))[0]?.src).toBeNull()
  await page.evaluate(() => { const runtime = window as unknown as Runtime; runtime.__old[0]!(); runtime.__settle[0]!() })
  await page.reload()
  await page.goto('/#/books/book/audio')
  await expect(play(page)).toBeEnabled(); await play(page).click()
  await expect(feedback(page)).toContainText('正在播放录音')
  expect((await media(page)).length).toBe(1); expect((await media(page))[0]?.src).toContain('/fixture/tone.mp3')
})

test('canceling unknown-upload leave keeps player; accepting different track reconciles scope first', async ({ page }) => {
  await instrument(page); const state = await setup(page); state.uploadStatus = 503; await open(page)
  await trackRow(page).getByRole('button', { name: '上传文件', exact: true }).click()
  await page.getByLabel('选择 MP3 文件').setInputFiles({ name: 'tone.mp3', mimeType: 'audio/mpeg', buffer: tone })
  await page.getByRole('button', { name: '开始上传', exact: true }).click(); await expect(page.locator('.upload-feedback')).toContainText('上传结果未知')
  await play(page).click(); await expect(feedback(page)).toContainText('正在播放录音')
  await play(page, 'second').click(); await page.getByRole('button', { name: '继续核对', exact: true }).click()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  await expect(page.getByRole('button', { name: '停止录音 Recording first', exact: true })).toBeVisible()
  expect(state.calls).toBe(1)
  await play(page, 'second').click(); await page.getByRole('button', { name: '放弃核对并离开', exact: true }).click()
  await expect(page.getByRole('button', { name: '停止录音 Recording second', exact: true })).toBeVisible()
  expect((await media(page))[0]?.src).toBeNull(); expect(state.calls).toBe(2)
})

for (const width of [1440, 768]) test(`preview keyboard, focus, live feedback, axe and overflow at ${width}`, async ({ page }) => {
  await instrument(page); await setup(page); await page.setViewportSize({ width, height: 1000 }); await open(page)
  await play(page).focus(); await page.keyboard.press('Enter')
  const stop = page.getByRole('button', { name: '停止录音 Recording first', exact: true })
  await expect(stop).toBeFocused(); await expect(feedback(page)).toHaveAttribute('aria-live', 'polite')
  await page.keyboard.press('Enter'); await expect(play(page)).toBeFocused()
  await page.waitForTimeout(400)
  expect((await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze()).violations).toEqual([])
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  await page.screenshot({ path: `test-results/textbook-preview-${width}.png`, fullPage: true })
})
