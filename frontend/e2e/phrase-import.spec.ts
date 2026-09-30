import { expect, test, type Page, type Route } from '@playwright/test'

const books = [
  { id: 'book-a', bookName: 'Book A', status: true },
  { id: 'book-b', bookName: 'Book B', status: true }
]
const json = (route: Route, data: unknown, status = 200) => route.fulfill({
  status, contentType: 'application/json', body: JSON.stringify(data)
})

async function open(page: Page) {
  await page.route('**/admin/auth/session', route => json(route, { success: true, data: { username: 'admin', roles: ['admin'] } }))
  await page.route('**/admin/auth/method', route => json(route, { success: true, data: { method: 'password' } }))
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
  await expect(page.getByRole('alert')).toContainText('Rejected entry')
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
