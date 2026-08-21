import { expect, test, type Page, type Route } from '@playwright/test'

const admin = {
  username: 'ci-admin',
  roles: ['admin']
}

const starterBook = {
  id: '11111111-1111-1111-1111-111111111111',
  bookName: 'CI Starter Book',
  description: 'Browser test fixture',
  publisher: 'Lexarbor',
  educationLevel: 'Secondary',
  grade: 'Grade 7',
  category: 'English',
  displayOrder: 1,
  status: true,
  iconUrl: ''
}

function json(route: Route, data: unknown, status = 200) {
  return route.fulfill({
    status,
    contentType: 'application/json',
    body: JSON.stringify(data)
  })
}

/** The public catalogue endpoint the book picker reads. */
const booksRoute = /\/api\/vocabulary-books\/all$/

/**
 * The import page needs an administrator session and the book list that fills
 * its first select. Everything else is per-test, because what these tests are
 * about is how one POST response is presented.
 */
async function openImportPage(page: Page, books = [starterBook]) {
  await page.route('**/admin/auth/session', (route) =>
    json(route, { success: true, data: admin }))
  await page.route(booksRoute, (route) => json(route, { success: true, data: { books } }))

  await page.goto('/#/import')
  await expect(page.locator('.session')).toContainText(admin.username)
}

async function selectBook(page: Page) {
  await page.locator('.el-form-item').first().locator('.el-select').click()
  await page.locator('.el-select-dropdown__item').first().click()
}

/** Fills only the three fields the form marks required. */
async function fillRequiredFields(page: Page) {
  await selectBook(page)
  await page.getByPlaceholder('如：apple').fill('apple')
  await page.getByPlaceholder('如：苹果').fill('苹果')
}

function submit(page: Page) {
  return page.locator('.el-form-item').last().locator('button').first().click()
}

/**
 * Answers the import POST with one status, so a test states only the status it
 * is about. The body carries a distinctive server message, which lets each test
 * assert whether the page showed its own text or passed the server's through.
 */
async function respondToImport(page: Page, status: number, message: string) {
  await page.route(/\/admin\/vocabulary$/, (route) =>
    route.request().method() === 'POST'
      ? json(route, { success: false, message }, status)
      : route.fallback())
}

test('sends the filled form as a nested word and meaning payload', async ({ page }) => {
  let importPayload: Record<string, unknown> | undefined

  await openImportPage(page)
  await page.route(/\/admin\/vocabulary$/, async (route) => {
    if (route.request().method() !== 'POST') {
      return route.fallback()
    }

    importPayload = route.request().postDataJSON()
    return json(route, { success: true, data: { success: true } })
  })

  await fillRequiredFields(page)
  await page.getByPlaceholder('如：/ˈæp.əl/').first().fill('/ˈæp.əl/')
  await page.getByPlaceholder('如：I eat an apple.').fill('I eat an apple.')
  await submit(page)

  await expect(page.locator('.el-message--success')).toContainText('导入成功')
  // The word and the meaning are separate objects, and the meaning carries the
  // book. Flattening them would still look correct on screen and would be
  // rejected only by the server.
  await expect.poll(() => importPayload).toEqual({
    word: { word: 'apple', phoneticUk: '/ˈæp.əl/' },
    meaning: {
      bookId: starterBook.id,
      meaning: '苹果',
      example: 'I eat an apple.'
    }
  })
})

test('omits the optional fields left blank rather than sending empty strings', async ({ page }) => {
  let importPayload: Record<string, unknown> | undefined

  await openImportPage(page)
  await page.route(/\/admin\/vocabulary$/, async (route) => {
    if (route.request().method() !== 'POST') {
      return route.fallback()
    }

    importPayload = route.request().postDataJSON()
    return json(route, { success: true, data: { success: true } })
  })

  await fillRequiredFields(page)
  await submit(page)

  // An empty string is a value the server would store; a missing key is not.
  // Both look identical in the form, which is why this is asserted here.
  await expect.poll(() => importPayload).toEqual({
    word: { word: 'apple' },
    meaning: { bookId: starterBook.id, meaning: '苹果' }
  })
})

test('clears the form after a successful import', async ({ page }) => {
  await openImportPage(page)
  await page.route(/\/admin\/vocabulary$/, (route) =>
    route.request().method() === 'POST'
      ? json(route, { success: true, data: { success: true } })
      : route.fallback())

  await fillRequiredFields(page)
  await submit(page)

  await expect(page.locator('.el-message--success')).toBeVisible()
  // Importing a list means submitting repeatedly. A form that keeps the last
  // word is how the same entry gets imported twice.
  await expect(page.getByPlaceholder('如：apple')).toHaveValue('')
  await expect(page.getByPlaceholder('如：苹果')).toHaveValue('')
})

test('does not submit while a required field is empty', async ({ page }) => {
  let importAttempted = false

  await openImportPage(page)
  await page.route(/\/admin\/vocabulary$/, (route) => {
    importAttempted = true
    return json(route, { success: true, data: { success: true } })
  })

  await selectBook(page)
  await page.getByPlaceholder('如：apple').fill('apple')
  await submit(page)

  await expect(page.locator('.el-form-item__error')).toContainText('请输入释义')
  expect(importAttempted).toBe(false)
})

/**
 * The reason this file exists. Three statuses are translated into a specific
 * instruction, and anything else falls through to the server's message. The
 * mapping lives only in the component, so a server that changes which status it
 * answers would leave the page confidently showing the wrong advice, with
 * nothing failing.
 */
const mappedStatuses = [
  { status: 404, expected: '所选教材不存在，请刷新后重新选择' },
  { status: 409, expected: '该单词或词义与现有数据冲突' },
  { status: 422, expected: '导入内容不符合业务规则' }
]

for (const { status, expected } of mappedStatuses) {
  test(`explains a ${status} in its own words`, async ({ page }) => {
    await openImportPage(page)
    await respondToImport(page, status, 'Server envelope message.')

    await fillRequiredFields(page)
    await submit(page)

    await expect(page.locator('.el-message--error')).toContainText(expected)
    // The envelope message is written for an API caller. Showing it here would
    // put an English sentence in front of an administrator using a Chinese UI.
    await expect(page.locator('.el-message--error')).not.toContainText('Server envelope message.')
  })
}

test('passes an unmapped status through with the server message', async ({ page }) => {
  await openImportPage(page)
  await respondToImport(page, 400, 'Word and Meaning are required.')

  await fillRequiredFields(page)
  await submit(page)

  // A status the mapping does not know must not be silently generic: the server
  // is the only thing that knows why a 400 happened.
  await expect(page.locator('.el-message--error')).toContainText('Word and Meaning are required.')
})

test('reports a failure envelope returned with a 200', async ({ page }) => {
  await openImportPage(page)
  await page.route(/\/admin\/vocabulary$/, (route) =>
    route.request().method() === 'POST'
      ? json(route, { success: false, message: 'Refused inside a 200.' })
      : route.fallback())

  await fillRequiredFields(page)
  await submit(page)

  // success:false carries the failure, not the HTTP status. Reading only the
  // status here would report a refused import as a successful one.
  await expect(page.locator('.el-message--error')).toContainText('Refused inside a 200.')
  await expect(page.locator('.el-message--success')).toHaveCount(0)
})

test('surfaces a failure to load the book list', async ({ page }) => {
  await page.route('**/admin/auth/session', (route) =>
    json(route, { success: true, data: admin }))
  await page.route(booksRoute, (route) =>
    json(route, { success: false, message: 'Books are unavailable.' }, 500))

  await page.goto('/#/import')

  // Without this the select is simply empty, which reads as "no books exist"
  // rather than "the list could not be loaded".
  await expect(page.locator('.el-message--error')).toContainText('Books are unavailable.')
})

test('offers every book, past the page the administration search would have returned', async ({ page }) => {
  const books = Array.from({ length: 25 }, (_, index) => ({
    ...starterBook,
    id: `book-${index + 1}`,
    bookName: `CI Book ${index + 1}`,
    displayOrder: index + 1
  }))
  let importPayload: { meaning?: { bookId?: string } } | undefined

  await openImportPage(page, books)
  await page.route(/\/admin\/vocabulary$/, async (route) => {
    if (route.request().method() !== 'POST') {
      return route.fallback()
    }

    importPayload = route.request().postDataJSON()
    return json(route, { success: true, data: { success: true } })
  })

  await page.locator('.el-form-item').first().locator('.el-select').click()
  // Scoped by name because the form has a second select, for part of speech,
  // whose options share the class.
  const bookOptions = page.locator('.el-select-dropdown__item', {
    hasText: /^\s*CI Book \d+\s*$/
  })
  await expect(bookOptions).toHaveCount(25)

  const lastBook = bookOptions.filter({ hasText: /^\s*CI Book 25\s*$/ })
  await lastBook.scrollIntoViewIfNeeded()
  await lastBook.click()
  await page.getByPlaceholder('如：apple').fill('apple')
  await page.getByPlaceholder('如：苹果').fill('苹果')
  await submit(page)

  // The picker used to read the paged administration search with no paging
  // parameters at all, which that endpoint rejects as a malformed request, so
  // against a real server the select was empty and the page showed "The request
  // is invalid." Supplying a page would have replaced that with a silently short
  // list, which is why the picker reads an unpaged endpoint rather than a
  // corrected call. Asserting the import carries the twenty-fifth book rather
  // than that the option exists is the difference between listed and usable.
  await expect.poll(() => importPayload?.meaning?.bookId).toBe('book-25')
})
