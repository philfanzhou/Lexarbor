import { test, expect, type Page, type Route } from '@playwright/test'
import AxeBuilder from '@axe-core/playwright'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { mockAntiforgery } from './support/antiforgery'

const tone = readFileSync(fileURLToPath(new URL('./fixtures/tone.mp3', import.meta.url)))
const book = { id: 'book', bookName: 'Disabled audio book', status: false }
const word = { id: 'word', word: 'apple', phoneticUk: null, phoneticUs: null, books: [book], meanings: [
  { id: 'meaning', vocabularyId: 'word', bookId: 'book', meaning: '特殊释义', partOfSpeech: 'n.' }
] }
const orphan = { ...word, id: 'orphan', word: 'orphan', books: [], meanings: [] }
function json(route: Route, data: unknown, status = 200) {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify({ success: status === 200, ...(status === 200 ? { data } : { message: 'Audio unavailable.' }) }) })
}
function metadata(wordId: string, meaningId: string | null, enabled = true) {
  return { enabled, items: (['en-GB', 'en-US'] as const).map(locale => ({
    wordId, meaningId, locale, targetType: meaningId ? 'meaning' : 'default',
    status: locale === 'en-US' ? 'missing' : meaningId ? 'stale' : 'ready',
    source: locale === 'en-US' ? null : meaningId ? 'meaning' : 'default', sourceRevision: 'current',
    input: { word: wordId, phonetic: null, partOfSpeech: null, meaning: null },
    ownBinding: locale === 'en-US' ? null : { assetId: 'asset', sourceRevision: 'old' },
    effectiveBinding: locale === 'en-US' ? null : { assetId: 'asset', sourceRevision: 'old' }
  })) }
}
function link(expiry = Date.now() + 60000, url = '/fixture/tone.mp3') {
  return { locale: 'en-GB', status: 'ready', source: 'default', assetId: 'asset', sourceRevision: 'current', url, expiresAt: new Date(expiry).toISOString() }
}
async function setup(page: Page) {
  await mockAntiforgery(page)
  await page.route('**/admin/auth/session', route => json(route, { username: 'audio-admin', roles: ['admin'] }))
  await page.route('**/admin/auth/config', route => json(route, { mode: 'hosted', loginUrl: '/hosted-login' }))
  await page.route('**/admin/system/version', route => json(route, { version: 'unknown', channel: 'development', revision: null }))
  await page.route('**/admin/vocabulary-books/categories', route => json(route, { items: [] }))
  await page.route('**/admin/vocabulary-books/education-levels', route => json(route, { items: [] }))
  await page.route(/\/admin\/vocabulary-books(\?.*)?$/, route => json(route, { items: [book], totalCount: 1, totalPage: 1 }))
  await page.route(/\/admin\/vocabulary(\?.*)?$/, route => json(route, { items: [word, orphan], totalCount: 2, totalPage: 1 }))
  await page.route(/\/admin\/vocabulary\/[^/]+$/, route => json(route, route.request().url().endsWith('/orphan') ? orphan : word))
  await page.route(/\/admin\/vocabulary\/[^/]+\/audio(\?.*)?$/, route => {
    const url = new URL(route.request().url())
    return json(route, metadata(url.pathname.split('/')[3], url.searchParams.get('meaningId')))
  })
  await page.route('**/playback*', route => json(route, link()))
  await page.route('**/fixture/tone.mp3', route => route.fulfill({ contentType: 'audio/mpeg', body: tone }))
}
async function open(page: Page, name = 'apple') {
  await page.goto('/#/vocabulary')
  await page.locator('.el-table__row', { hasText: name }).getByRole('button', { name: '详情' }).click()
  await expect(page.getByRole('button', { name: '试听单词默认英式音频' })).toBeEnabled()
}
const playButton = (page: Page) => page.getByRole('button', { name: '试听单词默认英式音频' })
async function instrument(page: Page, mode: 'normal' | 'error' | 'reject' | 'blocked' = 'normal') {
  await page.addInitScript(mode => {
    const original = window.Audio
    const originalPlay = HTMLMediaElement.prototype.play
    const audios: HTMLAudioElement[] = []
    Object.defineProperty(window, '__audios', { value: audios })
    window.Audio = class extends original {
      constructor() { super(); audios.push(this) }
    }
    if (mode !== 'normal') HTMLMediaElement.prototype.play = function () {
      if (mode === 'error') { queueMicrotask(() => this.dispatchEvent(new Event('error'))); return Promise.resolve() }
      return Promise.reject(new DOMException('synthetic', mode === 'blocked' ? 'NotAllowedError' : 'NotSupportedError'))
    }
    else HTMLMediaElement.prototype.play = originalPlay
  }, mode)
}
async function mediaState(page: Page) {
  return page.evaluate(() => {
    const audios = (window as unknown as { __audios: HTMLAudioElement[] }).__audios
    return audios.map(audio => ({ paused: audio.paused, src: audio.getAttribute('src'), duration: audio.duration }))
  })
}

test.beforeEach(async ({ page }) => { await setup(page) })

test('真实 MP3 默认/覆盖状态、禁用教材、键盘播放及无持久凭据', async ({ page }) => {
  await instrument(page)
  await open(page)
  await expect(page.getByRole('region', { name: '释义 特殊释义 音频' })).toContainText('需更新（仍可试听）')
  await expect(page.getByRole('region', { name: '释义 特殊释义 音频' })).toContainText('释义覆盖')
  await expect(page.getByRole('button', { name: '试听单词默认美式音频' })).toBeDisabled()
  await playButton(page).focus()
  await page.keyboard.press('Enter')
  await expect(page.getByText('正在播放', { exact: true })).toBeVisible()
  await expect.poll(async () => (await mediaState(page))[0]?.duration).toBeGreaterThan(0)
  await expect(page.getByText('正在播放', { exact: true })).not.toBeVisible()
  expect(await mediaState(page)).toEqual([expect.objectContaining({ paused: true, src: null })])
  await page.getByRole('button', { name: '试听释义 特殊释义 英式音频' }).click()
  await expect(page.getByText('正在播放', { exact: true })).toBeVisible()
  expect(await page.evaluate(() => [localStorage.length, sessionStorage.length])).toEqual([0, 0])
})

test('无教材词及同口音默认回落仍能试听', async ({ page }) => {
  await page.route(/\/audio\?meaningId=meaning$/, route => {
    const data = metadata('word', 'meaning')
    data.items[0].source = 'default'
    data.items[0].ownBinding = null
    data.items[0].status = 'ready'
    return json(route, data)
  })
  await open(page)
  await expect(page.getByRole('region', { name: '释义 特殊释义 音频' })).toContainText('使用单词默认')
  await page.keyboard.press('Escape')
  await open(page, 'orphan')
  await playButton(page).click()
  await expect(page.getByText('正在播放', { exact: true })).toBeVisible()
})

for (const mode of ['expired', 'error', 'reject'] as const) test(`${mode} 首次失败只重取一次`, async ({ page }) => {
  await instrument(page, mode === 'expired' ? 'normal' : mode)
  let requests = 0
  await page.route('**/playback*', route => { requests += 1; return json(route, link(mode === 'expired' ? Date.now() - 1000 : undefined)) })
  await open(page)
  await playButton(page).click()
  await expect(page.getByRole('alert')).toContainText(mode === 'expired' ? '已过期' : '播放失败')
  expect(requests).toBe(2)
  await page.waitForTimeout(150)
  expect(requests).toBe(2)
})

test('第一次过期后刷新合法链接成功', async ({ page }) => {
  let requests = 0
  await page.route('**/playback*', route => json(route, link(++requests === 1 ? Date.now() - 1 : Date.now() + 60000)))
  await open(page)
  await playButton(page).click()
  await expect(page.getByText('正在播放', { exact: true })).toBeVisible()
  expect(requests).toBe(2)
})

test('浏览器拒绝自动播放提示重新点击', async ({ page }) => {
  await instrument(page, 'blocked')
  let requests = 0
  await page.route('**/playback*', route => { requests += 1; return json(route, link()) })
  await open(page)
  await playButton(page).click()
  await expect(page.getByRole('alert')).toContainText('浏览器阻止了播放')
  expect(requests).toBe(1)
})

for (const response of ['missing', 'storage-error', 'old-backend', 'disabled', 'malformed'] as const) test(`${response} 不影响词汇编辑`, async ({ page }) => {
  if (response === 'missing') await page.route('**/playback*', route => json(route, { ...link(), status: 'missing', url: null, expiresAt: null }))
  if (response === 'storage-error') await page.route('**/playback*', route => json(route, null, 503))
  if (response === 'old-backend') await page.route(/\/audio(\?.*)?$/, route => json(route, null, 404))
  if (response === 'disabled') await page.route(/\/audio(\?.*)?$/, route => json(route, metadata('word', new URL(route.request().url()).searchParams.get('meaningId'), false)))
  if (response === 'malformed') await page.route(/\/audio(\?.*)?$/, route => json(route, null))
  await page.goto('/#/vocabulary')
  await page.locator('.el-table__row', { hasText: 'apple' }).getByRole('button', { name: '详情' }).click()
  await expect(page.getByRole('button', { name: '编辑拼写与音标' })).toBeVisible()
  if (response === 'old-backend' || response === 'malformed') await expect(page.getByText('音频暂不可用', { exact: false }).first()).toBeVisible()
  else if (response === 'disabled') await expect(playButton(page)).toBeDisabled()
  else {
    await playButton(page).click()
    await expect(page.getByRole('alert')).toContainText(response === 'missing' ? '暂无音频' : '暂时无法获取音频')
  }
})

for (const action of ['close', 'switch', 'unmount', 'logout'] as const) test(`正在播放时 ${action} 释放媒体`, async ({ page }) => {
  await instrument(page)
  await open(page)
  await playButton(page).click()
  await expect(page.getByText('正在播放', { exact: true })).toBeVisible()
  if (action === 'close' || action === 'switch') {
    await page.keyboard.press('Escape')
    if (action === 'switch') {
      await page.locator('.el-table__row', { hasText: 'orphan' }).getByRole('button', { name: '详情' }).click()
      await expect(playButton(page)).toBeEnabled()
    }
  } else if (action === 'unmount') await page.evaluate(() => { window.location.hash = '/books' })
  else {
    let release!: () => void
    const wait = new Promise<void>(resolve => { release = resolve })
    await page.route('**/admin/auth/logout', async route => { await wait; await json(route, {}) })
    await page.keyboard.press('Escape')
    // Reopen, start playback and use the visible header after dismissing modal focus.
    await page.locator('.el-table__row', { hasText: 'apple' }).getByRole('button', { name: '详情' }).click()
    await playButton(page).click()
    await page.getByRole('button', { name: '退出登录' }).evaluate((button: HTMLButtonElement) => button.click())
    await expect.poll(async () => (await mediaState(page)).every(audio => audio.paused && audio.src === null)).toBe(true)
    release()
    return
  }
  await expect.poll(async () => (await mediaState(page)).every(audio => audio.paused && audio.src === null)).toBe(true)
})

for (const kind of ['metadata', 'metadata-401', 'sign', 'late-401'] as const) test(`迟到 ${kind} 不修改重开后的新词`, async ({ page }) => {
  let release!: () => void
  const wait = new Promise<void>(resolve => { release = resolve })
  let entered!: () => void
  const started = new Promise<void>(resolve => { entered = resolve })
  const pattern = kind.startsWith('metadata') ? /\/word\/audio$/ : '**/word/audio/en-GB/playback'
  await page.route(pattern, async route => {
    entered()
    await wait
    await (kind.startsWith('metadata') ? json(route, metadata('word', null), kind === 'metadata-401' ? 401 : 200) : json(route, link(), kind === 'late-401' ? 401 : 200)).catch(() => {})
  })
  await page.goto('/#/vocabulary')
  await page.locator('.el-table__row', { hasText: 'apple' }).getByRole('button', { name: '详情' }).click()
  if (!kind.startsWith('metadata')) { await playButton(page).click() }
  await started
  await page.keyboard.press('Escape')
  await page.locator('.el-table__row', { hasText: 'orphan' }).getByRole('button', { name: '详情' }).click()
  await expect(playButton(page)).toBeEnabled()
  release()
  await page.waitForTimeout(150)
  await expect(page).toHaveURL(/#\/vocabulary$/)
  await expect(page.getByRole('heading', { name: 'orphan', exact: true })).toBeVisible()
  await expect(page.getByText('正在播放', { exact: true })).not.toBeVisible()
})

test('768px 音频状态与按钮可访问，无横向溢出', async ({ page }) => {
  await page.setViewportSize({ width: 768, height: 900 })
  await open(page)
  await page.waitForTimeout(400) // Element Plus drawer transition must finish before contrast sampling.
  const results = await new AxeBuilder({ page }).include('.word-detail-drawer').withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze()
  expect(results.violations).toEqual([])
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
})

for (const entry of ['book-words', 'phrases'] as const) test(`${entry} 入口共享默认与释义试听`, async ({ page }) => {
  await page.route(/\/admin\/vocabulary-books\/book\/units$/, route => json(route, { units: [] }))
  await page.route(/\/admin\/vocabulary-books\/book\/content(\?.*)?$/, route => json(route, {
    book, wordCount: 1, meaningCount: 1, items: [word], totalCount: 1, totalPage: 1
  }))
  await page.route(/\/admin\/vocabulary-books\/book\/phrase-positions/, route => json(route, {
    items: [{ bookId: 'book', unitId: 'unit', meaningId: 'meaning', section: null, entryKind: 'phrase', number: 1, title: null, wordId: 'word', word: 'apple', phoneticUk: null, phoneticUs: null, partOfSpeech: null, meaning: '特殊释义', example: null }], totalCount: 1, totalPage: 1
  }))
  if (entry === 'book-words') await page.goto('/#/books/book/words')
  else {
    await page.goto('/#/phrases')
    await page.locator('.phrase-positions__book').click()
    await page.getByRole('option', { name: 'Disabled audio book（停用）', exact: true }).click()
  }
  await page.getByRole('button', { name: '详情', exact: true }).click()
  await expect(playButton(page)).toBeEnabled()
  await expect(page.getByRole('button', { name: '试听释义 特殊释义 英式音频' })).toBeEnabled()
})

test('待签名时主动停止、同词关闭重开、晚到链接均不播放', async ({ page }) => {
  await instrument(page)
  let release!: () => void
  let entered!: () => void
  const wait = new Promise<void>(resolve => { release = resolve })
  const started = new Promise<void>(resolve => { entered = resolve })
  let requests = 0
  await page.route('**/word/audio/en-GB/playback', async route => {
    requests += 1
    if (requests === 1) { entered(); await wait }
    await json(route, link()).catch(() => {})
  })
  await open(page)
  await playButton(page).click()
  await started
  await page.getByRole('button', { name: '停止单词默认英式音频' }).click()
  await page.keyboard.press('Escape')
  await page.locator('.el-table__row', { hasText: 'apple' }).getByRole('button', { name: '详情' }).click()
  release()
  await expect(playButton(page)).toBeEnabled()
  expect(await mediaState(page)).toEqual([])
  await playButton(page).click()
  await expect(page.getByText('正在播放', { exact: true })).toBeVisible()
  expect(requests).toBe(2)
})

test('当前音频 401 保留共享登录跳转', async ({ page }) => {
  await page.route('**/playback*', route => json(route, null, 401))
  await open(page)
  await playButton(page).click()
  await expect(page).toHaveURL(/#\/login/)
})

test('真实解码失败重取一次恢复，不同时播放两份音频', async ({ page }) => {
  await instrument(page)
  let requests = 0
  await page.route('**/playback*', route => json(route, link(undefined, ++requests === 1 ? '/fixture/broken.mp3' : '/fixture/tone.mp3')))
  await page.route('**/fixture/broken.mp3', route => route.fulfill({ contentType: 'audio/mpeg', body: 'invalid audio bytes' }))
  await open(page)
  await playButton(page).click()
  await expect(page.getByText('正在播放', { exact: true })).toBeVisible()
  expect(requests).toBe(2)
  await page.getByRole('button', { name: '试听释义 特殊释义 英式音频' }).click()
  await expect(page.getByRole('region', { name: '释义 特殊释义 音频' })).toContainText('正在播放')
  const audios = await mediaState(page)
  expect(audios.slice(0, -1).every(audio => audio.paused && audio.src === null)).toBe(true)
  expect(audios.at(-1)?.paused).toBe(false)
})
