import AxeBuilder from '@axe-core/playwright'
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

const wcagTags = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']
const widths = [1440, 768]

const navLinks = [
  { name: '教材管理', path: '/books' },
  { name: '单条导入', path: '/import' },
  { name: '批量导入', path: '/import/batch' }
]

function json(route: Route, data: unknown, status = 200) {
  return route.fulfill({
    status,
    contentType: 'application/json',
    body: JSON.stringify(data)
  })
}

/** Answers everything the three administration pages read on load. */
async function mockAdministrator(page: Page) {
  await page.route('**/admin/auth/session', (route) =>
    json(route, { success: true, data: admin }))
  await page.route('**/admin/vocabulary-books/categories', (route) =>
    json(route, { success: true, data: { items: ['English'] } }))
  await page.route('**/admin/vocabulary-books/education-levels', (route) =>
    json(route, { success: true, data: { items: ['Secondary'] } }))
  await page.route(/\/admin\/vocabulary-books(?:\?.*)?$/, (route) =>
    json(route, { success: true, data: { items: [starterBook], totalCount: 1, totalPage: 1 } }))
  await page.route(/\/api\/vocabulary-books\/all$/, (route) =>
    json(route, { success: true, data: { books: [starterBook] } }))
}

/** `setUp` runs after the default mocks, so its routes take precedence. */
async function openAdministration(page: Page, path: string, setUp?: (page: Page) => Promise<unknown>) {
  await mockAdministrator(page)
  await setUp?.(page)
  await page.goto(`/#${path}`)
  await expect(page.locator('.session')).toContainText(admin.username)
  await expect(page.locator('.page-header h1')).toBeVisible()
}

async function openPublicPage(page: Page, path: '/login' | '/forbidden') {
  await page.route('**/admin/auth/session', (route) =>
    json(route, { success: false, message: 'Unauthorized' }, 401))
  await page.goto(`/#${path}`)
  await expect(page.locator('.auth-card')).toBeVisible()
}

function navigation(page: Page) {
  return page.getByRole('navigation', { name: '主导航' })
}

async function focusedText(page: Page) {
  return page.evaluate(() => {
    const element = document.activeElement as HTMLElement | null
    return element?.getAttribute('aria-label') ?? element?.innerText.trim() ?? ''
  })
}

for (const width of widths) {
  test.describe(`at ${width} px`, () => {
    test.use({ viewport: { width, height: 900 } })

    for (const path of ['/login', '/forbidden'] as const) {
      test(`${path} has no WCAG 2.1 AA violations`, async ({ page }) => {
        await openPublicPage(page, path)

        const results = await new AxeBuilder({ page }).withTags(wcagTags).analyze()

        expect(results.violations).toEqual([])
      })
    }

    for (const { path } of navLinks) {
      test(`the shell on ${path} has no WCAG 2.1 AA violations`, async ({ page }) => {
        await openAdministration(page, path)

        // Only the header and the navigation; the redesigned pages are also
        // checked in full below.
        const results = await new AxeBuilder({ page })
          .include('.app-header')
          .include('.app-nav')
          .withTags(wcagTags)
          .analyze()

        expect(results.violations).toEqual([])
      })
    }

    const emptyCatalog = (page: Page) =>
      page.route(/\/admin\/vocabulary-books(?:\?.*)?$/, (route) =>
        json(route, { success: true, data: { items: [], totalCount: 0, totalPage: 0 } }))

    const pageStates: { name: string, path: string, setUp?: (page: Page) => Promise<unknown>, act?: (page: Page) => Promise<void> }[] = [
      { name: '/books with books', path: '/books' },
      {
        name: '/books with no book',
        path: '/books',
        setUp: emptyCatalog,
        act: async (page) => {
          await expect(page.locator('.el-table__empty-block .el-empty')).toBeVisible()
        }
      },
      {
        name: '/books with the new book dialog open',
        path: '/books',
        act: async (page) => {
          await page.getByRole('button', { name: '新增教材' }).click()
          await expect(page.locator('.el-dialog')).toBeVisible()
        }
      },
      { name: '/import', path: '/import' },
      {
        name: '/import after a successful import',
        path: '/import',
        setUp: (page) => page.route(/\/admin\/vocabulary$/, (route) =>
          json(route, { success: true, data: { success: true } })),
        act: async (page) => {
          await page.locator('.el-form-item').first().locator('.el-select').click()
          await page.locator('.el-select-dropdown__item').first().click()
          await page.getByPlaceholder('如：apple').fill('apple')
          await page.getByPlaceholder('如：苹果').fill('苹果')
          await page.getByRole('button', { name: '导入', exact: true }).click()
          await expect(page.locator('.el-alert--success')).toContainText('已导入「apple」')
        }
      }
    ]

    for (const { name, path, setUp, act } of pageStates) {
      test(`${name} has no WCAG 2.1 AA violations`, async ({ page }) => {
        await openAdministration(page, path, setUp)
        await act?.(page)
        // Let transitions (dialog, alert, loading mask) settle before measuring
        // colours.
        await page.waitForTimeout(400)

        const results = await new AxeBuilder({ page }).withTags(wcagTags).analyze()

        expect(results.violations).toEqual([])
      })
    }

    test('Tab reaches the logout button and then each navigation link, with a visible focus ring', async ({ page }) => {
      await openAdministration(page, '/books')

      await page.keyboard.press('Tab')
      expect(await focusedText(page)).toBe('退出登录')

      for (const { name } of navLinks) {
        await page.keyboard.press('Tab')
        expect(await focusedText(page)).toBe(name)
        const outline = await page.evaluate(() => getComputedStyle(document.activeElement as Element).outlineStyle)
        expect(outline).not.toBe('none')
      }
    })
  })
}

test.describe('at 768 px', () => {
  test.use({ viewport: { width: 768, height: 900 } })

  for (const path of ['/login', '/forbidden', '/books', '/import', '/import/batch']) {
    test(`${path} does not scroll sideways`, async ({ page }) => {
      if (path === '/login' || path === '/forbidden') {
        await openPublicPage(page, path)
      } else {
        await openAdministration(page, path)
      }

      const overflow = await page.evaluate(() =>
        document.documentElement.scrollWidth - document.documentElement.clientWidth)
      expect(overflow).toBeLessThanOrEqual(0)
    })
  }

  test('the navigation collapses to icons that keep their names', async ({ page }) => {
    await openAdministration(page, '/books')

    for (const { name } of navLinks) {
      const link = navigation(page).getByRole('link', { name })
      await expect(link).toHaveAttribute('title', name)
      await expect(link.locator('.app-nav__text')).toHaveCSS('position', 'absolute')
    }
  })
})

test('each navigation link opens its page and only the current one is marked', async ({ page }) => {
  await openAdministration(page, '/books')

  for (const { name, path } of [...navLinks].reverse()) {
    await navigation(page).getByRole('link', { name }).click()

    await expect(page).toHaveURL(new RegExp(`#${path}$`))
    for (const other of navLinks) {
      const link = navigation(page).getByRole('link', { name: other.name })
      if (other.path === path) {
        await expect(link).toHaveAttribute('aria-current', 'page')
      } else {
        await expect(link).not.toHaveAttribute('aria-current')
      }
    }
  }
})

test('logging out posts to the logout endpoint and returns to the login page', async ({ page }) => {
  let logoutMethod: string | undefined
  await page.route('**/admin/auth/logout', (route) => {
    logoutMethod = route.request().method()
    return json(route, { success: true })
  })
  await openAdministration(page, '/books')

  await page.locator('.session').getByRole('button', { name: '退出登录' }).click()

  await expect(page).toHaveURL(/#\/login$/)
  expect(logoutMethod).toBe('POST')
  await expect(page.locator('.app-nav')).toHaveCount(0)
})

test('a failed logout shows the error and still returns to the login page', async ({ page }) => {
  await page.route('**/admin/auth/logout', (route) =>
    json(route, { success: false, message: 'Logout is unavailable.' }, 500))
  await openAdministration(page, '/books')

  await page.locator('.session').getByRole('button', { name: '退出登录' }).click()

  await expect(page.locator('.el-message--error')).toContainText('Logout is unavailable.')
  await expect(page).toHaveURL(/#\/login$/)
  await expect(page.locator('.app-nav')).toHaveCount(0)
})

test('Element Plus speaks Chinese in the pagination and the confirmation box', async ({ page }) => {
  await openAdministration(page, '/books')

  const pagination = page.locator('.el-pagination')
  await expect(pagination).toContainText('共')
  await expect(pagination).toContainText('前往')

  await page.locator('.el-table').getByRole('button', { name: '删除' }).click()
  const box = page.locator('.el-message-box')
  await expect(box.getByRole('button', { name: '确定' })).toBeVisible()
  await expect(box.getByRole('button', { name: '取消' })).toBeVisible()
  await box.getByRole('button', { name: '取消' }).click()
})
