import { expect, test, type Page, type Route } from '@playwright/test'
import AxeBuilder from '@axe-core/playwright'
import { mockAntiforgery } from './support/antiforgery'

const book = { id: 'book', bookName: 'Empty disabled textbook', displayOrder: 0, status: false }
const unit = { id: 'unit', bookId: 'book', number: 2, title: 'Listening', meaningCount: 0 }
const version = (n: number) => `00000000-0000-0000-0000-${String(n).padStart(12, '0')}`
const track = (n: number, hasFile = false) => ({ id: `track-${n}`, bookId: 'book', unitId: 'unit', title: `Recording ${n}`, displayOrder: n, hasFile, durationSeconds: hasFile ? 1.25 : null, isAvailable: false, version: version(n) })
type Track = ReturnType<typeof track>
function json(route: Route, data: unknown, status = 200) {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(status < 400 ? { success: true, data } : { success: false, message: data }) })
}
async function setup(page: Page, initial: Track[] = []) {
  const state = {
    rows: initial,
    units: [unit],
    requests: [] as Array<{ method: string; url: URL; body: Record<string, unknown> | null; csrf: string | undefined }>,
    failure: 0,
    lose: false,
    readFailure: 0,
    postReadFailure: false,
    hold: null as ((route: Route) => Promise<void>) | null
  }
  await mockAntiforgery(page)
  await page.route('**/admin/auth/session', route => json(route, { username: 'ci-admin', roles: ['admin'] }))
  await page.route('**/admin/auth/logout', route => json(route, {}))
  await page.route('**/admin/system/version', route => json(route, { version: '1.2.3', channel: 'release', revision: null }))
  await page.route('**/admin/vocabulary-books/categories', route => json(route, { items: [] }))
  await page.route('**/admin/vocabulary-books/education-levels', route => json(route, { items: [] }))
  await page.route(/\/admin\/vocabulary-books(?:\?.*)?$/, route => json(route, { items: [book], totalCount: 1, totalPage: 1 }))
  await page.route(/\/admin\/vocabulary-books\/[^/]+\/content(?:\?.*)?$/, route => json(route, { book, wordCount: 0, meaningCount: 0, items: [], totalCount: 0, totalPage: 0 }))
  await page.route(/\/admin\/vocabulary-books\/[^/]+\/units$/, route => json(route, { units: state.units }))
  await page.route(/\/admin\/vocabulary-books\/[^/]+\/units\/[^/]+\/audio-tracks(?:\/[^/?]+)?(?:\?.*)?$/, async route => {
    const req = route.request()
    const url = new URL(req.url())
    const method = req.method()
    const body = req.postDataJSON() as Record<string, unknown> | null
    state.requests.push({ method, url, body, csrf: req.headers()['x-signacore-csrf'] })
    if (state.hold) return state.hold(route)
    if (method === 'GET') {
      if (state.readFailure) return json(route, 'Read unavailable', state.readFailure)
      const p = Number(url.searchParams.get('page'))
      const size = Number(url.searchParams.get('size'))
      const rows = [...state.rows].sort((a, b) => a.displayOrder - b.displayOrder || a.id.localeCompare(b.id))
      return json(route, { items: rows.slice((p - 1) * size, p * size), totalCount: rows.length, totalPage: Math.ceil(rows.length / size) })
    }
    if (state.failure) return json(route, 'Controlled refusal', state.failure)
    const id = url.pathname.split('/').at(-1)
    if (method === 'POST') {
      const row = { ...track(100 + state.rows.length), title: String(body?.title), displayOrder: Number(body?.displayOrder) }
      state.rows.push(row)
      if (state.lose) return route.abort('failed')
      if (state.postReadFailure) state.readFailure = 503
      return json(route, row)
    }
    const row = state.rows.find(r => r.id === id)
    if (!row) return json(route, 'Not found', 404)
    const expected = method === 'DELETE' ? url.searchParams.get('expectedVersion') : body?.expectedVersion
    if (expected !== row.version || (body?.isAvailable && !row.hasFile)) return json(route, 'Conflict', 409)
    if (method === 'DELETE') state.rows = state.rows.filter(r => r !== row)
    else Object.assign(row, { title: body?.title, displayOrder: body?.displayOrder, isAvailable: body?.isAvailable, version: version(999) })
    if (state.lose) return route.abort('failed')
    return json(route, method === 'DELETE' ? { success: true } : row)
  })
  return state
}
async function open(page: Page) {
  await page.goto('/#/books/book/audio')
  await expect(page.getByRole('button', { name: '新增录音', exact: true })).toBeEnabled()
  await expect(page.getByText('正在读取录音目录…')).toHaveCount(0)
}
const writes = (state: Awaited<ReturnType<typeof setup>>) => state.requests.filter(r => r.method !== 'GET')
async function fill(page: Page, title = 'Listening exercise', order = '0') {
  await page.getByRole('textbox', { name: '录音标题' }).fill(title)
  await page.getByRole('spinbutton', { name: '录音顺序' }).fill(order)
}
async function create(page: Page, title: string) {
  await page.getByRole('button', { name: '新增录音', exact: true }).click()
  await fill(page, title)
  await page.getByRole('button', { name: '保存', exact: true }).click()
  await expect(page.getByRole('dialog')).toHaveCount(0)
}

test('book and word-list entries manage a disabled empty book; no unit gives an actionable way back', async ({ page }) => {
  const state = await setup(page)
  await page.goto('/#/books')
  await page.getByRole('button', { name: '教材录音', exact: true }).click()
  await expect(page).toHaveURL(/books\/book\/audio$/)
  await expect(page.getByText('禁用教材（仍可管理录音）')).toBeVisible()
  await expect(page.getByText('本单元暂无录音，可新增录音目录')).toBeVisible()
  await page.getByRole('button', { name: '教材词表', exact: true }).click()
  await page.getByRole('button', { name: '教材录音', exact: true }).click()
  await expect(page).toHaveURL(/books\/book\/audio$/)
  state.units = []
  await page.reload()
  await expect(page.getByText('请返回教材列表，在该教材的「单元」入口先建单元。', { exact: false })).toBeVisible()
  await expect(page.getByRole('button', { name: '新增录音', exact: true })).toHaveCount(0)
  expect(writes(state)).toHaveLength(0)
})

test('two creates, full conditional editing/order/availability, delete cancellation and last-page fallback use actual server reads', async ({ page }) => {
  const state = await setup(page, [track(1, true)])
  await open(page)
  await create(page, '  First  ')
  await create(page, 'Second')
  expect(writes(state).slice(0, 2).map(r => r.body)).toEqual([{ title: 'First', displayOrder: 0 }, { title: 'Second', displayOrder: 0 }])
  const row = page.getByRole('row').filter({ has: page.getByRole('cell', { name: 'Recording 1', exact: true }) })
  await row.getByRole('button', { name: '编辑', exact: true }).click()
  await fill(page, 'Edited recording', '2147483647')
  await page.getByRole('checkbox', { name: '设为可用' }).press('Space')
  await page.getByRole('button', { name: '保存', exact: true }).click()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  expect(writes(state).at(-1)?.body).toEqual({ title: 'Edited recording', displayOrder: 2147483647, isAvailable: true, expectedVersion: version(1) })
  await page.getByRole('row').filter({ has: page.getByRole('cell', { name: 'First', exact: true }) }).getByRole('button', { name: '编辑', exact: true }).click()
  await expect(page.getByRole('checkbox', { name: '设为可用' })).toBeDisabled()
  await page.getByRole('button', { name: '取消', exact: true }).click()
  await page.getByRole('row').filter({ has: page.getByRole('cell', { name: 'Edited recording', exact: true }) }).getByRole('button', { name: '移除', exact: true }).click()
  await expect(page.getByRole('dialog')).toContainText('历史文件和操作回执会保留')
  await page.getByRole('button', { name: '取消', exact: true }).click()
  expect(writes(state)).toHaveLength(3)
  state.rows = Array.from({ length: 11 }, (_, i) => track(i + 1))
  await page.locator('.page-size').click()
  await page.getByRole('option', { name: '10 条/页', exact: true }).click()
  await page.getByRole('button', { name: '下一页' }).click()
  await expect(page.getByRole('row').filter({ has: page.getByRole('cell', { name: 'Recording 11', exact: true }) })).toBeVisible()
  await page.getByRole('row').filter({ has: page.getByRole('cell', { name: 'Recording 11', exact: true }) }).getByRole('button', { name: '移除', exact: true }).click()
  await page.getByRole('button', { name: '确认移除', exact: true }).click()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  await expect(page.getByRole('row').filter({ has: page.getByRole('cell', { name: 'Recording 1', exact: true }) }).first()).toBeVisible()
  const deleted = writes(state).at(-1)!
  expect(deleted.method).toBe('DELETE')
  expect(deleted.url.searchParams.getAll('expectedVersion')).toEqual([version(11)])
  expect(deleted.csrf).toBe('synthetic-antiforgery-token')
  expect(state.requests.at(-1)?.url.searchParams.get('page')).toBe('1')
})

test('UTF16 title boundaries and order validation prevent invalid writes and duplicate synchronous saves', async ({ page }) => {
  const state = await setup(page)
  await open(page)
  await page.getByRole('button', { name: '新增录音', exact: true }).click()
  await fill(page, '😀'.repeat(101))
  await expect(page.getByRole('button', { name: '保存', exact: true })).toBeDisabled()
  await fill(page, '   ')
  await expect(page.getByRole('button', { name: '保存', exact: true })).toBeDisabled()
  await fill(page, '😀'.repeat(100), '2147483647')
  await page.getByRole('button', { name: '保存', exact: true }).evaluate((button: HTMLButtonElement) => { button.click(); button.click() })
  await expect(page.getByRole('dialog')).toHaveCount(0)
  expect(writes(state)).toHaveLength(1)
  expect(writes(state)[0]?.body?.title).toHaveLength(200)
})

test('409 keeps the draft and old condition until an explicit comparison; enabling can still conflict', async ({ page }) => {
  const state = await setup(page, [track(1, true)])
  await open(page)
  await page.getByRole('button', { name: '编辑', exact: true }).click()
  await fill(page, 'Draft title', '9')
  state.rows[0] = { ...track(1, true), title: 'Concurrent title', version: version(2) }
  await page.getByRole('button', { name: '保存', exact: true }).click()
  await expect(page.getByRole('alert').filter({ hasText: '版本或文件状态冲突' })).toBeVisible()
  await expect(page.getByRole('textbox', { name: '录音标题' })).toHaveValue('Draft title')
  await expect(page.getByRole('dialog')).toContainText(`提交条件版本：${version(1)}`)
  await expect(page.getByRole('button', { name: '保存', exact: true })).toBeDisabled()
  await page.getByRole('button', { name: '保留草稿，采用当前版本' }).click()
  await page.getByRole('button', { name: '保存', exact: true }).click()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  expect(writes(state).map(r => r.body?.expectedVersion)).toEqual([version(1), version(2)])
})

for (const method of ['POST', 'PUT', 'DELETE']) test(`${method} response loss remains unknown without replay; confirmed writes distinguish failed read-back`, async ({ page }) => {
  const state = await setup(page, method === 'POST' ? [] : [track(1, true)])
  await open(page)
  state.lose = true
  if (method === 'POST') {
    await page.getByRole('button', { name: '新增录音', exact: true }).click()
    await fill(page)
  } else await page.getByRole('button', { name: method === 'PUT' ? '编辑' : '移除', exact: true }).click()
  await page.getByRole('button', { name: method === 'DELETE' ? '确认移除' : '保存', exact: true }).click()
  await expect(page.getByRole('dialog')).toContainText('结果未知')
  await expect.poll(() => state.requests.filter(r => r.method === 'GET').length).toBe(2)
  expect(writes(state)).toHaveLength(1)
  await expect(page.getByRole('button', { name: method === 'DELETE' ? '确认移除' : '保存', exact: true })).toBeDisabled()
  await page.getByRole('button', { name: '取消', exact: true }).click()
  state.lose = false
  state.postReadFailure = true
  await create(page, 'Confirmed')
  await expect(page.getByRole('status').filter({ hasText: '写操作已确认，但当前目录读取失败' })).toBeVisible()
  expect(writes(state)).toHaveLength(2)
})

for (const status of [400, 403, 404, 503]) test(`write ${status} has recoverable feedback and no replay`, async ({ page }) => {
  const state = await setup(page, [track(1, true)])
  await open(page)
  await page.getByRole('button', { name: '编辑', exact: true }).click()
  state.failure = status
  await page.getByRole('button', { name: '保存', exact: true }).click()
  if (status === 403) await expect(page).toHaveURL(/forbidden/)
  else await expect(page.getByRole('dialog')).toContainText(status === 503 ? '结果未知' : status === 404 ? '目标已不存在' : '请求被拒绝（400）')
  expect(writes(state)).toHaveLength(1)
})

test('current 401 keeps shared login redirect; a rejected CSRF pair is never bypassed', async ({ page }) => {
  const state = await setup(page)
  await open(page)
  state.failure = 401
  await page.getByRole('button', { name: '新增录音', exact: true }).click()
  await fill(page)
  await page.getByRole('button', { name: '保存', exact: true }).click()
  await expect(page).toHaveURL(/login/)
  expect(writes(state)).toHaveLength(1)
  expect(writes(state)[0]?.csrf).toBe('synthetic-antiforgery-token')
})

test('canceling an in-flight write reports unknown, keeps draft and does not resend', async ({ page }) => {
  const state = await setup(page)
  await open(page)
  let held: Route | undefined
  state.hold = async route => { if (route.request().method() === 'POST') { held = route; await new Promise<void>(() => {}) } else await json(route, { items: [], totalCount: 0, totalPage: 0 }) }
  await page.getByRole('button', { name: '新增录音', exact: true }).click()
  await fill(page)
  await page.getByRole('button', { name: '保存', exact: true }).click()
  await expect.poll(() => Boolean(held)).toBe(true)
  await page.getByRole('button', { name: '取消等待（结果可能未知）' }).click()
  await expect(page.getByRole('dialog')).toContainText('结果未知')
  await expect(page.getByRole('textbox', { name: '录音标题' })).toHaveValue('Listening exercise')
  expect(writes(state)).toHaveLength(1)
  await held?.fulfill({ status: 200, body: '{}' }).catch(() => {})
})

for (const status of [200, 401, 403]) test(`late read ${status} after unit switch cannot change the replacement view`, async ({ page }) => {
  const state = await setup(page, [track(1)])
  state.units.push({ ...unit, id: 'second', number: 3 })
  await open(page)
  let held: Route | undefined
  state.hold = async route => {
    if (route.request().url().includes('/units/unit/')) { held = route; await new Promise<void>(() => {}) }
    else await json(route, { items: [track(9)], totalCount: 1, totalPage: 1 })
  }
  await page.getByRole('button', { name: '重新读取目录' }).click()
  await expect.poll(() => Boolean(held)).toBe(true)
  await page.locator('.unit-select').click()
  await page.getByRole('option', { name: '单元 3 · Listening', exact: true }).click()
  await expect(page.getByRole('row').filter({ has: page.getByRole('cell', { name: 'Recording 9', exact: true }) })).toBeVisible()
  await held?.fulfill({ status, contentType: 'application/json', body: JSON.stringify(status === 200 ? { success: true, data: { items: [track(1)], totalCount: 1, totalPage: 1 } } : { success: false, message: 'late refusal' }) }).catch(() => {})
  await expect(page).toHaveURL(/books\/book\/audio$/)
  await expect(page.getByRole('row').filter({ has: page.getByRole('cell', { name: 'Recording 9', exact: true }) })).toBeVisible()
  await expect(page.getByRole('row').filter({ has: page.getByRole('cell', { name: 'Recording 1', exact: true }) })).toHaveCount(0)
})

for (const width of [1440, 768]) test(`keyboard focus, aria-live, axe and no page overflow at ${width}px`, async ({ page }) => {
  await setup(page, [track(1, true)])
  await page.setViewportSize({ width, height: 1000 })
  await open(page)
  const trigger = page.getByRole('button', { name: '编辑', exact: true })
  await trigger.focus()
  await page.keyboard.press('Enter')
  await expect(page.getByRole('dialog')).toBeVisible()
  await page.getByRole('textbox', { name: '录音标题' }).press('ControlOrMeta+A')
  await page.keyboard.type('Keyboard draft')
  await expect(page.getByRole('textbox', { name: '录音标题' })).toHaveValue('Keyboard draft')
  await page.keyboard.press('Tab')
  await expect(page.getByRole('spinbutton', { name: '录音顺序' })).toBeFocused()
  await page.keyboard.press('Escape')
  await expect(page.getByRole('dialog')).toHaveCount(0)
  await expect(trigger).toBeFocused()
  await expect(page.locator('.audio-feedback')).toHaveAttribute('aria-live', 'polite')
  expect((await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze()).violations).toEqual([])
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  await page.screenshot({ path: `test-results/textbook-catalog-${width}.png`, fullPage: true })
  if (width === 768) {
    const scroller = page.locator('.el-table__body-wrapper .el-scrollbar__wrap')
    await scroller.focus()
    await page.keyboard.press('ArrowRight')
    await expect.poll(() => scroller.evaluate(el => el.scrollLeft)).toBeGreaterThan(0)
  }
  await trigger.click()
  await page.waitForFunction(() => !document.querySelector('.dialog-fade-enter-active, .dialog-fade-leave-active'))
  expect((await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze()).violations).toEqual([])
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
})

for (const method of ['GET', 'POST', 'PUT', 'DELETE']) test(`pending ${method} is invalidated immediately at logout start, including late authentication failure`, async ({ page }) => {
  const state = await setup(page, method === 'POST' ? [] : [track(1, true)])
  await open(page)
  let held: Route | undefined
  let logout: Route | undefined
  await page.route('**/admin/auth/logout', async route => { logout = route; await new Promise<void>(() => {}) })
  state.hold = async route => { held = route; await new Promise<void>(() => {}) }
  if (method === 'GET') await page.getByRole('button', { name: '重新读取目录' }).click()
  else {
    await page.getByRole('button', { name: method === 'POST' ? '新增录音' : method === 'PUT' ? '编辑' : '移除', exact: true }).click()
    if (method === 'POST') await fill(page)
    await page.getByRole('button', { name: method === 'DELETE' ? '确认移除' : '保存', exact: true }).click()
  }
  await expect.poll(() => Boolean(held)).toBe(true)
  // The shell logout is still reachable while the dialog is open only through
  // application code; simulate its ordinary click without weakening the modal.
  await page.locator('.session').getByRole('button', { name: '退出登录' }).evaluate((button: HTMLButtonElement) => button.click())
  await expect.poll(() => Boolean(logout)).toBe(true)
  await expect(page.getByRole('dialog')).toHaveCount(0)
  await expect(page.getByRole('row').filter({ has: page.getByRole('cell', { name: 'Recording 1', exact: true }) })).toHaveCount(0)
  await held?.fulfill({ status: 401, contentType: 'application/json', body: '{"success":false,"message":"late failure"}' }).catch(() => {})
  await logout?.fulfill({ status: 200, contentType: 'application/json', body: '{"success":true}' })
  await expect(page).toHaveURL(/login/)
  expect(await page.evaluate(() => [localStorage.length, sessionStorage.length])).toEqual([0, 0])
})

test('read failures differ from empty data, allow retry, and encode opaque ID components once', async ({ page }) => {
  const state = await setup(page)
  state.readFailure = 503
  await page.goto('/#/books/book/audio')
  await expect(page.getByText('目录读取失败：Read unavailable')).toBeVisible()
  await expect(page.getByText('本单元暂无录音，可新增录音目录')).toHaveCount(0)
  state.readFailure = 0
  await page.getByRole('button', { name: '重试目录', exact: true }).click()
  await expect(page.getByText('本单元暂无录音，可新增录音目录')).toBeVisible()
  await page.evaluate(() => { location.hash = '/books/book%20%25/audio' })
  await expect.poll(() => state.requests.at(-1)?.url.pathname).toBe('/admin/vocabulary-books/book%20%25/units/unit/audio-tracks')
  expect(state.requests.at(-1)?.url.pathname).not.toContain('%2525')
})

test('protected audio deep link returns through the simulated provider without exposing credentials', async ({ page }) => {
  await setup(page)
  let signedIn = false
  await page.route('**/admin/auth/session', route => signedIn ? json(route, { username: 'ci-admin', roles: ['admin'] }) : json(route, 'Authentication required', 401))
  await page.route('**/admin/auth/start?*', async route => {
    expect(new URL(route.request().url()).searchParams.get('returnUrl')).toBe('/books/book/audio')
    signedIn = true
    await route.fulfill({ contentType: 'text/html', body: '<script>location.replace("/#/books/book/audio")</script>' })
  })
  await page.goto('/#/books/book/audio')
  await expect(page).toHaveURL(/login/)
  await page.getByRole('button', { name: '使用 SignaCore 登录' }).click()
  await expect(page).toHaveURL(/books\/book\/audio$/)
  await expect(page.getByRole('button', { name: '新增录音', exact: true })).toBeEnabled()
})

for (const method of ['POST', 'PUT', 'DELETE']) test(`late ${method} after changing books cannot publish success or clear a new request`, async ({ page }) => {
  const state = await setup(page, method === 'POST' ? [] : [track(1, true)])
  await open(page)
  let held: Route | undefined
  state.hold = async route => {
    if (route.request().method() !== 'GET') { held = route; await new Promise<void>(() => {}) }
    else await json(route, { items: [track(9)], totalCount: 1, totalPage: 1 })
  }
  await page.getByRole('button', { name: method === 'POST' ? '新增录音' : method === 'PUT' ? '编辑' : '移除', exact: true }).click()
  if (method === 'POST') await fill(page)
  await page.getByRole('button', { name: method === 'DELETE' ? '确认移除' : '保存', exact: true }).click()
  await expect.poll(() => Boolean(held)).toBe(true)
  await page.evaluate(() => { location.hash = '/books/replacement/audio' })
  await expect(page.getByRole('row').filter({ has: page.getByRole('cell', { name: 'Recording 9', exact: true }) })).toBeVisible()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  await held?.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ success: true, data: track(1) }) }).catch(() => {})
  await expect(page.locator('.audio-feedback')).toHaveText('')
  await expect(page.getByRole('row').filter({ has: page.getByRole('cell', { name: 'Recording 9', exact: true }) })).toBeVisible()
})

test('an obsolete read finally cannot hide the replacement request loading state', async ({ page }) => {
  const state = await setup(page, [track(1)])
  state.units.push({ ...unit, id: 'second', number: 3 })
  await open(page)
  const pending: Route[] = []
  state.hold = async route => { pending.push(route); await new Promise<void>(() => {}) }
  await page.getByRole('button', { name: '重新读取目录' }).click()
  await expect.poll(() => pending.length).toBe(1)
  await page.locator('.unit-select').click()
  await page.getByRole('option', { name: '单元 3 · Listening', exact: true }).click()
  await expect.poll(() => pending.length).toBe(2)
  await json(pending[0]!, { items: [track(1)], totalCount: 1, totalPage: 1 }).catch(() => {})
  await expect(page.getByText('正在读取录音目录…')).toBeVisible()
  await expect(page.getByRole('button', { name: '重新读取目录' })).toBeDisabled()
  await json(pending[1]!, { items: [track(9)], totalCount: 1, totalPage: 1 })
  await expect(page.getByText('正在读取录音目录…')).toHaveCount(0)
  await expect(page.getByRole('row').filter({ has: page.getByRole('cell', { name: 'Recording 9', exact: true }) })).toBeVisible()
})

test('switching units during a 404 context reread releases its loading and ignores the old units', async ({ page }) => {
  const state = await setup(page, [track(1)])
  state.units.push({ ...unit, id: 'second', number: 3 })
  await open(page)
  let held: Route | undefined
  await page.route(/\/admin\/vocabulary-books\/[^/]+\/units$/, async route => { held = route; await new Promise<void>(() => {}) })
  state.readFailure = 404
  await page.getByRole('button', { name: '重新读取目录' }).click()
  await expect.poll(() => Boolean(held)).toBe(true)
  await expect(page.getByText('正在读取教材与单元…')).toBeVisible()
  await expect(page.getByRole('button', { name: '新增录音', exact: true })).toBeDisabled()
  state.readFailure = 0
  state.rows = [{ ...track(9), unitId: 'second' }]
  await page.locator('.unit-select').click()
  await page.getByRole('option', { name: '单元 3 · Listening', exact: true }).click()
  await expect(page.getByRole('cell', { name: 'Recording 9', exact: true })).toBeVisible()
  await expect(page.getByText('正在读取录音目录…')).toHaveCount(0)
  await expect(page.getByRole('button', { name: '新增录音', exact: true })).toBeEnabled({ timeout: 2000 })
  await json(held!, { units: [unit] }).catch(() => {})
  await expect(page.getByText('正在读取教材与单元…')).toHaveCount(0)
  await expect(page.getByText('原单元已不存在；草稿和原目标保留。', { exact: false })).toHaveCount(0)
  await expect(page.getByRole('cell', { name: 'Recording 9', exact: true })).toBeVisible()
  await expect(page.getByRole('cell', { name: 'Recording 1', exact: true })).toHaveCount(0)
  await create(page, 'Created in the replacement unit')
  expect(writes(state)).toHaveLength(1)
  expect(writes(state)[0]?.url.pathname).toContain('/units/second/audio-tracks')
})

test('an obsolete context finally cannot hide loading for a replacement book', async ({ page }) => {
  const state = await setup(page, [track(1)])
  await open(page)
  const pending: Route[] = []
  await page.route(/\/admin\/vocabulary-books\/[^/]+\/units$/, async route => { pending.push(route); await new Promise<void>(() => {}) })
  state.readFailure = 404
  await page.getByRole('button', { name: '重新读取目录' }).click()
  await expect.poll(() => pending.length).toBe(1)
  state.readFailure = 0
  await page.evaluate(() => { location.hash = '/books/replacement/audio' })
  await expect.poll(() => pending.length).toBe(2)
  await json(pending[0]!, { units: [] }).catch(() => {})
  await expect(page.getByText('正在读取教材与单元…')).toBeVisible()
  await expect(page.getByRole('button', { name: '新增录音', exact: true })).toHaveCount(0)
  await json(pending[1]!, { units: [{ ...unit, bookId: 'replacement' }] })
  await expect(page.getByText('正在读取教材与单元…')).toHaveCount(0)
  await expect(page.getByRole('button', { name: '新增录音', exact: true })).toBeEnabled()
  await expect.poll(() => state.requests.at(-1)?.url.pathname).toBe('/admin/vocabulary-books/replacement/units/unit/audio-tracks')
})

test('availability enables and disables through full conditional replacements; a file reference can still receive 409', async ({ page }) => {
  const state = await setup(page, [track(1, true)])
  await open(page)
  for (const enabled of [true, false]) {
    await page.getByRole('button', { name: '编辑', exact: true }).click()
    await page.getByRole('checkbox', { name: '设为可用' }).press('Space')
    await page.getByRole('button', { name: '保存', exact: true }).click()
    await expect(page.getByRole('dialog')).toHaveCount(0)
    expect(writes(state).at(-1)?.body?.isAvailable).toBe(enabled)
    expect(state.rows[0]?.isAvailable).toBe(enabled)
  }
  await page.getByRole('button', { name: '编辑', exact: true }).click()
  await page.getByRole('checkbox', { name: '设为可用' }).press('Space')
  state.failure = 409
  await page.getByRole('button', { name: '保存', exact: true }).click()
  await expect(page.getByRole('dialog')).toContainText('版本或文件状态冲突')
  await expect(page.getByRole('checkbox', { name: '设为可用' })).toBeChecked()
  await expect(page.getByRole('button', { name: '保存', exact: true })).toBeDisabled()
  expect(state.rows[0]?.isAvailable).toBe(false)
})

for (const status of [401, 403]) test(`an obsolete CSRF ${status} cannot clear the replacement context or poison its first write`, async ({ page }) => {
  const state = await setup(page)
  let held: Route | undefined
  let count = 0
  await page.route('**/admin/auth/csrf', async route => {
    count += 1
    if (count === 1) { held = route; await new Promise<void>(() => {}) }
    else await route.fulfill({ contentType: 'application/json', body: '{"token":"synthetic-antiforgery-token"}' })
  })
  await open(page)
  await page.getByRole('button', { name: '新增录音', exact: true }).click()
  await fill(page, 'Obsolete draft')
  await page.getByRole('button', { name: '保存', exact: true }).click()
  await expect.poll(() => Boolean(held)).toBe(true)
  await page.evaluate(() => { location.hash = '/books/replacement/audio' })
  await expect(page.getByRole('dialog')).toHaveCount(0)
  await page.getByRole('button', { name: '新增录音', exact: true }).click()
  await fill(page, 'Current draft')
  await page.getByRole('button', { name: '保存', exact: true }).click()
  await expect.poll(() => count).toBe(2)
  await expect(page.getByRole('dialog')).toHaveCount(0)
  await held?.fulfill({ status, contentType: 'application/json', body: '{"success":false,"message":"obsolete antiforgery failure"}' }).catch(() => {})
  await page.waitForTimeout(100)
  await expect(page).toHaveURL(/books\/replacement\/audio$/)
  await expect(page.locator('.session')).toContainText('ci-admin')
  expect(writes(state)).toHaveLength(1)
  expect(writes(state)[0]?.body?.title).toBe('Current draft')
})

test('a deleted unit keeps the original draft and target until an explicit unit switch', async ({ page }) => {
  const state = await setup(page, [track(1, true)])
  const second = { ...unit, id: 'second', number: 3 }
  state.units.push(second)
  await open(page)
  await page.getByRole('button', { name: '编辑', exact: true }).click()
  await fill(page, 'Retained after unit removal', '9')
  state.units = [second]
  state.failure = 404
  state.readFailure = 404
  await page.getByRole('button', { name: '保存', exact: true }).click()
  await expect(page.getByRole('dialog')).toContainText('目标已不存在')
  await expect(page.getByRole('textbox', { name: '录音标题' })).toHaveValue('Retained after unit removal')
  await expect(page.getByRole('dialog')).toContainText(`提交条件版本：${version(1)}`)
  await expect(page.getByText('原单元已不存在；草稿和原目标保留。', { exact: false })).toBeVisible()
  await expect(page.getByRole('button', { name: '保存', exact: true })).toBeDisabled()
  expect(state.requests.some(r => r.url.pathname.includes('/units/second/'))).toBe(false)
  expect(writes(state)).toHaveLength(1)
  await page.getByRole('button', { name: '取消', exact: true }).click()
  await expect(page.getByRole('button', { name: '重新读取目录' })).toBeFocused()
  state.failure = 0
  state.readFailure = 0
  state.rows = []
  await page.locator('.unit-select').click()
  await page.getByRole('option', { name: '单元 3 · Listening', exact: true }).click()
  await expect(page.getByText('本单元暂无录音，可新增录音目录')).toBeVisible()
  await expect(page.getByRole('button', { name: '新增录音', exact: true })).toBeEnabled()
  expect(state.requests.at(-1)?.url.pathname).toContain('/units/second/')
  expect(writes(state)).toHaveLength(1)
})

test('cancel while awaiting CSRF gives immediate unknown feedback and never dispatches the write', async ({ page }) => {
  const state = await setup(page)
  let held: Route | undefined
  await page.route('**/admin/auth/csrf', async route => { held = route; await new Promise<void>(() => {}) })
  await open(page)
  await page.getByRole('button', { name: '新增录音', exact: true }).click()
  await fill(page, 'Canceled before dispatch')
  await page.getByRole('button', { name: '保存', exact: true }).click()
  await expect.poll(() => Boolean(held)).toBe(true)
  await page.getByRole('button', { name: '取消等待（结果可能未知）' }).click()
  await expect(page.getByRole('dialog')).toContainText('结果未知', { timeout: 2000 })
  expect(writes(state)).toHaveLength(0)
  await expect(page.getByRole('textbox', { name: '录音标题' })).toHaveValue('Canceled before dispatch')
  await held?.fulfill({ contentType: 'application/json', body: '{"token":"synthetic-antiforgery-token"}' })
  await page.waitForTimeout(100)
  expect(writes(state)).toHaveLength(0)
  await expect(page.getByRole('button', { name: '保存', exact: true })).toBeDisabled()
})
