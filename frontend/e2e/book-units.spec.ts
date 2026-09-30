import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page, type Route } from '@playwright/test'

const admin = {
  username: 'ci-admin',
  roles: ['admin']
}

const bookA = {
  id: '11111111-1111-1111-1111-111111111111',
  bookName: 'CI Book A',
  description: 'Unit test fixture',
  publisher: 'Lexarbor',
  educationLevel: 'Secondary',
  grade: 'Grade 7',
  category: 'English',
  displayOrder: 1,
  status: true,
  iconUrl: ''
}

const legacyBook = {
  ...bookA,
  id: '44444444-4444-4444-4444-444444444444',
  bookName: 'Starter English 300',
  displayOrder: 4,
  status: false
}

const unitsA = [
  { id: 'unit-1', bookId: bookA.id, number: 1, title: 'Starter', meaningCount: 3 },
  { id: 'unit-2', bookId: bookA.id, number: 2, title: null, meaningCount: 0 }
]

const unitsLegacy = [
  { id: 'unit-l1', bookId: legacyBook.id, number: 1, title: 'Word List', meaningCount: 12 }
]

const wcagTags = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']

/**
 * Fresh copies of the shared fixtures: the write mocks below edit the state
 * they serve, and mutating the module-level rows would leak one test's edits
 * into the next test's list.
 */
function cloneUnits(units: typeof unitsA) {
  return units.map(unit => ({ ...unit }))
}

function json(route: Route, data: unknown, status = 200) {
  return route.fulfill({
    status,
    contentType: 'application/json',
    body: JSON.stringify(data)
  })
}

/** The server's envelope: `data` on success, a top-level `message` on failure. */
function envelope(route: Route, data: unknown, status = 200) {
  if (status < 400) {
    return json(route, { success: true, data }, status)
  }

  const message = (data as { message?: string } | null)?.message ?? 'Request failed.'
  return json(route, { success: false, message }, status)
}

interface Recorded {
  url: URL
  method: string
  body: unknown
}

/** Answers the unit list reads from mutable state and records every write. */
function useUnits(page: Page, state: { units: typeof unitsA }) {
  const requests: Recorded[] = []
  let reads = 0
  void page.route(/\/admin\/vocabulary-books\/[^/]+\/units(\/[^/]+)?$/, (route) => {
    const url = new URL(route.request().url())
    const method = route.request().method()
    if (method === 'GET') {
      reads += 1
      const bookId = url.pathname.split('/')[3]
      return json(route, { success: true, data: { units: state.units.filter(unit => unit.bookId === bookId) } })
    }

    requests.push({ url, method, body: route.request().postDataJSON() })
    const bookId = url.pathname.split('/')[3]
    if (method === 'POST') {
      // The write persists server-side, so the next list read serves it.
      const created = {
        id: 'unit-new',
        bookId,
        number: (route.request().postDataJSON() as { number: number }).number,
        title: (route.request().postDataJSON() as { title: string | null }).title,
        meaningCount: 0
      }
      state.units.push(created)
      return envelope(route, created)
    }

    if (method === 'PUT') {
      const number = (route.request().postDataJSON() as { number: number }).number
      const title = (route.request().postDataJSON() as { title: string | null }).title
      const edited = state.units.find(unit => unit.id === url.pathname.split('/').at(-1))
      if (edited) {
        edited.number = number
        edited.title = title
      }

      return envelope(route, { id: url.pathname.split('/').at(-1), bookId, number, title, meaningCount: 3 })
    }

    if (method === 'DELETE') {
      const unitId = url.pathname.split('/').at(-1) as string
      state.units = state.units.filter(unit => unit.id !== unitId)
    }

    return envelope(route, { success: true })
  })
  // A getter, so the count the handler increments stays visible to the test.
  return { requests, get reads() { return reads } }
}

async function mockBooksList(page: Page, books = [bookA, legacyBook]) {
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

async function openBooks(page: Page, books = [bookA, legacyBook]) {
  await page.route('**/admin/auth/session', (route) =>
    json(route, { success: true, data: admin }))
  await page.route('**/admin/auth/method', (route) =>
    json(route, { success: true, data: { method: 'password' } }))
  await mockBooksList(page, books)
  await page.goto('/#/books')
  await expect(page.locator('.session')).toContainText(admin.username)
}

const dialog = (page: Page) => page.locator('.unit-dialog')

const dialogOf = (page: Page, book: typeof bookA) =>
  page.locator('.el-table__row', { hasText: book.bookName }).getByRole('button', { name: '单元' })

async function openUnits(page: Page, book = bookA) {
  await dialogOf(page, book).click()
  await expect(dialog(page)).toBeVisible()
}

test('lists a book\'s units with their assignment counts', async ({ page }) => {
  await openBooks(page)
  const state = { units: cloneUnits(unitsA) }
  useUnits(page, state)

  await openUnits(page)
  await expect(dialog(page)).toContainText(`单元管理：${bookA.bookName}`)
  await expect(dialog(page).locator('.el-table__row', { hasText: '1' })).toContainText('Starter')
  await expect(dialog(page).locator('.el-table__row', { hasText: '1' })).toContainText('3')
  await expect(dialog(page).locator('.el-table__row', { hasText: '2' })).toContainText('—')
})

test('adds a unit through the form and refreshes the list', async ({ page }) => {
  await openBooks(page)
  const state = { units: cloneUnits(unitsA) }
  const unitsRoute = useUnits(page, state)

  await openUnits(page)
  await dialog(page).getByRole('spinbutton').fill('3')
  await dialog(page).getByRole('textbox').fill(' Daily Life ')
  await dialog(page).getByRole('button', { name: '添加单元' }).click()

  expect(unitsRoute.requests).toHaveLength(1)
  expect(unitsRoute.requests[0].method).toBe('POST')
  expect(unitsRoute.requests[0].url.pathname).toBe(`/admin/vocabulary-books/${bookA.id}/units`)
  // A replace body: both fields, the title trimmed, never merged.
  expect(unitsRoute.requests[0].body).toEqual({ number: 3, title: 'Daily Life' })

  await expect(page.locator('.el-message--success')).toContainText('单元已添加')
  await expect(dialog(page).locator('.el-table__row')).toHaveCount(3)
  await expect.poll(() => unitsRoute.reads).toBeGreaterThanOrEqual(2)
})

test('edits a unit by replacing its number and title', async ({ page }) => {
  await openBooks(page)
  const state = { units: cloneUnits(unitsA) }
  const { requests } = useUnits(page, state)

  await openUnits(page)
  await dialog(page).locator('.el-table__row', { hasText: 'Starter' }).getByRole('button', { name: '编辑' }).click()
  await expect(dialog(page)).toContainText('编辑单元 1')
  await expect(dialog(page).getByRole('spinbutton')).toHaveValue('1')
  await expect(dialog(page).getByRole('textbox')).toHaveValue('Starter')

  await dialog(page).getByRole('spinbutton').fill('4')
  await dialog(page).getByRole('textbox').fill('Everyday English')
  await dialog(page).getByRole('button', { name: '保存修改' }).click()

  expect(requests).toHaveLength(1)
  expect(requests[0].method).toBe('PUT')
  expect(requests[0].url.pathname).toBe(`/admin/vocabulary-books/${bookA.id}/units/unit-1`)
  expect(requests[0].body).toEqual({ number: 4, title: 'Everyday English' })
  await expect(page.locator('.el-message--success')).toContainText('单元已更新')
})

test('a duplicate number is refused with the server reason and keeps the draft', async ({ page }) => {
  await openBooks(page)
  const state = { units: cloneUnits(unitsA) }
  const unitsRoute = useUnits(page, state)
  await page.route(/\/admin\/vocabulary-books\/[^/]+\/units$/, (route) => {
    if (route.request().method() !== 'POST') {
      return route.fallback()
    }

    return envelope(route, { message: 'A unit with the same number already exists in this vocabulary book.' }, 409)
  })

  await openUnits(page)
  await dialog(page).getByRole('spinbutton').fill('1')
  await dialog(page).getByRole('textbox').fill('Duplicate')
  await dialog(page).getByRole('button', { name: '添加单元' }).click()

  const error = dialog(page).locator('.el-alert--error').last()
  await expect(error).toContainText('A unit with the same number already exists in this vocabulary book.')
  await expect(error).toContainText('刷新列表')
  // The refused write keeps the entered values for a corrected retry.
  await expect(dialog(page).getByRole('spinbutton')).toHaveValue('1')
  await expect(dialog(page).getByRole('textbox')).toHaveValue('Duplicate')
  expect(unitsRoute.requests).toHaveLength(0)

  await error.getByRole('button', { name: '刷新列表' }).click()
  await expect.poll(() => unitsRoute.reads).toBeGreaterThanOrEqual(2)
})

test('deleting a unit confirms its impact and refreshes the list', async ({ page }) => {
  await openBooks(page)
  const state = { units: cloneUnits(unitsA) }
  const unitsRoute = useUnits(page, state)

  await openUnits(page)
  await dialog(page).locator('.el-table__row', { hasText: 'Starter' }).getByRole('button', { name: '删除' }).click()

  const confirm = page.locator('.el-message-box')
  await expect(confirm).toContainText('移除该单元及其 3 条词义归属')
  await expect(confirm).toContainText('词义、共享词条和其他单元的归属会保留')
  // The server state changes with the delete; the refresh that follows must
  // serve it, so the fixture switches before the request that observes it.
  state.units.splice(0, 1)
  await confirm.getByRole('button', { name: '确定' }).click()

  expect(unitsRoute.requests).toHaveLength(1)
  expect(unitsRoute.requests[0].method).toBe('DELETE')
  expect(unitsRoute.requests[0].url.pathname).toBe(`/admin/vocabulary-books/${bookA.id}/units/unit-1`)
  await expect(page.locator('.el-message--success')).toContainText('单元已删除')
  await expect(dialog(page).locator('.el-table__row')).toHaveCount(1)
})

test('a failed list load offers a retry that works', async ({ page }) => {
  await openBooks(page)
  const state = { units: cloneUnits(unitsA) }
  useUnits(page, state)
  let broken = true
  await page.route(/\/admin\/vocabulary-books\/[^/]+\/units(\?.*)?$/, (route) => {
    if (route.request().method() !== 'GET') {
      return route.fallback()
    }

    if (broken) {
      return route.abort('failed')
    }

    const bookId = new URL(route.request().url()).pathname.split('/')[3]
    return json(route, { success: true, data: { units: state.units.filter(unit => unit.bookId === bookId) } })
  })

  await openUnits(page)
  const error = dialog(page).locator('.el-alert--error')
  await expect(error).toContainText('网络异常，未能取得单元列表')

  broken = false
  await error.getByRole('button', { name: '重试' }).click()
  await expect(dialog(page).locator('.el-table__row')).toHaveCount(2)
})

test('a disabled book offers the same unit entry', async ({ page }) => {
  await openBooks(page)
  const state = { units: cloneUnits(unitsLegacy) }
  useUnits(page, state)

  // The disabled legacy book's row offers 单元 like every other row.
  await openUnits(page, legacyBook)
  await expect(dialog(page)).toContainText(`单元管理：${legacyBook.bookName}`)
  await expect(dialog(page).locator('.el-table__row')).toHaveCount(1)
  await expect(dialog(page).locator('.el-table__row')).toContainText('Word List')
  await expect(dialog(page).locator('.el-table__row')).toContainText('12')
})

test('a book deleted elsewhere reports the way back instead of an empty list', async ({ page }) => {
  await openBooks(page)
  await page.route(/\/admin\/vocabulary-books\/[^/]+\/units(\?.*)?$/, (route) =>
    envelope(route, { message: 'Vocabulary book was not found.' }, 404))

  await openUnits(page)
  await expect(dialog(page).locator('.el-alert')).toContainText('目标教材不存在或已被删除')
  await expect(dialog(page).locator('.el-alert')).toContainText('返回教材列表')
  await dialog(page).getByRole('button', { name: '关闭', exact: true }).click()
  await expect(dialog(page)).not.toBeVisible()
})

for (const width of [1440, 768]) {
  test.describe(`at ${width} px`, () => {
    test.use({ viewport: { width, height: 900 } })

    test('the unit dialog is accessible', async ({ page }) => {
      await openBooks(page)
      const state = { units: cloneUnits(unitsA) }
      useUnits(page, state)

      await openUnits(page)
      await expect(dialog(page).locator('.el-table__row')).toHaveCount(2)
      await page.waitForTimeout(400)

      const results = await new AxeBuilder({ page }).withTags(wcagTags).analyze()
      expect(results.violations).toEqual([])
      const overflow = await page.evaluate(() =>
        document.documentElement.scrollWidth - document.documentElement.clientWidth)
      expect(overflow).toBeLessThanOrEqual(0)
    })
  })
}
