import { expect, test, type Page, type Route } from '@playwright/test'
import { mockAntiforgery } from './support/antiforgery'

const books = [
  { id: 'book-a', bookName: 'Book A', status: true },
  { id: 'book-b', bookName: 'Book B', status: true }
]
const json = (route: Route, data: unknown, status = 200) => route.fulfill({
  status, contentType: 'application/json', body: JSON.stringify(data)
})

async function open(page: Page) {
  await page.route('**/admin/auth/session', route => json(route, { success: true, data: { username: 'admin', roles: ['admin'] } }))
  await mockAntiforgery(page)
  await page.route('**/admin/system/version', route => json(route, { success: true, data: { version: '1', revision: null, channel: 'test' } }))
  await page.route('**/api/vocabulary-books/all', route => json(route, { success: true, data: { books } }))
  await page.goto('/#/import/phrase')
  await expect(page.getByRole('heading', { name: '新增短语' })).toBeVisible()
}

async function pick(page: Page, selector: string, label: string) {
  await page.locator(selector).click()
  await page.locator('.el-select-dropdown__item:visible').filter({ hasText: label }).click()
}

test('submits one explicit phrase position and reports created or reused', async ({ page }) => {
  const bodies: unknown[] = []
  let created = true
  await page.route(/\/admin\/vocabulary-books\/[^/]+\/units$/, route => json(route, {
    success: true, data: { units: [{ id: 'unit-a', bookId: 'book-a', number: 2, title: 'Two', meaningCount: 0 }] }
  }))
  await page.route('**/admin/vocabulary/batch', route => {
    bodies.push(route.request().postDataJSON())
    return json(route, { success: true, data: { total: 1, created: created ? 1 : 0, reused: created ? 0 : 1 } })
  })
  await open(page)
  // A phrase has no phonetics and no part of speech, so the form offers neither.
  const form = page.locator('.phrase-import__form')
  await expect(form.getByRole('heading', { name: '释义与例句' })).toBeVisible()
  await expect(form.getByRole('heading', { name: '发音' })).toHaveCount(0)
  for (const label of ['英式音标', '美式音标', '词性']) await expect(form.locator('.el-form-item__label', { hasText: label })).toHaveCount(0)
  await expect(form.locator('.el-form-item__label')).toHaveText(['教材', '单元', '分节', '类别', '英文短语', '释义', '例句'])
  await page.getByRole('button', { name: '新增短语' }).click()
  await expect(page.getByRole('alert')).toContainText('请选择教材和单元')
  expect(bodies).toHaveLength(0)
  await pick(page, '.phrase-import__book', 'Book A')
  await pick(page, '.phrase-import__unit', '第 2 单元 Two')
  await pick(page, '.phrase-import__section', 'Section A')
  await page.getByPlaceholder('如：take off').fill('take off')
  await page.getByPlaceholder('如：起飞').fill('起飞')
  await page.getByRole('button', { name: '新增短语' }).click()
  await expect(page.getByRole('status')).toContainText('已新增短语词义')
  expect(bodies).toEqual([{ bookId: 'book-a', entries: [{ word: 'take off', meaning: '起飞', unitId: 'unit-a', section: 'A', entryKind: 'phrase' }] }])
  for (const field of ['phoneticUk', 'phoneticUs', 'partOfSpeech']) expect(Object.keys((bodies[0] as { entries: object[] }).entries[0])).not.toContain(field)

  created = false
  await page.getByPlaceholder('如：take off').fill('take off')
  await page.getByPlaceholder('如：起飞').fill('起飞')
  await page.getByRole('button', { name: '新增短语' }).click()
  await expect(page.getByRole('status')).toContainText('已复用现有词义')
  expect(bodies).toHaveLength(2)
})

test('book switch clears unit and section; failed unit load and submit keep the draft', async ({ page }) => {
  let bookBLoads = 0
  let postCount = 0
  await page.route(/\/admin\/vocabulary-books\/[^/]+\/units$/, route => {
    const id = new URL(route.request().url()).pathname.split('/').at(-2)
    if (id === 'book-b' && ++bookBLoads === 1) return json(route, { success: false, message: 'Units unavailable' }, 503)
    return json(route, { success: true, data: { units: [{ id: id === 'book-a' ? 'unit-a' : 'unit-b', bookId: id, number: 2, title: 'Two', meaningCount: 0 }] } })
  })
  await page.route('**/admin/vocabulary/batch', route => {
    postCount += 1
    return json(route, { success: false, message: 'Rejected entry' }, 400)
  })
  await open(page)
  await pick(page, '.phrase-import__book', 'Book A')
  await pick(page, '.phrase-import__unit', '第 2 单元 Two')
  await pick(page, '.phrase-import__section', 'Section B')
  await page.getByPlaceholder('如：take off').fill('look after')
  await page.getByPlaceholder('如：起飞').fill('照顾')
  await pick(page, '.phrase-import__book', 'Book B')
  await expect(page.locator('.phrase-import__unit')).toContainText('选择现有单元')
  await expect(page.locator('.phrase-import__section')).not.toContainText('Section B')
  await expect(page.getByRole('alert')).toContainText('Units unavailable')
  await page.getByRole('button', { name: '重试单元列表' }).click()
  await pick(page, '.phrase-import__unit', '第 2 单元 Two')
  await page.getByRole('button', { name: '新增短语' }).click()
  await expect(page.locator('.phrase-import__alert')).toContainText('Rejected entry')
  await expect(page.getByPlaceholder('如：take off')).toHaveValue('look after')
  expect(postCount).toBe(1)
})

test('late unit response from an earlier book cannot restore its choices', async ({ page }) => {
  let releaseFirst: (() => void) | undefined
  let signalFirst: (() => void) | undefined
  const firstStarted = new Promise<void>(resolve => { signalFirst = resolve })
  const firstRelease = new Promise<void>(resolve => { releaseFirst = resolve })
  await page.route(/\/admin\/vocabulary-books\/[^/]+\/units$/, async route => {
    const id = new URL(route.request().url()).pathname.split('/').at(-2)
    if (id === 'book-a') {
      signalFirst?.()
      await firstRelease
    }
    return json(route, { success: true, data: { units: [{ id: `unit-${id}`, bookId: id, number: id === 'book-a' ? 1 : 2, title: id, meaningCount: 0 }] } })
  })
  await open(page)
  await pick(page, '.phrase-import__book', 'Book A')
  await firstStarted
  await pick(page, '.phrase-import__book', 'Book B')
  await expect(page.locator('.phrase-import__unit')).toContainText('选择现有单元')
  releaseFirst?.()
  await page.locator('.phrase-import__unit').click()
  await expect(page.getByRole('option', { name: '第 2 单元 book-b' })).toBeVisible()
  await expect(page.getByRole('option', { name: '第 1 单元 book-a' })).toHaveCount(0)
})

async function draft(page: Page) {
  await pick(page, '.phrase-import__book', 'Book A')
  await pick(page, '.phrase-import__unit', '第 2 单元 Two')
  await pick(page, '.phrase-import__section', 'Section A')
  await page.getByPlaceholder('如：take off').fill(' take off ')
  await page.getByPlaceholder('如：起飞').fill(' 起飞 ')
}
async function mockUnits(page: Page) {
  await page.route(/\/admin\/vocabulary-books\/[^/]+\/units$/, route => json(route, {
    success: true, data: { units: [{ id: 'unit-a', bookId: 'book-a', number: 2, title: 'Two', meaningCount: 0 }] }
  }))
}

test('freezes the write snapshot and all editors; clear retains location without a write', async ({ page }) => {
  await mockUnits(page)
  let release: (() => void) | undefined
  const gate = new Promise<void>(resolve => { release = resolve })
  const bodies: unknown[] = []
  await page.route('**/admin/vocabulary/batch', async route => {
    bodies.push(route.request().postDataJSON())
    await gate
    await json(route, { success: true, data: { total: 1, created: 1, reused: 0 } })
  })
  await open(page)
  await draft(page)
  await page.getByRole('button', { name: '新增短语' }).click()
  await expect.poll(() => bodies.length).toBe(1)
  for (const input of await page.locator('.phrase-import__form input, .phrase-import__form textarea').all()) await expect(input).toBeDisabled()
  await expect(page.getByRole('button', { name: '清空内容' })).toBeDisabled()
  await page.locator('.phrase-import__form').evaluate(form => form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })))
  expect(bodies).toHaveLength(1)
  release?.()
  await expect(page.getByRole('status')).toContainText('「take off」 · Book A / 第 2 单元 / Section A')
  await expect(page.getByPlaceholder('如：take off')).toHaveValue('')
  await expect(page.locator('.phrase-import__unit')).toContainText('第 2 单元 Two')
  await page.getByPlaceholder('如：take off').fill('another draft')
  await page.getByRole('button', { name: '清空内容' }).click()
  await expect(page.getByPlaceholder('如：take off')).toHaveValue('')
  await expect(page.getByRole('status')).toHaveCount(0)
  await expect(page.locator('.phrase-import__section')).toContainText('Section A')
  expect(bodies).toEqual([{ bookId: 'book-a', entries: [{ word: 'take off', meaning: '起飞', unitId: 'unit-a', section: 'A', entryKind: 'phrase' }] }])
})

for (const status of [200, 400]) {
  test(`old ${status} response cannot update a re-entered page`, async ({ page }) => {
    await mockUnits(page)
    let release: (() => void) | undefined
    const gate = new Promise<void>(resolve => { release = resolve })
    let started = false
    await page.route('**/admin/vocabulary/batch', async route => {
      started = true
      await gate
      await json(route, status === 200 ? { success: true, data: { total: 1, created: 1, reused: 0 } } : { success: false, message: 'Old failure' }, status)
    })
    await open(page)
    await draft(page)
    await page.getByRole('button', { name: '新增短语' }).click()
    await expect.poll(() => started).toBe(true)
    await page.locator('a[href="#/import"]').click()
    await expect(page.getByRole('heading', { name: '单条导入', exact: true })).toBeVisible()
    await page.getByRole('link', { name: '要指定单元新增短语？前往新增短语' }).click()
    await page.getByPlaceholder('如：take off').fill('look after')
    const response = page.waitForResponse('**/admin/vocabulary/batch')
    release?.()
    await response
    await expect(page.getByPlaceholder('如：take off')).toHaveValue('look after')
    await expect(page.getByRole('status')).toHaveCount(0)
    await expect(page.getByText('Old failure')).toHaveCount(0)
  })
}

for (const status of [400, 404, 422, 503, 0]) {
  test(`submission ${status || 'network uncertainty'} retains input`, async ({ page }) => {
    await mockUnits(page)
    let posts = 0
    await page.route('**/admin/vocabulary/batch', route => {
      posts++
      return status ? json(route, { success: false, message: 'Rejected', errors: status === 400 ? [{ index: 0, message: 'Invalid meaning' }] : undefined }, status) : route.abort('failed')
    })
    await open(page)
    await draft(page)
    await page.getByRole('button', { name: '新增短语' }).click()
    await expect(page.getByRole('alert')).toContainText(status === 400 ? 'Invalid meaning' : status === 404 ? '不存在' : status === 422 ? '停用' : status === 503 ? 'Rejected' : '提交结果未知')
    await expect(page.getByPlaceholder('如：take off')).toHaveValue(' take off ')
    if (!status) await expect(page.getByRole('link', { name: '前往短语管理核对' })).toBeVisible()
    expect(posts).toBe(1)
  })
}

test('whitespace validation focuses the first error; missing units link to management', async ({ page }) => {
  let unitsEmpty = false
  let posts = 0
  await page.route(/\/admin\/vocabulary-books\/[^/]+\/units$/, route => json(route, { success: true, data: { units: unitsEmpty ? [] : [{ id: 'unit-a', bookId: 'book-a', number: 2, title: 'Two', meaningCount: 0 }] } }))
  await page.route('**/admin/vocabulary/batch', route => { posts++; return json(route, {}) })
  await open(page)
  await draft(page)
  await page.getByPlaceholder('如：take off').fill('   ')
  await page.getByRole('button', { name: '新增短语' }).focus()
  await page.keyboard.press('Enter')
  await expect(page.getByPlaceholder('如：take off')).toBeFocused()
  await expect(page.locator('.el-form-item__error')).toContainText('请输入英文短语')
  expect(posts).toBe(0)
  unitsEmpty = true
  await pick(page, '.phrase-import__book', 'Book B')
  await expect(page.getByRole('link', { name: '前往教材管理' })).toBeVisible()
  await expect(page.getByRole('button', { name: '新增短语' })).toBeDisabled()
})

for (const width of [1440, 768]) {
  test(`phrase form at ${width}px has no overflow or accessibility violations`, async ({ page }) => {
    const { default: AxeBuilder } = await import('@axe-core/playwright')
    await page.setViewportSize({ width, height: 1000 })
    await mockUnits(page)
    await open(page)
    await draft(page)
    await expect(page.locator('.el-select-dropdown:visible')).toHaveCount(0)
    await page.waitForTimeout(300)
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
    const result = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze()
    expect(result.violations).toEqual([])
  })
}
