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

function problemJson(route: Route, data: unknown, status: number) {
  return route.fulfill({
    status,
    contentType: 'application/problem+json',
    body: JSON.stringify(data)
  })
}

async function mockCatalog(page: Page, books = [starterBook]) {
  await page.route('**/admin/system/version', (route) =>
    json(route, {
      success: true,
      data: { version: '1.2.3', revision: null, channel: 'release' }
    }))
  await page.route('**/admin/vocabulary-books/categories', (route) =>
    json(route, { success: true, data: { items: books.length ? ['English'] : [] } }))
  await page.route('**/admin/vocabulary-books/education-levels', (route) =>
    json(route, { success: true, data: { items: books.length ? ['Secondary'] : [] } }))
  await page.route(/\/admin\/vocabulary-books(?:\?.*)?$/, (route) => {
    if (route.request().method() === 'GET') {
      return json(route, {
        success: true,
        data: { items: books, totalCount: books.length, totalPage: books.length ? 1 : 0 }
      })
    }

    return json(route, { success: true, data: { id: starterBook.id } })
  })
}

test('restores an administrator session and displays the catalog', async ({ page }) => {
  await page.route('**/admin/auth/session', (route) =>
    json(route, { success: true, data: admin }))
  await mockCatalog(page)

  await page.goto('/#/books')

  await expect(page.locator('.brand')).toHaveText('Lexarbor')
  await expect(page.locator('.session')).toContainText(admin.username)
  await expect(page.locator('.el-table')).toContainText(starterBook.bookName)
})

test('submits a new catalog book through the administration UI', async ({ page }) => {
  let createPayload: Record<string, unknown> | undefined

  await page.route('**/admin/auth/session', (route) =>
    json(route, { success: true, data: admin }))
  await mockCatalog(page)
  await page.route(/\/admin\/vocabulary-books$/, async (route) => {
    if (route.request().method() !== 'POST') {
      return route.fallback()
    }

    createPayload = route.request().postDataJSON()
    return json(route, { success: true, data: { id: 'new-book-id' } })
  })

  await page.goto('/#/books')
  await page.getByRole('button', { name: '新增教材' }).click()
  await page.locator('.el-dialog input').first().fill('New Browser-Test Book')
  await page.locator('.el-dialog__footer .el-button--primary').click()

  await expect.poll(() => createPayload?.bookName).toBe('New Browser-Test Book')
})

test('shows an empty catalog without errors and creates the first book', async ({ page }) => {
  let createPayload: Record<string, unknown> | undefined

  await page.route('**/admin/auth/session', (route) =>
    json(route, { success: true, data: admin }))
  await mockCatalog(page, [])
  await page.route(/\/admin\/vocabulary-books$/, async (route) => {
    if (route.request().method() !== 'POST') {
      return route.fallback()
    }

    createPayload = route.request().postDataJSON()
    return json(route, { success: true, data: { id: 'first-book-id' } })
  })

  await page.goto('/#/books')

  // Lexarbor ships no vocabulary data, so this is what every new instance shows
  // first. An empty page must read as "no books yet", not as a failure.
  await expect(page.locator('.session')).toContainText(admin.username)
  await expect(page.locator('.el-table__empty-block')).toBeVisible()
  await expect(page.locator('.el-table__body .el-table__row')).toHaveCount(0)
  await expect(page.locator('.el-message--error')).toHaveCount(0)

  await page.getByRole('button', { name: '新增教材' }).click()
  await page.locator('.el-dialog input').first().fill('First Book')
  await page.locator('.el-dialog__footer .el-button--primary').click()

  await expect.poll(() => createPayload?.bookName).toBe('First Book')
  await expect(page.locator('.el-message--error')).toHaveCount(0)
})

/** Opens /books with a list endpoint that answers every GET through `respond`. */
async function openCatalog(page: Page, respond: (route: Route) => Promise<void>) {
  const requests: URL[] = []
  await page.route('**/admin/auth/session', (route) =>
    json(route, { success: true, data: admin }))
  await mockCatalog(page)
  await page.route(/\/admin\/vocabulary-books(?:\?.*)?$/, (route) => {
    if (route.request().method() !== 'GET') {
      return route.fallback()
    }

    requests.push(new URL(route.request().url()))
    return respond(route)
  })

  await page.goto('/#/books')
  await expect(page.locator('.session')).toContainText(admin.username)
  return requests
}

function catalogPage(route: Route, items: unknown[], totalCount = items.length) {
  return json(route, { success: true, data: { items, totalCount, totalPage: Math.ceil(totalCount / 20) } })
}

test('tells an empty catalog apart from a search that matched nothing', async ({ page }) => {
  const requests = await openCatalog(page, (route) => catalogPage(route, []))

  const empty = page.locator('.el-table__empty-block')
  await expect(empty).toContainText('还没有教材，点击「新增教材」创建')

  await page.getByRole('textbox', { name: '搜索教材名称' }).fill('missing')
  await page.getByRole('button', { name: '搜索', exact: true }).click()

  await expect.poll(() => requests.at(-1)?.searchParams.get('keyword')).toBe('missing')
  await expect(empty).toContainText('没有匹配的教材')
  await expect(empty).not.toContainText('还没有教材')
})

test('changing the page size sends one request, for the first page', async ({ page }) => {
  const requests = await openCatalog(page, (route) => catalogPage(route, [starterBook], 100))

  await page.locator('.el-pagination').getByText('3', { exact: true }).click()
  await expect.poll(() => requests.at(-1)?.searchParams.get('page')).toBe('3')
  const before = requests.length

  await expect(page.getByRole('combobox', { name: '每页条数' })).toBeVisible()
  await page.locator('.page-size').click()
  await page.locator('.el-select-dropdown__item:visible', { hasText: '50 条/页' }).click()

  await expect.poll(() => requests.length).toBe(before + 1)
  // A second request could only come from the pagination reacting to its own
  // page count shrinking; give it the chance to happen before asserting.
  await page.waitForTimeout(300)
  expect(requests.length).toBe(before + 1)
  expect(requests.at(-1)?.searchParams.get('size')).toBe('50')
  expect(requests.at(-1)?.searchParams.get('page')).toBe('1')
})

test('shows a failed list inside the page and loads it again on retry', async ({ page }) => {
  let fail = true
  await openCatalog(page, (route) => fail
    ? json(route, { success: false, message: 'Catalog is unavailable.' }, 500)
    : catalogPage(route, [starterBook]))

  const alert = page.locator('.books-error')
  await expect(page.locator('.el-message--error')).toContainText('Catalog is unavailable.')
  await expect(alert).toContainText('Catalog is unavailable.')
  // A failure is not an empty catalog; the empty texts would say otherwise.
  await expect(page.locator('.el-table__empty-block')).not.toContainText('还没有教材')

  fail = false
  await alert.getByRole('button', { name: '重试' }).click()

  await expect(alert).toHaveCount(0)
  await expect(page.locator('.el-table')).toContainText(starterBook.bookName)
})

// Exception-generated failures arrive as ServiceMantle Problem Details while
// endpoint-explicit failures keep the envelope; both must remain readable.
test('shows the Problem Details title of an exception-generated failure', async ({ page }) => {
  await openCatalog(page, (route) => problemJson(route, {
    type: 'urn:servicemantle:error:vocabulary.storage_busy',
    title: 'The vocabulary database is temporarily busy.',
    status: 503,
    correlationId: 'synthetic-correlation-id',
    errorCode: 'vocabulary.storage_busy'
  }, 503))

  const alert = page.locator('.books-error')
  await expect(page.locator('.el-message--error'))
    .toContainText('The vocabulary database is temporarily busy.')
  await expect(alert).toContainText('The vocabulary database is temporarily busy.')
  // The readable title replaces the shape's plumbing fields, and a raw
  // exception text never reaches the page.
  await expect(page.locator('body')).not.toContainText('correlationId')
  await expect(page.locator('body')).not.toContainText('postgresql://')
})
