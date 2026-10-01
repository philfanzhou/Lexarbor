import { expect, test, type Route } from '@playwright/test'

const book = { id: 'book-a', bookName: 'Phrase Book', status: false, displayOrder: 1 }
const other = { id: 'book-b', bookName: 'Other Book', status: true, displayOrder: 2 }
const units = [{ id: 'unit-a', bookId: book.id, number: 2, title: 'Two', meaningCount: 1 }]
const position = {
  bookId: book.id, unitId: 'unit-a', meaningId: 'meaning-a', section: 'A', entryKind: 'phrase',
  number: 2, title: 'Two', wordId: 'word-a', word: 'take off', phoneticUk: null,
  phoneticUs: null, partOfSpeech: 'v.', meaning: 'leave', example: null
}
const json = (route: Route, data: unknown, status = 200) => route.fulfill({
  status, contentType: 'application/json', body: JSON.stringify(data)
})

test('navigates, filters phrase positions, pages and opens detail', async ({ page }) => {
  const queries: URL[] = []
  await page.route('**/admin/auth/session', route => json(route, { success: true, data: { username: 'admin', roles: ['admin'] } }))
  await page.route('**/admin/system/version', route => json(route, { success: true, data: { version: '1', revision: null, channel: 'test' } }))
  await page.route(/\/admin\/vocabulary-books\?/, route => json(route, { success: true, data: { items: [book, other], totalPage: 1, totalCount: 2 } }))
  await page.route(/\/admin\/vocabulary-books\/[^/]+\/units$/, route => json(route, { success: true, data: { units } }))
  await page.route(/\/admin\/vocabulary-books\/[^/]+\/phrase-positions/, route => {
    const url = new URL(route.request().url())
    queries.push(url)
    return json(route, { success: true, data: { items: [position], totalCount: 21, totalPage: 2 } })
  })
  await page.route('**/admin/vocabulary/word-a', route => json(route, { success: true, data: {
    id: 'word-a', word: 'take off', phoneticUk: null, phoneticUs: null,
    books: [{ id: book.id, bookName: book.bookName, status: false }],
    meanings: [{ id: 'meaning-a', vocabularyId: 'word-a', bookId: book.id,
      partOfSpeech: 'v.', meaning: 'leave', example: null, units: [{ unitId: 'unit-a', number: 2, title: 'Two', section: 'A', entryKind: 'phrase' }] }]
  } }))
  await page.goto('/#/books')
  await page.getByRole('link', { name: '短语管理' }).click()
  await expect(page.getByText('请选择教材查看短语位置。')).toBeVisible()
  await page.locator('.phrase-positions__header').getByRole('link', { name: '新增短语' }).click()
  await expect(page).toHaveURL(/#\/import\/phrase$/)
  await page.getByRole('link', { name: '短语管理' }).click()
  await page.locator('.phrase-positions__book').click()
  await page.locator('.el-select-dropdown__item:visible').filter({ hasText: 'Phrase Book（停用）' }).click()
  await expect(page.getByText('take off')).toBeVisible()
  await page.locator('.phrase-positions__unit').click()
  await page.locator('.el-select-dropdown__item:visible').filter({ hasText: '第 2 单元 Two' }).click()
  await page.locator('.phrase-positions__section').click()
  await page.locator('.el-select-dropdown__item:visible').filter({ hasText: 'Section A' }).click()
  await page.getByRole('textbox', { name: '搜索短语' }).fill('take')
  await page.getByRole('button', { name: '搜索' }).click()
  await expect.poll(() => queries.at(-1)?.searchParams.get('keyword')).toBe('take')
  expect(queries.at(-1)?.searchParams.get('unitId')).toBe('unit-a')
  expect(queries.at(-1)?.searchParams.get('section')).toBe('A')
  await page.locator('.el-pagination').getByText('2', { exact: true }).click()
  await expect.poll(() => queries.at(-1)?.searchParams.get('page')).toBe('2')
  await page.getByRole('button', { name: '详情' }).click()
  await expect(page.getByText('leave').first()).toBeVisible()
})

test('empty and failed reads can be retried', async ({ page }) => {
  let attempts = 0
  await page.route('**/admin/auth/session', route => json(route, { success: true, data: { username: 'admin', roles: ['admin'] } }))
  await page.route('**/admin/system/version', route => json(route, { success: true, data: { version: '1', revision: null, channel: 'test' } }))
  await page.route(/\/admin\/vocabulary-books\?/, route => json(route, { success: true, data: { items: [book], totalPage: 1, totalCount: 1 } }))
  await page.route(/\/admin\/vocabulary-books\/[^/]+\/units$/, route => json(route, { success: true, data: { units } }))
  await page.route(/\/admin\/vocabulary-books\/[^/]+\/phrase-positions/, route => {
    attempts += 1
    return attempts === 1
      ? json(route, { success: false, message: 'Temporary failure' }, 503)
      : json(route, { success: true, data: { items: [], totalCount: 0, totalPage: 0 } })
  })
  await page.goto('/#/phrases')
  await page.locator('.phrase-positions__book').click()
  await page.locator('.el-select-dropdown__item:visible').filter({ hasText: 'Phrase Book（停用）' }).click()
  await expect(page.getByRole('alert')).toContainText('Temporary failure')
  await page.getByRole('button', { name: '重试' }).click()
  await expect(page.getByText('没有匹配的短语位置。')).toBeVisible()
})
