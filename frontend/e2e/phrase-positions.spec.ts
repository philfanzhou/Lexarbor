import { expect, test, type Page, type Route } from '@playwright/test'

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
  await expect(page.getByText('该教材暂无短语位置。')).toBeVisible()
})

async function base(page: Page) {
  await page.route('**/admin/auth/session', route => json(route, { success: true, data: { username: 'admin', roles: ['admin'] } }))
  await page.route('**/admin/system/version', route => json(route, { success: true, data: { version: '1', revision: null, channel: 'test' } }))
  await page.route(/\/admin\/vocabulary-books\?/, route => json(route, { success: true, data: { items: [book, other], totalPage: 1, totalCount: 2 } }))
  await page.route(/\/admin\/vocabulary-books\/[^/]+\/units$/, route => json(route, { success: true, data: { units } }))
}
async function choose(page: Page, selector: string, label: string) {
  await page.locator(selector).click()
  await page.getByRole('option', { name: label, exact: true }).click()
}

test('remote book paging/search retains chosen labels; failures retry without fetching all pages', async ({ page }) => {
  await base(page)
  const reads: URL[] = []
  let fail = false
  await page.route(/\/admin\/vocabulary-books\?/, route => {
    const url = new URL(route.request().url()); reads.push(url)
    const current = Number(url.searchParams.get('page'))
    if (fail) { fail = false; return json(route, { success: false, message: 'Book failure' }, 503) }
    return json(route, { success: true, data: {
      items: url.searchParams.get('keyword') ? [other] : current === 3 ? [book] : [{ ...other, id: `book-page-${current}`, bookName: `Page ${current}` }], totalCount: 41, totalPage: 3
    } })
  })
  await page.route(/\/phrase-positions/, route => json(route, { success: true, data: { items: [], totalCount: 0, totalPage: 0 } }))
  await page.goto('/#/phrases')
  await page.locator('.phrase-positions__book').click()
  await expect(page.getByRole('option', { name: 'Page 1' })).toBeVisible()
  expect(reads).toHaveLength(1)
  expect(reads[0]?.searchParams.get('size')).toBe('20')
  await page.getByRole('button', { name: '下一页教材' }).click()
  await expect(page.getByRole('option', { name: 'Page 2' })).toBeVisible()
  await page.getByRole('button', { name: '下一页教材' }).click()
  await page.getByRole('option', { name: 'Phrase Book（停用）' }).click()
  await expect(page.locator('.phrase-positions__book')).toContainText('Phrase Book（停用）')
  await page.locator('.phrase-positions__book').click()
  await page.getByRole('button', { name: '上一页教材' }).click()
  await expect(page.getByRole('option', { name: 'Page 2' })).toBeVisible()
  await expect(page.getByRole('option', { name: 'Phrase Book（停用）' })).toBeVisible()
  fail = true
  await page.locator('.phrase-positions__book input').fill('Other')
  await expect(page.locator('.phrase-positions__alert')).toContainText('Book failure')
  await page.locator('.phrase-positions__book-footer:visible').getByRole('button', { name: '重试教材列表' }).click()
  await expect(page.getByRole('option', { name: 'Other Book' })).toBeVisible()
  expect(reads.at(-1)?.searchParams.get('keyword')).toBe('Other')
  expect(reads.at(-1)?.searchParams.get('page')).toBe('1')
  await page.keyboard.press('Escape')
  await page.getByRole('heading', { name: '短语管理' }).click()
  await expect(page.locator('.phrase-positions__book')).toContainText('Phrase Book（停用）')
})

test('capacity, jumper, reset and clear preserve exact filter/count semantics', async ({ page }) => {
  await base(page)
  const queries: URL[] = []
  await page.route(/\/phrase-positions/, route => {
    const url = new URL(route.request().url()); queries.push(url)
    return json(route, { success: true, data: { items: [position, { ...position, section: 'B' }], totalCount: 101, totalPage: 6 } })
  })
  await page.goto('/#/phrases')
  await choose(page, '.phrase-positions__book', 'Phrase Book（停用）')
  await expect(page.locator('.el-table__body tbody tr')).toHaveCount(2)
  await choose(page, '.phrase-positions__unit', '第 2 单元 Two')
  await choose(page, '.phrase-positions__section', 'Section B')
  await page.getByRole('textbox', { name: '搜索短语' }).fill('take')
  await page.getByRole('button', { name: '搜索', exact: true }).click()
  await expect.poll(() => queries.at(-1)?.searchParams.get('keyword')).toBe('take')
  await choose(page, '.phrase-positions__size', '50 条/页')
  await expect.poll(() => queries.at(-1)?.searchParams.get('size')).toBe('50')
  const jump = page.locator('.el-pagination__jump input')
  await jump.fill('3'); await jump.press('Enter')
  await expect.poll(() => queries.at(-1)?.searchParams.get('page')).toBe('3')
  await page.getByRole('button', { name: '重置筛选' }).click()
  await expect.poll(() => queries.at(-1)?.searchParams.get('page')).toBe('1')
  expect(queries.at(-1)?.searchParams.has('unitId')).toBe(false)
  expect(queries.at(-1)?.searchParams.has('section')).toBe(false)
  expect(queries.at(-1)?.searchParams.has('keyword')).toBe(false)
  await expect(page.locator('.phrase-positions__book')).toContainText('Phrase Book')
  await page.locator('.phrase-positions__book').hover()
  await page.locator('.phrase-positions__book .el-select__clear').click()
  await expect(page.getByText('请选择教材查看短语位置。')).toBeVisible()
  const count = queries.length
  await page.waitForTimeout(200)
  expect(queries).toHaveLength(count)
  await expect(page.locator('.el-table')).toHaveCount(0)
})

for (const action of ['remove', 'word']) {
  test(`last-page ${action} rereads the valid page and keeps precise mutation boundaries`, async ({ page }) => {
    await base(page)
    let count = 21
    const queries: URL[] = []
    const writes: unknown[] = []
    await page.route(/\/phrase-positions/, route => {
      const url = new URL(route.request().url()); queries.push(url)
      return json(route, { success: true, data: { items: count === 20 && url.searchParams.get('page') === '2' ? [] : [position], totalCount: count, totalPage: Math.ceil(count / 20) } })
    })
    await page.route('**/admin/vocabulary-books/book-a/meanings/meaning-a/positions**', route => {
      writes.push({ method: route.request().method(), url: route.request().url(), body: route.request().method() === 'PUT' ? route.request().postDataJSON() : null }); count--
      return json(route, { success: true, data: { success: true } })
    })
    await page.goto('/#/phrases')
    await choose(page, '.phrase-positions__book', 'Phrase Book（停用）')
    await page.locator('.el-pagination').getByText('2', { exact: true }).click()
    await expect.poll(() => queries.at(-1)?.searchParams.get('page')).toBe('2')
    await page.getByRole('button', { name: action === 'remove' ? '从本单元移除' : '调整位置', exact: true }).click()
    const dialog = page.getByRole('dialog', { name: '管理单元位置' })
    if (action === 'word') {
      await dialog.locator('.el-form-item').filter({ hasText: '目标类别' }).locator('.el-select').click()
      await page.getByRole('option', { name: '词条', exact: true }).click()
    }
    await dialog.getByRole('button', { name: action === 'remove' ? '确认只移除此位置' : '保存位置' }).click()
    await expect(page.getByText('共 20 条短语位置')).toBeVisible()
    await expect.poll(() => queries.at(-1)?.searchParams.get('page')).toBe('1')
    expect(queries.slice(-2).map(url => url.searchParams.get('page'))).toEqual(['2', '1'])
    expect(writes).toHaveLength(1)
    if (action === 'remove') expect(writes[0]).toMatchObject({ method: 'DELETE', url: expect.stringContaining('/positions/unit-a?section=A&entryKind=phrase') })
    else expect(writes[0]).toMatchObject({ method: 'PUT', body: { from: { unitId: 'unit-a', section: 'A', entryKind: 'phrase' }, to: { unitId: 'unit-a', section: 'A', entryKind: 'word' } } })
  })
}

for (const width of [1440, 768]) {
  test(`phonetics, scrollable actions, focus and axe at ${width}px`, async ({ page }) => {
    const { default: AxeBuilder } = await import('@axe-core/playwright')
    await page.setViewportSize({ width, height: 1000 })
    await base(page)
    await page.route(/\/phrase-positions/, route => json(route, { success: true, data: { items: [{ ...position, phoneticUk: '/teɪk/', partOfSpeech: null }], totalCount: 1, totalPage: 1 } }))
    await page.route('**/admin/vocabulary/word-a', route => json(route, { success: true, data: { id: 'word-a', word: 'take off', phoneticUk: null, phoneticUs: null, books: [], meanings: [] } }))
    await page.goto('/#/phrases')
    await choose(page, '.phrase-positions__book', 'Phrase Book（停用）')
    await expect(page.getByText('/teɪk/', { exact: true })).toBeVisible()
    await expect(page.locator('.el-table__body .cell').filter({ hasText: /^—$/ })).toHaveCount(2)
    const detail = page.getByRole('button', { name: '详情', exact: true })
    await detail.focus(); await page.keyboard.press('Enter')
    await expect(page.getByRole('dialog')).toBeVisible()
    await page.keyboard.press('Escape')
    await expect(detail).toBeFocused()
    await expect(page.getByRole('dialog')).toBeHidden()
    await expect(page.locator('.el-select-dropdown:visible')).toHaveCount(0)
    await page.waitForTimeout(300)
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
    expect((await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze()).violations).toEqual([])
  })
}

test('late book searches and previous-book units/positions cannot repaint current selection', async ({ page }) => {
  await base(page)
  let release: (() => void) | undefined
  const gate = new Promise<void>(resolve => { release = resolve })
  const started = new Set<string>()
  await page.route(/\/admin\/vocabulary-books\?/, async route => {
    const keyword = new URL(route.request().url()).searchParams.get('keyword')
    if (keyword === 'old') { started.add('books'); await gate }
    await json(route, { success: true, data: { items: keyword === 'new' ? [other] : [book, other], totalPage: 1, totalCount: 2 } })
  })
  await page.route(/\/units$/, async route => {
    if (route.request().url().includes('book-a')) { started.add('units'); await gate }
    await json(route, { success: true, data: { units: route.request().url().includes('book-a') ? units : [{ ...units[0], id: 'unit-b', bookId: 'book-b', title: 'Other unit' }] } })
  })
  await page.route(/\/phrase-positions/, async route => {
    if (route.request().url().includes('book-a')) { started.add('positions'); await gate }
    await json(route, { success: true, data: { items: [{ ...position, word: route.request().url().includes('book-a') ? 'old phrase' : 'new phrase' }], totalCount: 1, totalPage: 1 } })
  })
  await page.goto('/#/phrases')
  await choose(page, '.phrase-positions__book', 'Phrase Book（停用）')
  await expect.poll(() => started.has('units') && started.has('positions')).toBe(true)
  await choose(page, '.phrase-positions__book', 'Other Book')
  await expect(page.getByText('new phrase', { exact: true })).toBeVisible()
  await page.locator('.phrase-positions__book').click()
  await page.locator('.phrase-positions__book input').fill('old')
  await expect.poll(() => started.has('books')).toBe(true)
  await page.locator('.phrase-positions__book input').fill('new')
  await expect(page.getByRole('option', { name: 'Phrase Book（停用）' })).toHaveCount(0)
  release?.()
  await page.keyboard.press('Escape')
  await expect(page.locator('.phrase-positions__book')).toContainText('Other Book')
  await expect(page.getByText('old phrase', { exact: true })).toHaveCount(0)
  await page.locator('.phrase-positions__unit').click()
  await expect(page.getByRole('option', { name: '第 2 单元 Other unit' })).toBeVisible()
  await expect(page.getByRole('option', { name: '第 2 单元 Two' })).toHaveCount(0)
})

test('unmount cancels each independent outstanding read', async ({ page }) => {
  await base(page)
  const canceled: string[] = []
  page.on('requestfailed', request => canceled.push(request.url()))
  let started = 0
  let release: (() => void) | undefined
  const gate = new Promise<void>(resolve => { release = resolve })
  await page.route(/\/units$|\/phrase-positions/, async route => { started++; await gate; await json(route, { success: true, data: { units: [], items: [], totalCount: 0, totalPage: 0 } }) })
  await page.goto('/#/phrases')
  await choose(page, '.phrase-positions__book', 'Phrase Book（停用）')
  await expect.poll(() => started).toBe(2)
  await page.getByRole('link', { name: '单条导入', exact: true }).click()
  await expect.poll(() => canceled.filter(url => /\/units$|\/phrase-positions/.test(url)).length).toBe(2)
  release?.()
  await expect(page.getByRole('heading', { name: '单条导入' })).toBeVisible()
})
