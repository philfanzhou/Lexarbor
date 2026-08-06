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

async function mockCatalog(page: Page) {
  await page.route('**/admin/vocabulary-books/categories', (route) =>
    json(route, { success: true, data: { items: ['English'] } }))
  await page.route('**/admin/vocabulary-books/education-levels', (route) =>
    json(route, { success: true, data: { items: ['Secondary'] } }))
  await page.route(/\/admin\/vocabulary-books(?:\?.*)?$/, (route) => {
    if (route.request().method() === 'GET') {
      return json(route, {
        success: true,
        data: { items: [starterBook], totalCount: 1, totalPage: 1 }
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

test('logs in without exposing credentials in the browser URL', async ({ page }) => {
  let loginPayload: unknown

  await page.route('**/admin/auth/session', (route) =>
    json(route, { success: false, message: 'Unauthorized' }, 401))
  await page.route('**/admin/auth/login', async (route) => {
    loginPayload = route.request().postDataJSON()
    return json(route, { success: true, data: admin })
  })
  await mockCatalog(page)

  await page.goto('/#/books')
  await page.locator('input[autocomplete="username"]').fill('ci-admin')
  await page.locator('input[autocomplete="current-password"]').fill('correct-horse')
  await page.locator('.auth-card__action').click()

  await expect(page).toHaveURL(/#\/books$/)
  await expect(page.locator('.session')).toContainText(admin.username)
  expect(loginPayload).toEqual({ username: 'ci-admin', password: 'correct-horse' })
  expect(page.url()).not.toContain('correct-horse')
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
  await page.locator('.toolbar button').nth(1).click()
  await page.locator('.el-dialog input').first().fill('New Browser-Test Book')
  await page.locator('.el-dialog__footer .el-button--primary').click()

  await expect.poll(() => createPayload?.bookName).toBe('New Browser-Test Book')
})
