import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page, type Route } from '@playwright/test'

const admin = {
  username: 'ci-admin',
  roles: ['admin']
}

const bookA = {
  id: '11111111-1111-1111-1111-111111111111',
  bookName: 'CI Book A',
  description: 'Browser test fixture',
  publisher: 'Lexarbor',
  educationLevel: 'Secondary',
  grade: 'Grade 7',
  category: 'English',
  displayOrder: 1,
  status: true,
  iconUrl: ''
}

const bookB = {
  ...bookA,
  id: '22222222-2222-2222-2222-222222222222',
  bookName: 'CI Old Book B',
  displayOrder: 2,
  status: false
}

const deepBook = {
  ...bookA,
  id: '33333333-3333-3333-3333-333333333333',
  bookName: 'CI Deep Book',
  displayOrder: 3,
  status: false
}

function ref(book: { id: string; bookName: string; status: boolean }) {
  return { id: book.id, bookName: book.bookName, status: book.status }
}

const refA = ref(bookA)
const refB = ref(bookB)

const meaningsA = [
  { id: 'meaning-a1', vocabularyId: 'word-apple', bookId: bookA.id, partOfSpeech: 'n.', meaning: '苹果', example: 'An apple a day.' },
  { id: 'meaning-a2', vocabularyId: 'word-apple', bookId: bookA.id, partOfSpeech: 'n.', meaning: '苹果树', example: null }
]
const meaningB = [
  { id: 'meaning-b1', vocabularyId: 'word-apple', bookId: bookB.id, partOfSpeech: 'n.', meaning: '一种水果', example: null }
]

const apple = { id: 'word-apple', word: 'apple', phoneticUk: '/ˈæp.əl/', phoneticUs: '/ˈæp.əl/', books: [refA, refB] }
const cherry = { id: 'word-cherry', word: 'cherry', phoneticUk: '/ˈtʃer.i/', phoneticUs: null, books: [refA] }
const banana = { id: 'word-banana', word: 'banana', phoneticUk: null, phoneticUs: null, books: [refB] }
const legacy = { id: 'word-legacy', word: 'legacy', phoneticUk: null, phoneticUs: null, books: [] }

const appleDetail = { ...apple, meanings: [...meaningsA, ...meaningB] }
const legacyDetail = { ...legacy, meanings: [] }

const contentA = {
  book: bookA,
  wordCount: 2,
  meaningCount: 3,
  items: [
    { ...apple, meanings: meaningsA },
    { ...cherry, meanings: [{ id: 'meaning-c1', vocabularyId: 'word-cherry', bookId: bookA.id, partOfSpeech: 'n.', meaning: '樱桃', example: null }] }
  ],
  totalCount: 2,
  totalPage: 1
}

const contentB = {
  book: bookB,
  wordCount: 2,
  meaningCount: 2,
  items: [
    { ...apple, meanings: meaningB },
    { ...banana, meanings: [{ id: 'meaning-d1', vocabularyId: 'word-banana', bookId: bookB.id, partOfSpeech: 'n.', meaning: '香蕉', example: null }] }
  ],
  totalCount: 2,
  totalPage: 1
}

const libraryPage = {
  items: [apple, cherry, banana, legacy],
  totalCount: 4,
  totalPage: 1
}

const wcagTags = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']

function json(route: Route, data: unknown, status = 200) {
  return route.fulfill({
    status,
    contentType: 'application/json',
    body: JSON.stringify(data)
  })
}

async function mockSession(page: Page) {
  await page.route('**/admin/auth/session', (route) =>
    json(route, { success: true, data: admin }))
}

async function mockBooksList(page: Page, books = [bookA, bookB]) {
  await page.route(/\/admin\/vocabulary-books(\?.*)?$/, (route) => {
    if (route.request().method() !== 'GET') {
      return route.fallback()
    }

    return json(route, { success: true, data: { items: books, totalCount: books.length, totalPage: 1 } })
  })
  await page.route('**/admin/vocabulary-books/categories', (route) =>
    json(route, { success: true, data: { items: ['English'] } }))
  await page.route('**/admin/vocabulary-books/education-levels', (route) =>
    json(route, { success: true, data: { items: ['Secondary'] } }))
}

async function mockBookContent(page: Page) {
  await page.route(/\/admin\/vocabulary-books\/[^/]+\/content(\?.*)?$/, (route) => {
    const bookId = new URL(route.request().url()).pathname.split('/')[3]
    return json(route, { success: true, data: bookId === bookB.id ? contentB : contentA })
  })
}

async function mockLibrary(page: Page, data = libraryPage) {
  await page.route(/\/admin\/vocabulary(\?.*)?$/, (route) =>
    json(route, { success: true, data }))
}

async function mockWordDetail(page: Page) {
  await page.route(/\/admin\/vocabulary\/[^/]+$/, (route) => {
    const wordId = new URL(route.request().url()).pathname.split('/').at(-1)
    return json(route, { success: true, data: wordId === legacy.id ? legacyDetail : appleDetail })
  })
}

async function openBooksList(page: Page, books = [bookA, bookB]) {
  await mockSession(page)
  await mockBooksList(page, books)
  await mockBookContent(page)
  await page.goto('/#/books')
  await expect(page.locator('.session')).toContainText(admin.username)
}

async function openBookWords(page: Page, book = bookA) {
  await openBooksList(page)
  await page.locator('.el-table__row', { hasText: book.bookName }).getByRole('button', { name: '查看单词' }).click()
  await expect(page).toHaveURL(new RegExp(`#/books/${book.id}/words$`))
}

async function openLibrary(page: Page) {
  await mockSession(page)
  await mockBooksList(page)
  await mockLibrary(page)
  await mockWordDetail(page)
  await page.goto('/#/vocabulary')
  await expect(page.locator('.page-header h1')).toHaveText('单词管理')
}

async function focusedText(page: Page) {
  return page.evaluate(() => {
    const element = document.activeElement as HTMLElement | null
    return element?.innerText.trim() ?? ''
  })
}

/**
 * Holds every matching request until the test answers it, in arrival order,
 * so out-of-order and late answers can be orchestrated deliberately.
 */
function useDeferredRoute(page: Page, pattern: RegExp) {
  const pending: Array<(answer: { data: unknown; status: number }) => void> = []
  const requests: URL[] = []

  void page.route(pattern, (route) => {
    requests.push(new URL(route.request().url()))
    return new Promise<void>((resolve) => {
      pending.push((answer) => {
        json(route, { success: answer.status < 400, data: answer.data }, answer.status).catch(() => undefined)
        resolve()
      })
    })
  })

  return {
    requests,
    answerNext(data: unknown, status = 200) {
      const answer = pending.shift()
      if (!answer) {
        throw new Error('No request is waiting for an answer.')
      }

      answer({ data, status })
    },
    /** Answers the most recent request first, so a late one can stay late. */
    answerLast(data: unknown, status = 200) {
      const answer = pending.pop()
      if (!answer) {
        throw new Error('No request is waiting for an answer.')
      }

      answer({ data, status })
    }
  }
}

test('opens a book\'s word list from 教材管理 and shows its identity and counts', async ({ page }) => {
  await openBookWords(page, bookA)

  await expect(page.locator('.page-header h1')).toHaveText('教材词表')
  await expect(page.locator('.page-header__description')).toContainText('CI Book A')
  await expect(page.locator('.book-words__meta')).toContainText('启用')
  await expect(page.locator('.book-words__meta')).toContainText('去重单词 2')
  await expect(page.locator('.book-words__meta')).toContainText('释义 3')
  await expect(page.locator('.el-table')).toContainText('apple')
  await expect(page.locator('.el-table')).toContainText('cherry')
  // Two meanings of one word in this book are two rows of one cell, not two
  // words; the counts above stay word-based.
  const appleRow = page.locator('.el-table__row', { hasText: 'apple' })
  await expect(appleRow).toContainText('苹果')
  await expect(appleRow).toContainText('苹果树')
})

test('runs the keyword and paging on the server and keeps whole-book counts', async ({ page }) => {
  const requests: URL[] = []
  await mockSession(page)
  await mockBooksList(page)
  await page.route(/\/admin\/vocabulary-books\/[^/]+\/content(\?.*)?$/, (route) => {
    requests.push(new URL(route.request().url()))
    return json(route, { success: true, data: { ...contentA, totalCount: 40, totalPage: 2 } })
  })
  await page.goto('/#/books')
  await page.locator('.el-table__row', { hasText: bookA.bookName }).getByRole('button', { name: '查看单词' }).click()
  await expect(page.locator('.el-table')).toContainText('apple')

  await page.getByRole('textbox', { name: '搜索单词' }).fill('app')
  await page.getByRole('button', { name: '搜索', exact: true }).click()
  await expect.poll(() => requests.at(-1)?.searchParams.get('keyword')).toBe('app')
  expect(requests.at(-1)?.searchParams.get('page')).toBe('1')

  // Whole-book counts do not shrink with the keyword or the page: the server
  // counts the book, the keyword only narrows the matching page.
  await expect(page.locator('.book-words__meta')).toContainText('去重单词 2')
  await expect(page.locator('.book-words__meta')).toContainText('释义 3')

  await page.locator('.el-pagination').getByText('2', { exact: true }).click()
  await expect.poll(() => requests.at(-1)?.searchParams.get('page')).toBe('2')
  await expect(page.locator('.book-words__meta')).toContainText('去重单词 2')
})

test('tells an empty book and a keyword miss apart', async ({ page }) => {
  await openBookWords(page, bookA)
  await page.route(/\/admin\/vocabulary-books\/[^/]+\/content(\?.*)?$/, (route) => {
    const keyword = new URL(route.request().url()).searchParams.get('keyword')
    return json(route, {
      success: true,
      data: keyword ? { ...contentA, items: [], totalCount: 0, totalPage: 0 } : contentA
    })
  })

  await page.getByRole('textbox', { name: '搜索单词' }).fill('zzz')
  await page.getByRole('button', { name: '搜索', exact: true }).click()

  const empty = page.locator('.el-table__empty-block')
  await expect(empty).toContainText('没有匹配的单词')
})

test('maintains a disabled book the same way and finds the way back', async ({ page }) => {
  await openBookWords(page, bookB)

  await expect(page.locator('.page-header__description')).toContainText('CI Old Book B')
  await expect(page.locator('.book-words__meta')).toContainText('停用')
  await expect(page.locator('.el-table')).toContainText('banana')

  await page.getByRole('button', { name: '返回教材列表' }).click()
  await expect(page).toHaveURL(/#\/books$/)
  await expect(page.locator('.el-table')).toContainText('CI Old Book B')
})

test('a missing book is explicit, not an empty word list', async ({ page }) => {
  await openBooksList(page)
  await page.route(/\/admin\/vocabulary-books\/[^/]+\/content(\?.*)?$/, (route) =>
    json(route, { success: false, message: 'No such book.' }, 404))
  await page.locator('.el-table__row', { hasText: bookA.bookName }).getByRole('button', { name: '查看单词' }).click()

  await expect(page.locator('.book-words__notice')).toContainText('教材不存在或已被删除')
  await page.locator('.book-words__notice').getByRole('button', { name: '返回教材列表' }).click()
  await expect(page).toHaveURL(/#\/books$/)
})

test('a failed load offers a retry and recovers', async ({ page }) => {
  let fail = true
  await openBooksList(page)
  await page.route(/\/admin\/vocabulary-books\/[^/]+\/content(\?.*)?$/, (route) => fail
    ? json(route, { success: false, message: 'Words are unavailable.' }, 500)
    : json(route, { success: true, data: contentA }))
  await page.locator('.el-table__row', { hasText: bookA.bookName }).getByRole('button', { name: '查看单词' }).click()

  const alert = page.locator('.book-words__error')
  await expect(page.locator('.el-message--error')).toContainText('Words are unavailable.')
  await expect(alert).toContainText('Words are unavailable.')

  fail = false
  await alert.getByRole('button', { name: '重试' }).click()

  await expect(alert).toHaveCount(0)
  await expect(page.locator('.el-table')).toContainText('apple')
})

test('lists the whole library with disabled-only and unassigned words', async ({ page }) => {
  await openLibrary(page)

  const appleRow = page.locator('.el-table__row', { hasText: 'apple' })
  await expect(appleRow).toContainText('CI Book A')
  await expect(appleRow).toContainText('CI Old Book B（停用）')

  const bananaRow = page.locator('.el-table__row', { hasText: 'banana' })
  await expect(bananaRow).toContainText('CI Old Book B（停用）')
  await expect(bananaRow.locator('.el-tag')).toHaveCount(1)

  await expect(page.locator('.el-table__row', { hasText: 'legacy' })).toContainText('无教材归属')
  await expect(page.locator('.el-pagination')).toContainText('共 4 条')
})

test('sends the keyword and the book filter to the server', async ({ page }) => {
  const requests: URL[] = []
  await mockSession(page)
  await mockBooksList(page)
  await page.route(/\/admin\/vocabulary(\?.*)?$/, (route) => {
    requests.push(new URL(route.request().url()))
    return json(route, { success: true, data: { items: [apple], totalCount: 1, totalPage: 1 } })
  })
  await page.goto('/#/vocabulary')

  await page.getByRole('textbox', { name: '搜索单词' }).fill('app')
  await page.getByRole('button', { name: '搜索', exact: true }).click()
  await expect.poll(() => requests.at(-1)?.searchParams.get('keyword')).toBe('app')

  await page.locator('.toolbar__book').click()
  await page.locator('.el-select-dropdown__item:visible', { hasText: 'CI Old Book B' }).click()
  await expect.poll(() => requests.at(-1)?.searchParams.get('bookId')).toBe(bookB.id)
  await expect(requests.at(-1)?.searchParams.get('keyword')).toBe('app')
  await expect(requests.at(-1)?.searchParams.get('page')).toBe('1')

  // One word with several meanings stays one row and one count.
  await expect(page.locator('.el-table__row')).toHaveCount(1)
  await expect(page.locator('.el-pagination')).toContainText('共 1 条')
})

test('the book filter searches remotely and pages past its first page', async ({ page }) => {
  const filterRequests: URL[] = []
  const listRequests: URL[] = []
  const firstPage = Array.from({ length: 20 }, (_, index) => ({
    ...bookA,
    id: `filter-book-${index + 1}`,
    bookName: `CI Book ${String(index + 1).padStart(2, '0')}`
  }))
  await mockSession(page)
  await page.route(/\/admin\/vocabulary(\?.*)?$/, (route) => {
    listRequests.push(new URL(route.request().url()))
    return json(route, { success: true, data: libraryPage })
  })
  await page.route(/\/admin\/vocabulary-books(\?.*)?$/, (route) => {
    if (route.request().method() !== 'GET') {
      return route.fallback()
    }

    filterRequests.push(new URL(route.request().url()))
    const pageNumber = Number(new URL(route.request().url()).searchParams.get('page') ?? '1')
    const items = pageNumber === 1 ? firstPage : pageNumber === 2 ? [deepBook] : []
    return json(route, { success: true, data: { items, totalCount: 21, totalPage: 2 } })
  })
  await page.goto('/#/vocabulary')

  await page.locator('.toolbar__book').click()
  const dropdown = page.locator('.el-select-dropdown:visible')
  await expect(dropdown).toContainText('CI Book 01')
  await expect(dropdown.locator('.el-select-dropdown__footer')).toContainText('共 21 本 · 第 1/2 页')
  expect(filterRequests.at(-1)?.searchParams.get('size')).toBe('20')

  // Page two holds the disabled book the first twenty crowded out.
  await dropdown.locator('.el-select-dropdown__footer').getByRole('button', { name: '下一页' }).click()
  await expect.poll(() => filterRequests.at(-1)?.searchParams.get('page')).toBe('2')
  await expect(dropdown).toContainText('CI Deep Book（停用）')

  await dropdown.locator('.el-select-dropdown__item', { hasText: 'CI Deep Book' }).click()
  await expect(page.locator('.toolbar__book')).toContainText('CI Deep Book')
  await expect.poll(() => listRequests.at(-1)?.searchParams.get('bookId')).toBe(deepBook.id)
})

test('a filter failure offers a read-only retry', async ({ page }) => {
  let fail = true
  await mockSession(page)
  await mockLibrary(page)
  await page.route(/\/admin\/vocabulary-books(\?.*)?$/, (route) => fail
    ? json(route, { success: false, message: 'Books are unavailable.' }, 500)
    : json(route, { success: true, data: { items: [bookA], totalCount: 1, totalPage: 1 } }))
  await page.goto('/#/vocabulary')

  await page.locator('.toolbar__book').click()
  const footer = page.locator('.el-select-dropdown:visible').locator('.el-select-dropdown__footer')
  await expect(footer).toContainText('教材列表加载失败')

  fail = false
  await footer.getByRole('button', { name: '重试' }).click()
  await expect(page.locator('.el-select-dropdown:visible')).toContainText('CI Book A')
})

test('opens the same read-only detail drawer from both lists', async ({ page }) => {
  await openLibrary(page)
  await page.locator('.el-table__row', { hasText: 'apple' }).getByRole('button', { name: '详情' }).click()

  const drawer = page.locator('.el-drawer')
  await expect(drawer).toBeVisible()
  await expect(drawer).toContainText('apple')
  await expect(drawer).toContainText('英 /ˈæp.əl/')
  // Shared fields once, meanings grouped per book with their status.
  await expect(drawer.locator('.word-detail__group')).toHaveCount(2)
  await expect(drawer.locator('.word-detail__group', { hasText: 'CI Book A' })).toContainText('启用')
  await expect(drawer.locator('.word-detail__group', { hasText: 'CI Book A' })).toContainText('苹果')
  await expect(drawer.locator('.word-detail__group', { hasText: 'CI Book A' })).toContainText('苹果树')
  await expect(drawer.locator('.word-detail__group', { hasText: 'CI Old Book B' })).toContainText('停用')
  await expect(drawer.locator('.word-detail__group', { hasText: 'CI Old Book B' })).toContainText('一种水果')
  // Editing arrives with its own task; each meaning's deletion goes through
  // the cleanup confirmation flow (covered by the cleanup specification).
  await expect(drawer.getByRole('button', { name: '编辑' })).toHaveCount(0)
  await expect(drawer.getByRole('button', { name: '删除' })).toHaveCount(3)

  await page.keyboard.press('Escape')
  await expect(drawer).not.toBeVisible()
  expect(await focusedText(page)).toBe('详情')

  // The same drawer serves the book word list.
  await openBookWords(page, bookA)
  await page.locator('.el-table__row', { hasText: 'apple' }).getByRole('button', { name: '详情' }).click()
  await expect(page.locator('.el-drawer')).toContainText('一种水果')
  await page.keyboard.press('Escape')
  expect(await focusedText(page)).toBe('详情')
})

test('the drawer separates failure, 404, and an unassigned word', async ({ page }) => {
  await mockSession(page)
  await mockBooksList(page)
  await mockLibrary(page)
  let detailState: 'fail' | 'missing' | 'ok' = 'fail'
  await page.route(/\/admin\/vocabulary\/[^/]+$/, (route) => {
    if (detailState === 'fail') {
      return json(route, { success: false, message: 'Detail is unavailable.' }, 500)
    }

    if (detailState === 'missing') {
      return json(route, { success: false, message: 'No such word.' }, 404)
    }

    return json(route, { success: true, data: legacyDetail })
  })
  await page.goto('/#/vocabulary')

  await page.locator('.el-table__row', { hasText: 'apple' }).getByRole('button', { name: '详情' }).click()
  const drawer = page.locator('.el-drawer')
  await expect(drawer).toContainText('Detail is unavailable.')

  detailState = 'missing'
  await drawer.getByRole('button', { name: '重试' }).click()
  await expect(drawer).toContainText('该单词不存在或已被删除')

  detailState = 'ok'
  await page.locator('.el-drawer__close-btn').click()
  await page.locator('.el-table__row', { hasText: 'legacy' }).getByRole('button', { name: '详情' }).click()
  await expect(drawer).toContainText('无教材归属')
  await expect(drawer).toContainText('该单词暂无释义记录')
})

test('a late answer cannot paint the word that replaced it', async ({ page }) => {
  const detail = useDeferredRoute(page, /\/admin\/vocabulary\/[^/]+$/)
  await mockSession(page)
  await mockBooksList(page)
  await mockLibrary(page)
  await page.goto('/#/vocabulary')

  await page.locator('.el-table__row', { hasText: 'apple' }).getByRole('button', { name: '详情' }).click()
  await page.keyboard.press('Escape')
  await page.locator('.el-table__row', { hasText: 'cherry' }).getByRole('button', { name: '详情' }).click()

  // cherry's request is answered first; apple's late success arrives after a
  // new target owns the drawer and must not replace it.
  detail.answerLast(cherry)
  const drawer = page.locator('.el-drawer')
  await expect(drawer).toContainText('cherry')

  detail.answerNext(appleDetail)
  await page.waitForTimeout(200)
  await expect(drawer).toContainText('cherry')
  await expect(drawer).not.toContainText('苹果树')
})

test('a late book answer cannot paint the book that replaced it', async ({ page }) => {
  const content = useDeferredRoute(page, /\/admin\/vocabulary-books\/[^/]+\/content(\?.*)?$/)
  await mockSession(page)
  await mockBooksList(page)
  await page.goto('/#/books')

  await page.locator('.el-table__row', { hasText: bookA.bookName }).getByRole('button', { name: '查看单词' }).click()
  await expect(page).toHaveURL(new RegExp(`#/books/${bookA.id}/words$`))

  // A same-document switch straight to another book's list reuses the page
  // component; only the generation guard keeps the late answer out.
  await page.goto(`/#/books/${bookB.id}/words`)

  content.answerLast(contentB)
  await expect(page.locator('.page-header__description')).toContainText('CI Old Book B')
  await expect(page.locator('.el-table')).toContainText('banana')

  content.answerNext(contentA)
  await page.waitForTimeout(200)
  await expect(page.locator('.page-header__description')).toContainText('CI Old Book B')
  await expect(page.locator('.el-table')).not.toContainText('苹果树')
})

test('an answer that lands after leaving the page writes nothing', async ({ page }) => {
  const content = useDeferredRoute(page, /\/admin\/vocabulary-books\/[^/]+\/content(\?.*)?$/)
  await mockSession(page)
  await mockBooksList(page)
  await page.goto('/#/books')

  await page.locator('.el-table__row', { hasText: bookA.bookName }).getByRole('button', { name: '查看单词' }).click()
  await expect(page).toHaveURL(new RegExp(`#/books/${bookA.id}/words$`))

  await page.getByRole('button', { name: '返回教材列表' }).click()
  await expect(page).toHaveURL(/#\/books$/)

  // The unmounted page's request is answered late; nothing may throw and the
  // books list stays itself.
  content.answerNext(contentA)
  await page.waitForTimeout(200)
  await expect(page.locator('.page-header h1')).toHaveText('教材管理')

  // Returning loads fresh state for the current target.
  await page.locator('.el-table__row', { hasText: bookA.bookName }).getByRole('button', { name: '查看单词' }).click()
  content.answerNext(contentA)
  await expect(page.locator('.el-table')).toContainText('apple')
})

test('a 401 on the lists keeps the shared redirect', async ({ page }) => {
  await mockSession(page)
  await mockBooksList(page)
  await page.route(/\/admin\/vocabulary(\?.*)?$/, (route) =>
    json(route, { success: false, message: 'Unauthorized' }, 401))
  await page.goto('/#/vocabulary')

  await expect(page).toHaveURL(/#\/login/)
  await expect(page.locator('.app-nav')).toHaveCount(0)
})

for (const width of [1440, 768]) {
  test.describe(`at ${width} px`, () => {
    test.use({ viewport: { width, height: 900 } })

    for (const { name, open } of [
      { name: '/vocabulary', open: openLibrary },
      { name: '/books/:bookId/words', open: (page: Page) => openBookWords(page, bookA) }
    ]) {
      test(`${name} has no WCAG 2.1 AA violations and no sideways scroll`, async ({ page }) => {
        await open(page)
        await page.waitForTimeout(400)

        const results = await new AxeBuilder({ page }).withTags(wcagTags).analyze()
        expect(results.violations).toEqual([])

        const overflow = await page.evaluate(() =>
          document.documentElement.scrollWidth - document.documentElement.clientWidth)
        expect(overflow).toBeLessThanOrEqual(0)
      })
    }

    test('the detail drawer has no WCAG 2.1 AA violations', async ({ page }) => {
      await openLibrary(page)
      await page.locator('.el-table__row', { hasText: 'apple' }).getByRole('button', { name: '详情' }).click()
      await expect(page.locator('.el-drawer')).toBeVisible()
      await page.waitForTimeout(400)

      const results = await new AxeBuilder({ page }).withTags(wcagTags).analyze()
      expect(results.violations).toEqual([])
    })
  })
}
