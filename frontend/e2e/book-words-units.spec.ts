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

const refA = { id: bookA.id, bookName: bookA.bookName, status: bookA.status }
const refB = { id: bookB.id, bookName: bookB.bookName, status: bookB.status }

// Apple's one meaning is assigned to units 2 and 6 both; bank carries two
// meanings, one per unit. Unit 9 has no assignments.
const appleMeaning = {
  id: 'meaning-apple',
  vocabularyId: 'word-apple',
  bookId: bookA.id,
  partOfSpeech: 'n.',
  meaning: '苹果',
  example: null,
  units: [
    { unitId: 'unit-2', number: 2, title: 'School Life' },
    { unitId: 'unit-6', number: 6, title: null }
  ]
}
const apple = { id: 'word-apple', word: 'apple', phoneticUk: '/ˈæp.əl/', phoneticUs: null, books: [refA, refB] }
const bank = { id: 'word-bank', word: 'bank', phoneticUk: null, phoneticUs: null, books: [refA] }
const bankRiver = {
  id: 'meaning-bank-river', vocabularyId: 'word-bank', bookId: bookA.id,
  partOfSpeech: 'n.', meaning: '河岸', example: null,
  units: [{ unitId: 'unit-2', number: 2, title: 'School Life' }]
}
const bankMoney = {
  id: 'meaning-bank-money', vocabularyId: 'word-bank', bookId: bookA.id,
  partOfSpeech: 'n.', meaning: '银行', example: null,
  units: [{ unitId: 'unit-6', number: 6, title: null }]
}
const banana = { id: 'word-banana', word: 'banana', phoneticUk: null, phoneticUs: null, books: [refB] }
const bananaMeaning = {
  id: 'meaning-banana', vocabularyId: 'word-banana', bookId: bookB.id,
  partOfSpeech: 'n.', meaning: '香蕉', example: null,
  units: [{ unitId: 'unit-b1', number: 1, title: 'Greetings' }]
}

const unitsA = [
  { id: 'unit-2', bookId: bookA.id, number: 2, title: 'School Life', meaningCount: 2 },
  { id: 'unit-6', bookId: bookA.id, number: 6, title: null, meaningCount: 2 },
  { id: 'unit-9', bookId: bookA.id, number: 9, title: 'Empty Unit', meaningCount: 0 }
]
const unitsB = [
  { id: 'unit-b1', bookId: bookB.id, number: 1, title: 'Greetings', meaningCount: 1 }
]

const contentA = {
  book: bookA,
  wordCount: 2,
  meaningCount: 3,
  items: [
    { ...apple, meanings: [appleMeaning] },
    { ...bank, meanings: [bankRiver, bankMoney] }
  ],
  totalCount: 2,
  totalPage: 1
}

function unitPage(unit: { id: string; number: number; title: string | null }, items: unknown[], counts = { wordCount: 0, meaningCount: 0, totalCount: 0, totalPage: 0 }) {
  return {
    book: bookA,
    unit: { id: unit.id, bookId: bookA.id, number: unit.number, title: unit.title },
    wordCount: counts.wordCount,
    meaningCount: counts.meaningCount,
    items,
    totalCount: counts.totalCount,
    totalPage: counts.totalPage
  }
}

const unit2Content = unitPage(
  unitsA[0],
  [
    { ...apple, meanings: [appleMeaning] },
    { ...bank, meanings: [bankRiver] }
  ],
  { wordCount: 2, meaningCount: 2, totalCount: 2, totalPage: 1 }
)
const unit6Content = unitPage(
  unitsA[1],
  [
    { ...apple, meanings: [appleMeaning] },
    { ...bank, meanings: [bankMoney] }
  ],
  { wordCount: 2, meaningCount: 2, totalCount: 2, totalPage: 1 }
)
const unit9Content = unitPage(unitsA[2], [])
const unitB1Content = {
  book: bookB,
  unit: { id: 'unit-b1', bookId: bookB.id, number: 1, title: 'Greetings' },
  wordCount: 1,
  meaningCount: 1,
  items: [{ ...banana, meanings: [bananaMeaning] }],
  totalCount: 1,
  totalPage: 1
}

const wordDetail = {
  ...apple,
  meanings: [appleMeaning]
}

const wcagTags = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']

function json(route: Route, data: unknown, status = 200) {
  return route.fulfill({
    status,
    contentType: 'application/json',
    body: JSON.stringify(data)
  })
}

const bookContentRoute = /\/admin\/vocabulary-books\/[^/]+\/content(\?.*)?$/
const unitContentRoute = /\/admin\/vocabulary-books\/[^/]+\/units\/[^/]+\/content(\?.*)?$/
const unitsRoute = /\/admin\/vocabulary-books\/[^/]+\/units(\?.*)?$/

/** Answers unit-content requests from mutable state, so a test can vary it. */
function useUnitContent(page: Page, state: { byUnit: Record<string, unknown> }) {
  return page.route(unitContentRoute, (route) => {
    const unitId = new URL(route.request().url()).pathname.split('/').at(-2) as string
    const data = state.byUnit[unitId]
    return data
      ? json(route, { success: true, data })
      : json(route, { success: false, message: 'Vocabulary book unit was not found.' }, 404)
  })
}

async function openBookWords(page: Page, book = bookA) {
  await page.route('**/admin/auth/session', (route) =>
    json(route, { success: true, data: admin }))
  await page.route('**/admin/auth/method', (route) =>
    json(route, { success: true, data: { method: 'password' } }))
  await page.route('**/admin/system/version', (route) =>
    json(route, { success: true, data: { version: '1.2.3', revision: null, channel: 'release' } }))
  await page.route(bookContentRoute, (route) =>
    json(route, { success: true, data: book.id === bookB.id ? { ...contentA, book: bookB } : contentA }))
  await page.route(unitsRoute, (route) =>
    json(route, { success: true, data: { units: book.id === bookB.id ? unitsB : unitsA } }))
  await useUnitContent(page, {
    byUnit: { 'unit-2': unit2Content, 'unit-6': unit6Content, 'unit-9': unit9Content, 'unit-b1': unitB1Content }
  })
  await page.route(/\/admin\/vocabulary\/[^/]+$/, (route) =>
    json(route, { success: true, data: wordDetail }))

  await page.goto(`/#/books/${book.id}/words`)
  await expect(page.locator('.session')).toContainText(admin.username)
  await expect(page.locator('.el-table')).toContainText('apple')
}

async function pickUnit(page: Page, label: string) {
  await page.locator('.toolbar__unit').click()
  await page.locator('.el-select-dropdown__item:visible').filter({ hasText: label }).first().click()
}

test('switches whole book, unit 2, unit 6, and back, each from its own read', async ({ page }) => {
  const contentRequests: string[] = []
  await page.route('**/admin/auth/session', (route) =>
    json(route, { success: true, data: admin }))
  await page.route('**/admin/auth/method', (route) =>
    json(route, { success: true, data: { method: 'password' } }))
  await page.route(bookContentRoute, (route) => {
    contentRequests.push(new URL(route.request().url()).pathname)
    return json(route, { success: true, data: contentA })
  })
  await page.route(unitContentRoute, (route) => {
    contentRequests.push(new URL(route.request().url()).pathname)
    const unitId = new URL(route.request().url()).pathname.split('/').at(-2)
    return json(route, { success: true, data: unitId === 'unit-2' ? unit2Content : unit6Content })
  })
  await page.route(unitsRoute, (route) =>
    json(route, { success: true, data: { units: unitsA } }))
  await page.goto(`/#/books/${bookA.id}/words`)

  // Whole book first: its counts are the book's, and the picker says 全书.
  await expect(page.locator('.book-words__meta')).toContainText('去重单词 2')
  await expect(page.locator('.book-words__meta')).toContainText('释义 3')
  await expect(page.locator('.book-words__meta .el-tag')).toHaveCount(1)
  await expect(page.locator('.toolbar__unit')).toContainText('全书')
  await expect(page.locator('.el-table__header')).toContainText('本教材释义')

  await pickUnit(page, '单元 2 · School Life')
  await expect(page.locator('.book-words__meta')).toContainText('单元 2 · School Life')
  await expect(page.locator('.book-words__meta')).toContainText('去重单词 2')
  await expect(page.locator('.book-words__meta')).toContainText('释义 2')
  await expect(page.locator('.el-table__header')).toContainText('本单元释义')
  await expect(contentRequests.at(-1)).toMatch(/\/units\/unit-2\/content$/)

  // Unit 6 has no title: the tag names the number alone.
  await pickUnit(page, '单元 6')
  await expect(page.locator('.book-words__meta')).toContainText('单元 6')
  await expect(page.locator('.book-words__meta')).not.toContainText('School Life')
  await expect(contentRequests.at(-1)).toMatch(/\/units\/unit-6\/content$/)

  await pickUnit(page, '全书')
  await expect(page.locator('.book-words__meta .el-tag')).toHaveCount(1)
  await expect(page.locator('.el-table__header')).toContainText('本教材释义')
  await expect(page.locator('.el-table')).toContainText('河岸')
  await expect(page.locator('.el-table')).toContainText('银行')
  expect(contentRequests).toHaveLength(4)
})

test('shows one meaning in both of its units and one word with two meanings apart', async ({ page }) => {
  await openBookWords(page)

  await pickUnit(page, '单元 2 · School Life')
  const appleRow = page.locator('.el-table__row', { hasText: 'apple' })
  await expect(appleRow).toContainText('苹果')
  await expect(page.locator('.el-table__row', { hasText: 'bank' })).toContainText('河岸')
  await expect(page.locator('.el-table__row', { hasText: 'bank' })).not.toContainText('银行')

  await pickUnit(page, '单元 6')
  await expect(page.locator('.el-table__row', { hasText: 'apple' })).toContainText('苹果')
  await expect(page.locator('.el-table__row', { hasText: 'bank' })).toContainText('银行')
  await expect(page.locator('.el-table__row', { hasText: 'bank' })).not.toContainText('河岸')
})

test('runs the unit view\'s keyword and page on the server', async ({ page }) => {
  await openBookWords(page)
  await page.route(unitContentRoute, (route) => {
    const url = new URL(route.request().url())
    const unitId = url.pathname.split('/').at(-2)
    const keyword = url.searchParams.get('keyword')
    const pageParam = url.searchParams.get('page')
    if (unitId !== 'unit-2') {
      return json(route, { success: false, message: 'Vocabulary book unit was not found.' }, 404)
    }
    if (keyword === 'bank' && pageParam === '2') {
      return json(route, {
        success: true,
        data: { ...unit2Content, items: [{ ...bank, meanings: [bankRiver] }], totalCount: 24, totalPage: 2 }
      })
    }
    if (keyword === 'bank') {
      return json(route, { success: true, data: { ...unit2Content, items: [], totalCount: 24, totalPage: 2 } })
    }
    return json(route, { success: true, data: unit2Content })
  })

  await pickUnit(page, '单元 2 · School Life')
  await page.getByRole('textbox', { name: '搜索单词' }).fill('bank')
  await page.getByRole('button', { name: '搜索', exact: true }).click()
  await expect(page.locator('.el-table__empty-block')).toContainText('没有匹配的单词')
  // Unit counts keep the unit's scope while the keyword narrows the page.
  await expect(page.locator('.book-words__meta')).toContainText('去重单词 2')
  await expect(page.locator('.el-pagination')).toContainText('24')

  await page.locator('.el-pagination').getByText('2', { exact: true }).click()
  await expect(page.locator('.el-table__row', { hasText: 'bank' })).toContainText('河岸')
})

test('tells an empty unit from the whole book', async ({ page }) => {
  await openBookWords(page)

  await pickUnit(page, '单元 9 · Empty Unit')
  await expect(page.locator('.el-table__empty-block')).toContainText('该单元暂无单词')
  await expect(page.locator('.book-words__meta')).toContainText('去重单词 0')

  await pickUnit(page, '全书')
  await expect(page.locator('.el-table')).toContainText('apple')
})

test('maintains a disabled book\'s unit view the same way', async ({ page }) => {
  await openBookWords(page, bookB)

  await expect(page.locator('.book-words__meta')).toContainText('停用')
  await pickUnit(page, '单元 1 · Greetings')
  await expect(page.locator('.book-words__meta')).toContainText('单元 1 · Greetings')
  await expect(page.locator('.el-table__row', { hasText: 'banana' })).toContainText('香蕉')
})

test('ignores a unit answer that arrives after the view moved on', async ({ page }) => {
  await openBookWords(page)
  // Re-register the route so this test controls the timing: late answers can
  // be orchestrated only on a deferred route.
  const unit2 = {
    requests: [] as URL[],
    answer: null as null | ((data: unknown) => void)
  }
  await page.route(unitContentRoute, (route) => {
    const url = new URL(route.request().url())
    if (url.pathname.endsWith('/units/unit-2/content')) {
      unit2.requests.push(url)
      return new Promise<void>((resolve) => {
        unit2.answer = (data) => {
          json(route, { success: true, data }).catch(() => undefined)
          resolve()
        }
      })
    }
    return json(route, { success: true, data: unit6Content })
  })

  await pickUnit(page, '单元 2 · School Life')
  // Switch to unit 6 while unit 2's answer is still pending.
  await pickUnit(page, '单元 6')
  await expect(page.locator('.book-words__meta')).toContainText('单元 6')
  await expect(page.locator('.el-table__row', { hasText: 'bank' })).toContainText('银行')

  // The late unit 2 answer must not repaint the view.
  unit2.answer?.(unit2Content)
  await page.waitForTimeout(300)
  await expect(page.locator('.book-words__meta')).toContainText('单元 6')
  await expect(page.locator('.el-table__row', { hasText: 'bank' })).toContainText('银行')
})

test('reports a unit deleted while its view was open and returns to the whole book', async ({ page }) => {
  await openBookWords(page)
  const state = { byUnit: { 'unit-2': unit2Content, 'unit-6': unit6Content, 'unit-9': unit9Content } }
  await useUnitContent(page, state)

  await pickUnit(page, '单元 2 · School Life')
  await expect(page.locator('.el-table')).toContainText('apple')

  // The unit is deleted elsewhere: the same view now answers 404.
  delete state.byUnit['unit-2']
  await page.getByRole('button', { name: '搜索', exact: true }).click()
  await expect(page.locator('.book-words__notice')).toContainText('该单元不存在或已被删除')
  await expect(page.locator('.el-table')).toHaveCount(0)

  await page.getByRole('button', { name: '返回全书' }).click()
  await expect(page.locator('.el-table')).toContainText('apple')
  await expect(page.locator('.toolbar__unit')).toContainText('全书')
})

test('retries a failed unit read and keeps the whole-book view working', async ({ page }) => {
  await openBookWords(page)
  let reads = 0
  await page.route(unitContentRoute, (route) => {
    reads += 1
    if (reads === 1) {
      return json(route, { success: false, message: 'Store is busy.' }, 503)
    }
    return json(route, { success: true, data: unit2Content })
  })

  await pickUnit(page, '单元 2 · School Life')
  const alert = page.locator('.book-words__error')
  await expect(alert).toContainText('Store is busy.')
  await expect(page.locator('.el-table')).toHaveCount(1)

  await alert.getByRole('button', { name: '重试' }).click()
  await expect(page.locator('.book-words__meta')).toContainText('单元 2 · School Life')
  await expect(page.locator('.el-table__row', { hasText: 'apple' })).toContainText('苹果')
})

test('disables both whole-book removal entries in the unit view and explains why', async ({ page }) => {
  await openBookWords(page)

  // Whole-book view: selecting rows enables the toolbar entry.
  await page.locator('.el-table__row', { hasText: 'apple' }).locator('td').first().click()
  const toolbarRemove = page.locator('.toolbar__remove')
  await expect(toolbarRemove).toBeEnabled()

  await pickUnit(page, '单元 2 · School Life')
  await expect(toolbarRemove).toBeDisabled()
  const rowRemove = page.locator('.el-table__row', { hasText: 'apple' }).getByRole('button', { name: '移除', exact: true })
  await expect(rowRemove).toBeDisabled()

  await toolbarRemove.hover()
  await expect(page.getByRole('tooltip', { name: '单元视图下不能从本教材移除；请切回全书视图操作' })).toBeVisible()
  // The toolbar now carries a third select beside the unit picker, and the
  // popper of this tooltip sits over it; leave the button so the tooltip
  // closes before the picker is used.
  await page.mouse.move(0, 0)

  // Back in the whole book the entries work again.
  await pickUnit(page, '全书')
  await expect(toolbarRemove).toBeDisabled() // no rows selected in the new view
  await page.locator('.el-table__row', { hasText: 'apple' }).locator('td').first().click()
  await expect(toolbarRemove).toBeEnabled()
})

test('retries a failed unit list and keeps the whole-book view usable', async ({ page }) => {
  await openBookWords(page)
  let reads = 0
  await page.route(unitsRoute, (route) => {
    reads += 1
    if (reads === 1) {
      return json(route, { success: false, message: 'Store is busy.' }, 503)
    }
    return json(route, { success: true, data: { units: unitsA } })
  })

  // This route takes over with the reload: that read fails, the retry succeeds.
  await page.reload()
  await expect(page.locator('.book-words__error')).toContainText('单元列表加载失败')
  await expect(page.locator('.toolbar__unit input')).toBeDisabled()
  // The whole-book view itself loaded and stays usable.
  await expect(page.locator('.el-table')).toContainText('apple')

  await page.getByRole('button', { name: '重试单元列表' }).click()
  await expect(page.locator('.book-words__error')).toHaveCount(0)
  await expect(page.locator('.toolbar__unit input')).toBeEnabled()
  await pickUnit(page, '单元 2 · School Life')
  await expect(page.locator('.book-words__meta')).toContainText('单元 2 · School Life')
})

test('labels each meaning\'s units in the shared detail drawer', async ({ page }) => {
  await openBookWords(page)

  await page.locator('.el-table__row', { hasText: 'apple' }).getByRole('button', { name: '详情' }).click()
  const drawer = page.locator('.el-drawer')
  await expect(drawer).toBeVisible()
  const group = drawer.locator('.word-detail__group', { hasText: bookA.bookName })
  await expect(group).toContainText('苹果')
  await expect(group).toContainText('单元 2 · School Life')
  await expect(group).toContainText('单元 6')
})

// Apple's one meaning sits in unit 2's Section A and Section B both; bank's
// meaning is unsectioned there. The section picker narrows the unit view to
// one section's places, the counts follow the narrowing, and the per-section
// place counts beside them always speak for the whole unit.
const sectionedAppleMeaning = {
  ...appleMeaning,
  units: [
    { unitId: 'unit-2', number: 2, title: 'School Life', section: 'A' },
    { unitId: 'unit-2', number: 2, title: 'School Life', section: 'B' },
    { unitId: 'unit-6', number: 6, title: null, section: null }
  ]
}

test('narrows the unit view to one section and reports the whole unit\'s sections', async ({ page }) => {
  const sectionCounts = { sectionA: 1, sectionB: 1, noSection: 1 }
  const bothSections = unitPage(
    unitsA[0],
    [
      { ...apple, meanings: [sectionedAppleMeaning] },
      { ...bank, meanings: [bankRiver] }
    ],
    { wordCount: 2, meaningCount: 2, totalCount: 2, totalPage: 1 }
  )
  const onlyApple = unitPage(
    unitsA[0],
    [{ ...apple, meanings: [sectionedAppleMeaning] }],
    { wordCount: 1, meaningCount: 1, totalCount: 1, totalPage: 1 }
  )
  const onlyBank = unitPage(
    unitsA[0],
    [{ ...bank, meanings: [bankRiver] }],
    { wordCount: 1, meaningCount: 1, totalCount: 1, totalPage: 1 }
  )

  await page.route('**/admin/auth/session', (route) =>
    json(route, { success: true, data: admin }))
  await page.route('**/admin/auth/method', (route) =>
    json(route, { success: true, data: { method: 'password' } }))
  await page.route('**/admin/system/version', (route) =>
    json(route, { success: true, data: { version: '1.2.3', revision: null, channel: 'release' } }))
  await page.route(bookContentRoute, (route) =>
    json(route, { success: true, data: contentA }))
  await page.route(unitsRoute, (route) =>
    json(route, { success: true, data: { units: unitsA } }))
  await page.route(/\/admin\/vocabulary\/[^/]+$/, (route) =>
    json(route, { success: true, data: { ...apple, meanings: [sectionedAppleMeaning] } }))

  const askedFor: string[] = []
  await page.route(unitContentRoute, (route) => {
    const url = new URL(route.request().url())
    const unitId = url.pathname.split('/').at(-2)
    askedFor.push(`${unitId}?section=${url.searchParams.get('section') ?? '-'}`)
    if (unitId !== 'unit-2') {
      return json(route, { success: true, data: unit6Content })
    }
    switch (url.searchParams.get('section')) {
      case 'A':
      case 'B':
        return json(route, { success: true, data: { ...onlyApple, sectionCounts } })
      case 'none':
        return json(route, { success: true, data: { ...onlyBank, sectionCounts } })
      default:
        return json(route, { success: true, data: { ...bothSections, sectionCounts } })
    }
  })

  await page.goto(`/#/books/${bookA.id}/words`)
  await expect(page.locator('.el-table')).toContainText('apple')
  // The section picker exists only inside a unit view.
  await expect(page.locator('.toolbar__section')).toHaveCount(0)

  await pickUnit(page, '单元 2 · School Life')
  await expect(page.locator('.toolbar__section')).toBeVisible()
  await expect(page.locator('.toolbar__section')).toContainText('全部分节')
  await expect(page.locator('.book-words__meta')).toContainText('释义 2')
  await expect(page.locator('.book-words__sections')).toHaveText('分节 A 1 · B 1 · 未分节 1')
  await expect(askedFor.at(-1)).toBe('unit-2?section=-')

  const pickSection = async (label: string) => {
    await page.locator('.toolbar__section').click()
    await page.locator('.el-select-dropdown__item:visible').filter({ hasText: label }).first().click()
  }

  await pickSection('Section A')
  await expect(page.locator('.el-table__row', { hasText: 'bank' })).toHaveCount(0)
  await expect(page.locator('.el-table__row', { hasText: 'apple' })).toContainText('苹果')
  await expect(page.locator('.book-words__meta')).toContainText('去重单词 1')
  await expect(page.locator('.book-words__meta')).toContainText('释义 1')
  await expect(page.locator('.book-words__sections')).toHaveText('分节 A 1 · B 1 · 未分节 1')
  await expect(askedFor.at(-1)).toBe('unit-2?section=A')

  await pickSection('未分节')
  await expect(page.locator('.el-table__row', { hasText: 'apple' })).toHaveCount(0)
  await expect(page.locator('.el-table__row', { hasText: 'bank' })).toContainText('河岸')
  await expect(askedFor.at(-1)).toBe('unit-2?section=none')

  await pickSection('全部分节')
  await expect(page.locator('.el-table')).toContainText('apple')
  await expect(page.locator('.el-table')).toContainText('河岸')
  await expect(askedFor.at(-1)).toBe('unit-2?section=-')

  // A unit switch resets the section refinement to 全部分节.
  await pickSection('Section B')
  await expect(askedFor.at(-1)).toBe('unit-2?section=B')
  await pickUnit(page, '单元 6')
  await expect(page.locator('.toolbar__section')).toContainText('全部分节')
  await expect(askedFor.at(-1)).toBe('unit-6?section=-')

  // The drawer names the section each place sits in, and a meaning in both
  // sections of one unit reads as two tags of that unit.
  await pickUnit(page, '单元 2 · School Life')
  await page.locator('.el-table__row', { hasText: 'apple' }).getByRole('button', { name: '详情' }).click()
  const drawer = page.locator('.el-drawer')
  await expect(drawer).toBeVisible()
  await expect(drawer.locator('.word-detail__unit-tag')).toHaveCount(3)
  await expect(drawer.locator('.word-detail__unit-tag').first()).toHaveText('单元 2 · School Life · Section A · 未分类')
  await expect(drawer.locator('.word-detail__unit-tag').nth(1)).toHaveText('单元 2 · School Life · Section B · 未分类')
  await expect(drawer.locator('.word-detail__unit-tag').nth(2)).toHaveText('单元 6 · 未分节 · 未分类')
})

// Apple's one meaning sits in unit 2 under both kinds of its unsectioned
// place; bank's meaning is an unclassified word there. The kind picker narrows
// the unit view to one kind's places, the counts follow the narrowing, and the
// per-kind place counts beside them always speak for the whole unit.
const kindedAppleMeaning = {
  ...appleMeaning,
  units: [
    { unitId: 'unit-2', number: 2, title: 'School Life', section: null, entryKind: 'word' },
    { unitId: 'unit-2', number: 2, title: 'School Life', section: null, entryKind: 'phrase' },
    { unitId: 'unit-6', number: 6, title: null, section: null, entryKind: null }
  ]
}
const kindedBankRiver = {
  ...bankRiver,
  units: [{ unitId: 'unit-2', number: 2, title: 'School Life', section: null, entryKind: null }]
}

test('narrows the unit view to one entry kind and reports the whole unit\'s kinds', async ({ page }) => {
  const entryKindCounts = { word: 1, phrase: 1, none: 1 }
  const bothKinds = unitPage(
    unitsA[0],
    [
      { ...apple, meanings: [kindedAppleMeaning] },
      { ...bank, meanings: [kindedBankRiver] }
    ],
    { wordCount: 2, meaningCount: 2, totalCount: 2, totalPage: 1 }
  )
  const onlyWords = unitPage(
    unitsA[0],
    [{ ...apple, meanings: [kindedAppleMeaning] }],
    { wordCount: 1, meaningCount: 1, totalCount: 1, totalPage: 1 }
  )
  const onlyUnclassified = unitPage(
    unitsA[0],
    [{ ...bank, meanings: [kindedBankRiver] }],
    { wordCount: 1, meaningCount: 1, totalCount: 1, totalPage: 1 }
  )

  await page.route('**/admin/auth/session', (route) =>
    json(route, { success: true, data: admin }))
  await page.route('**/admin/auth/method', (route) =>
    json(route, { success: true, data: { method: 'password' } }))
  await page.route('**/admin/system/version', (route) =>
    json(route, { success: true, data: { version: '1.2.3', revision: null, channel: 'release' } }))
  await page.route(bookContentRoute, (route) =>
    json(route, { success: true, data: contentA }))
  await page.route(unitsRoute, (route) =>
    json(route, { success: true, data: { units: unitsA } }))
  await page.route(/\/admin\/vocabulary\/[^/]+$/, (route) =>
    json(route, { success: true, data: { ...apple, meanings: [kindedAppleMeaning] } }))

  const askedFor: string[] = []
  await page.route(unitContentRoute, (route) => {
    const url = new URL(route.request().url())
    const unitId = url.pathname.split('/').at(-2)
    askedFor.push(`${unitId}?section=${url.searchParams.get('section') ?? '-'}&entryKind=${url.searchParams.get('entryKind') ?? '-'}`)
    if (unitId !== 'unit-2') {
      return json(route, { success: true, data: unit6Content })
    }
    switch (url.searchParams.get('entryKind')) {
      case 'word':
      case 'phrase':
        return json(route, { success: true, data: { ...onlyWords, entryKindCounts } })
      case 'none':
        return json(route, { success: true, data: { ...onlyUnclassified, entryKindCounts } })
      default:
        return json(route, { success: true, data: { ...bothKinds, entryKindCounts } })
    }
  })

  await page.goto(`/#/books/${bookA.id}/words`)
  await expect(page.locator('.el-table')).toContainText('apple')
  // The kind picker exists only inside a unit view.
  await expect(page.locator('.toolbar__kind')).toHaveCount(0)

  await pickUnit(page, '单元 2 · School Life')
  await expect(page.locator('.toolbar__kind')).toBeVisible()
  await expect(page.locator('.toolbar__kind')).toContainText('全部类别')
  await expect(page.locator('.book-words__meta')).toContainText('释义 2')
  await expect(page.locator('.book-words__kinds')).toHaveText('单词 1 · 短语 1 · 未分类 1')
  await expect(askedFor.at(-1)).toBe('unit-2?section=-&entryKind=-')

  const pickKind = async (label: string) => {
    await page.locator('.toolbar__kind').click()
    await page.locator('.el-select-dropdown__item:visible').filter({ hasText: label }).first().click()
  }

  await pickKind('单词')
  await expect(page.locator('.el-table__row', { hasText: 'bank' })).toHaveCount(0)
  await expect(page.locator('.el-table__row', { hasText: 'apple' })).toContainText('苹果')
  await expect(page.locator('.book-words__meta')).toContainText('去重单词 1')
  await expect(page.locator('.book-words__meta')).toContainText('释义 1')
  await expect(page.locator('.book-words__kinds')).toHaveText('单词 1 · 短语 1 · 未分类 1')
  await expect(askedFor.at(-1)).toBe('unit-2?section=-&entryKind=word')

  await pickKind('未分类')
  await expect(page.locator('.el-table__row', { hasText: 'apple' })).toHaveCount(0)
  await expect(page.locator('.el-table__row', { hasText: 'bank' })).toContainText('河岸')
  await expect(askedFor.at(-1)).toBe('unit-2?section=-&entryKind=none')

  await pickKind('全部类别')
  await expect(page.locator('.el-table')).toContainText('apple')
  await expect(page.locator('.el-table')).toContainText('河岸')
  await expect(askedFor.at(-1)).toBe('unit-2?section=-&entryKind=-')

  // The section and kind dimensions combine by intersection.
  await page.locator('.toolbar__section').click()
  await page.locator('.el-select-dropdown__item:visible').filter({ hasText: 'Section A' }).first().click()
  await expect(askedFor.at(-1)).toBe('unit-2?section=A&entryKind=-')

  // A unit switch resets the kind refinement to 全部类别.
  await pickKind('短语')
  await expect(askedFor.at(-1)).toBe('unit-2?section=A&entryKind=phrase')
  await pickUnit(page, '单元 6')
  await expect(page.locator('.toolbar__kind')).toContainText('全部类别')
  await expect(askedFor.at(-1)).toBe('unit-6?section=-&entryKind=-')

  // The drawer names the kind each place sits under, and a meaning under two
  // kinds of one place reads as two tags of that place.
  await pickUnit(page, '单元 2 · School Life')
  await page.locator('.el-table__row', { hasText: 'apple' }).getByRole('button', { name: '详情' }).click()
  const drawer = page.locator('.el-drawer')
  await expect(drawer).toBeVisible()
  await expect(drawer.locator('.word-detail__unit-tag')).toHaveCount(3)
  await expect(drawer.locator('.word-detail__unit-tag').first()).toHaveText('单元 2 · School Life · 未分节 · 词条')
  await expect(drawer.locator('.word-detail__unit-tag').nth(1)).toHaveText('单元 2 · School Life · 未分节 · 短语')
  await expect(drawer.locator('.word-detail__unit-tag').nth(2)).toHaveText('单元 6 · 未分节 · 未分类')
})

for (const width of [1440, 768]) {
  test.describe(`unit view at ${width} px`, () => {
    test.use({ viewport: { width, height: 900 } })

    test('has no WCAG 2.1 AA violations and no sideways scroll', async ({ page }) => {
      await openBookWords(page)
      await pickUnit(page, '单元 2 · School Life')
      await expect(page.locator('.el-table__row', { hasText: 'apple' })).toContainText('苹果')
      await page.waitForTimeout(400)

      const results = await new AxeBuilder({ page }).withTags(wcagTags).analyze()
      expect(results.violations).toEqual([])

      const overflow = await page.evaluate(() =>
        document.documentElement.scrollWidth - document.documentElement.clientWidth)
      expect(overflow).toBeLessThanOrEqual(0)
    })
  })
}

test('reaches the unit picker from the keyboard', async ({ page }) => {
  await openBookWords(page)

  // The picker opens with the pointer and then navigates by keyboard: the
  // highlight starts on the current view (全书) and one ArrowDown reaches the
  // first unit, Enter picks it.
  await page.locator('.toolbar__unit').click()
  await expect(page.locator('.el-select-dropdown:visible')).toBeVisible()
  await page.keyboard.press('ArrowDown')
  await page.keyboard.press('Enter')
  await expect(page.locator('.book-words__meta')).toContainText('单元 2 · School Life')
})
