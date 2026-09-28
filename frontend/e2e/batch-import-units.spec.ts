import { expect, test, type Page, type Route } from '@playwright/test'

const admin = {
  username: 'ci-admin',
  roles: ['admin']
}

function book(id: string, bookName: string, displayOrder: number) {
  return {
    id,
    bookName,
    description: 'Browser test fixture',
    publisher: 'Lexarbor',
    educationLevel: 'Secondary',
    grade: 'Grade 7',
    category: 'English',
    displayOrder,
    status: true,
    iconUrl: ''
  }
}

const starterBook = book('11111111-1111-1111-1111-111111111111', 'CI Starter Book', 1)
const otherBook = book('22222222-2222-2222-2222-222222222222', 'CI Other Book', 2)

// Unit numbers are matched as written against String(unit.number): a padded
// `02` or a `Unit 2` spelling is unknown, and a blank is no assignment.
const starterUnits = [
  { id: 'unit-2', bookId: starterBook.id, number: 2, title: 'School Life', meaningCount: 12 },
  { id: 'unit-5', bookId: starterBook.id, number: 5, title: null, meaningCount: 3 }
]
const otherUnits = [
  { id: 'other-unit-1', bookId: otherBook.id, number: 1, title: 'Greetings', meaningCount: 0 }
]

function json(route: Route, data: unknown, status = 200) {
  return route.fulfill({
    status,
    contentType: 'application/json',
    body: JSON.stringify(data)
  })
}

const batchRoute = /\/admin\/vocabulary\/batch$/
const unitsRoute = /\/admin\/vocabulary-books\/[^/]+\/units$/

/** Serves each book its own units and records the URLs asked for. */
async function useUnits(page: Page) {
  const askedFor: string[] = []
  await page.route(unitsRoute, (route) => {
    askedFor.push(new URL(route.request().url()).pathname)
    const bookId = new URL(route.request().url()).pathname.split('/')[3]
    const units = bookId === otherBook.id ? otherUnits : starterUnits
    return json(route, { success: true, data: { units } })
  })
  return askedFor
}

async function openBatchPage(page: Page) {
  await page.route('**/admin/auth/session', (route) =>
    json(route, { success: true, data: admin }))
  await page.route('**/admin/system/version', (route) =>
    json(route, {
      success: true,
      data: { version: '1.2.3', revision: null, channel: 'release' }
    }))
  await page.route(/\/api\/vocabulary-books\/all$/, (route) =>
    json(route, { success: true, data: { books: [starterBook, otherBook] } }))
  await useUnits(page)

  await page.goto('/#/import/batch')
  await expect(page.locator('.session')).toContainText(admin.username)
}

async function selectBook(page: Page, index = 0) {
  await page.locator('.batch-book-select').click()
  await page.locator('.el-select-dropdown__item').nth(index).click()
}

async function selectFormat(page: Page, label: 'TSV' | 'CSV' | 'JSON') {
  await page.locator('.batch-format').getByText(label, { exact: true }).click()
}

function dataInput(page: Page) {
  return page.locator('.batch-input textarea')
}

function submitButton(page: Page) {
  return page.getByRole('button', { name: '提交导入' })
}

function previewRows(page: Page) {
  return page.locator('.batch-preview .el-table__body .el-table__row')
}

/** The row whose first cell is this position. */
function previewRow(page: Page, position: number) {
  return previewRows(page).filter({
    has: page.locator('td:first-child', { hasText: new RegExp(`^\\s*${position}\\s*$`) })
  })
}

/** Records every batch request and answers each with the given response. */
async function answerBatch(page: Page, respond: (route: Route) => Promise<void>) {
  const payloads: unknown[] = []
  await page.route(batchRoute, (route) => {
    payloads.push(route.request().postDataJSON())
    return respond(route)
  })
  return payloads
}

function acceptBatch(page: Page) {
  return answerBatch(page, (route) =>
    json(route, { success: true, data: { total: 3, created: 3, reused: 0 } }))
}

/** Waits until the unit column shows a resolved unit, so the unit list has loaded. */
async function unitResolved(page: Page, position: number, text: string) {
  await expect(previewRow(page, position).locator('td').nth(7)).toHaveText(text)
}

// One batch with two different units and one row without a unit, as TSV with
// the optional seventh column: 5, 6, or 7 columns per line.
const unitsTsv = [
  'apple\t\t\tn.\t苹果\tI eat an apple.\t2',
  'banana\t\t\t\t香蕉\t\t5',
  'cherry\t\t\t\t樱桃'
].join('\n')

const unitsEntries = [
  { word: 'apple', partOfSpeech: 'n.', meaning: '苹果', example: 'I eat an apple.', unitId: 'unit-2' },
  { word: 'banana', meaning: '香蕉', unitId: 'unit-5' },
  { word: 'cherry', meaning: '樱桃' }
]

test('resolves the TSV seventh column into unitIds and leaves unit-less rows without the field', async ({ page }) => {
  await openBatchPage(page)
  const payloads = await acceptBatch(page)
  await selectBook(page)

  await dataInput(page).fill(unitsTsv)
  await expect(page.locator('.batch-summary')).toContainText('数据行 3 条，有效 3 条，无效 0 条')
  await unitResolved(page, 1, '2 · School Life')
  await unitResolved(page, 2, '5')
  await expect(previewRow(page, 3).locator('td').nth(7)).toHaveText('')

  await submitButton(page).click()
  await expect(page.locator('.batch-result')).toBeVisible()
  expect(payloads).toEqual([{ bookId: starterBook.id, entries: unitsEntries }])
})

test('resolves a CSV unit column in any header order into the same unitIds', async ({ page }) => {
  await openBatchPage(page)
  const payloads = await acceptBatch(page)
  await selectBook(page)

  const csv = [
    'unit,part_of_speech,meaning,word,example',
    '2,n.,苹果,apple,I eat an apple.',
    '"5",,香蕉,banana,',
    ',,樱桃,cherry,'
  ].join('\r\n')
  await page.locator('.batch-file-input').setInputFiles({
    name: 'words.csv',
    mimeType: 'text/csv',
    buffer: Buffer.from(csv, 'utf-8')
  })

  await expect(page.locator('.batch-summary')).toContainText('数据行 3 条，有效 3 条，无效 0 条')
  await unitResolved(page, 2, '2 · School Life')
  await unitResolved(page, 3, '5')

  await submitButton(page).click()
  await expect(page.locator('.batch-result')).toBeVisible()
  expect(payloads).toEqual([{ bookId: starterBook.id, entries: unitsEntries }])
})

test('resolves a JSON unit field into the same unitIds', async ({ page }) => {
  await openBatchPage(page)
  const payloads = await acceptBatch(page)
  await selectBook(page)

  await selectFormat(page, 'JSON')
  await dataInput(page).fill(JSON.stringify([
    { word: 'apple', partOfSpeech: 'n.', meaning: '苹果', example: 'I eat an apple.', unit: '2' },
    { word: 'banana', meaning: '香蕉', unit: '5' },
    { word: 'cherry', meaning: '樱桃' }
  ]))

  await expect(page.locator('.batch-summary')).toContainText('数据行 3 条，有效 3 条，无效 0 条')
  await unitResolved(page, 1, '2 · School Life')
  await unitResolved(page, 2, '5')

  await submitButton(page).click()
  await expect(page.locator('.batch-result')).toBeVisible()
  expect(payloads).toEqual([{ bookId: starterBook.id, entries: unitsEntries }])
})

test('refuses a non-string JSON unit value instead of converting it', async ({ page }) => {
  await openBatchPage(page)
  const payloads = await acceptBatch(page)
  await selectBook(page)

  await selectFormat(page, 'JSON')
  await dataInput(page).fill(JSON.stringify([
    { word: 'apple', meaning: '苹果', unit: 2 },
    { word: 'banana', meaning: '香蕉' }
  ]))

  await expect(page.locator('.batch-summary')).toContainText('数据行 2 条，有效 1 条，无效 1 条')
  await expect(previewRow(page, 1)).toContainText('字段 unit 应为字符串')
  await expect(page.locator('.batch-blockers')).toContainText('存在 1 行无效数据')
  await expect(submitButton(page)).toBeDisabled()
  await submitButton(page).click({ force: true })
  expect(payloads).toHaveLength(0)
})

test('marks unknown and blank unit numbers on their rows and keeps them from submitting', async ({ page }) => {
  await openBatchPage(page)
  const payloads = await acceptBatch(page)
  await selectBook(page)

  // 2 is a unit; 02, "Unit 2", and 9 are not; an empty seventh column is no
  // assignment. The line numbers stay physical so the rows can be found.
  const text = [
    '# comment',
    'apple\t\t\tn.\t苹果\t\t2',
    'banana\t\t\t\t香蕉\t\t02',
    'cherry\t\t\t\t樱桃\t\tUnit 2',
    'date\t\t\t\t枣\t\t9',
    'egg\t\t\t\t蛋\t\t'
  ].join('\n')
  await dataInput(page).fill(text)

  await expect(page.locator('.batch-summary')).toContainText('数据行 5 条，有效 2 条，无效 3 条')
  await unitResolved(page, 2, '2 · School Life')
  await expect(previewRow(page, 3)).toContainText('未知单元：02')
  await expect(previewRow(page, 4)).toContainText('未知单元：Unit 2')
  await expect(previewRow(page, 5)).toContainText('未知单元：9')
  await expect(previewRow(page, 3).locator('td').nth(7)).toHaveText('02')
  await expect(previewRow(page, 6).locator('td').nth(7)).toHaveText('')

  await page.locator('.batch-only-invalid').click()
  await expect(previewRows(page).locator('td:first-child')).toHaveText(['3', '4', '5'])
  await expect(page.locator('.batch-blockers')).toContainText('存在 3 行无效数据')
  await expect(submitButton(page)).toBeDisabled()
  await submitButton(page).click({ force: true })
  expect(payloads).toHaveLength(0)

  // Corrected rows import with their assignments; the blank stays unassigned.
  const fixed = [
    'apple\t\t\tn.\t苹果\t\t2',
    'banana\t\t\t\t香蕉\t\t2',
    'cherry\t\t\t\t樱桃\t\t5',
    'date\t\t\t\t枣\t\t5',
    'egg\t\t\t\t蛋'
  ].join('\n')
  await dataInput(page).fill(fixed)
  await expect(page.locator('.batch-summary')).toContainText('数据行 5 条，有效 5 条，无效 0 条')
  await expect(submitButton(page)).toBeEnabled()
  await submitButton(page).click()
  await expect(page.locator('.batch-result')).toBeVisible()
  expect(payloads).toEqual([{
    bookId: starterBook.id,
    entries: [
      { word: 'apple', partOfSpeech: 'n.', meaning: '苹果', unitId: 'unit-2' },
      { word: 'banana', meaning: '香蕉', unitId: 'unit-2' },
      { word: 'cherry', meaning: '樱桃', unitId: 'unit-5' },
      { word: 'date', meaning: '枣', unitId: 'unit-5' },
      { word: 'egg', meaning: '蛋' }
    ]
  }])
})

test('re-resolves the unit column when the book changes', async ({ page }) => {
  await openBatchPage(page)
  const payloads = await acceptBatch(page)
  await selectBook(page)

  await dataInput(page).fill(unitsTsv)
  await unitResolved(page, 1, '2 · School Life')
  await expect(submitButton(page)).toBeEnabled()

  // The other book has unit 1, not 2 or 5: every unit column becomes unknown.
  await selectBook(page, 1)
  await expect(previewRow(page, 1)).toContainText('未知单元：2')
  await expect(previewRow(page, 2)).toContainText('未知单元：5')
  await expect(page.locator('.batch-blockers')).toContainText('存在 2 行无效数据')
  await expect(submitButton(page)).toBeDisabled()
  await submitButton(page).click({ force: true })
  expect(payloads).toHaveLength(0)

  // Back on the first book the same text resolves again.
  await selectBook(page, 0)
  await unitResolved(page, 1, '2 · School Life')
  await expect(submitButton(page)).toBeEnabled()
})

test('shows a server unit rejection on the source line it belongs to', async ({ page }) => {
  await openBatchPage(page)
  await answerBatch(page, (route) => json(route, {
    success: false,
    message: '1 entry is invalid.',
    errors: [{ index: 1, message: 'Unit was not found in the requested vocabulary book.' }]
  }, 400))
  await selectBook(page)

  await dataInput(page).fill(unitsTsv)
  await submitButton(page).click()

  // A unit deleted after the list was loaded: the server refuses the whole
  // batch and the reason lands on entry 1, the banana line.
  await expect(page.locator('.batch-server-summary')).toContainText('1 行未通过服务端校验，整批未写入')
  await expect(previewRow(page, 2)).toContainText('服务端：Unit was not found in the requested vocabulary book.')
  await expect(submitButton(page)).toBeDisabled()
})

test('blocks submission while the unit list cannot be loaded, and retries', async ({ page }) => {
  await openBatchPage(page)
  const payloads = await acceptBatch(page)

  // Replace the unit list route: the first read fails, later ones succeed.
  let reads = 0
  await page.route(unitsRoute, async (route) => {
    reads += 1
    if (reads === 1) {
      return json(route, { success: false, message: 'Store is busy.' }, 503)
    }
    return json(route, { success: true, data: { units: starterUnits } })
  })

  await selectBook(page)
  await expect(page.locator('.batch-units-error')).toContainText('当前教材的单元列表加载失败，无法解析单元归属')
  await expect(page.locator('.batch-blockers')).toContainText('当前教材的单元列表未加载，无法解析单元归属，请重试')

  // The rows parse, but their unit numbers cannot be resolved or submitted.
  await dataInput(page).fill(unitsTsv)
  await expect(page.locator('.batch-summary')).toContainText('数据行 3 条，有效 3 条，无效 0 条')
  await expect(previewRow(page, 1).locator('td').nth(7)).toHaveText('2')
  await expect(submitButton(page)).toBeDisabled()
  await submitButton(page).click({ force: true })
  expect(payloads).toHaveLength(0)

  await page.locator('.batch-units-retry').click()
  await unitResolved(page, 1, '2 · School Life')
  await expect(page.locator('.batch-units-error')).toHaveCount(0)
  await expect(submitButton(page)).toBeEnabled()
  await submitButton(page).click()
  await expect(page.locator('.batch-result')).toBeVisible()
  expect(payloads).toEqual([{ bookId: starterBook.id, entries: unitsEntries }])
})

test('resolves the TSV eighth column into sections and keeps invalid ones on their rows', async ({ page }) => {
  await openBatchPage(page)
  const payloads = await acceptBatch(page)
  await selectBook(page)

  const text = [
    'apple\t\t\tn.\t苹果\t\t2\tA',
    'banana\t\t\t\t香蕉\t\t2\tB',
    'cherry\t\t\t\t樱桃\t\t5',
    'date\t\t\t\t枣\t\t\tA',
    'egg\t\t\t\t蛋\t\t2\ta',
    'fig\t\t\t\t无花果\t\t2\t A '
  ].join('\n')
  await dataInput(page).fill(text)

  await expect(page.locator('.batch-summary')).toContainText('数据行 6 条，有效 4 条，无效 2 条')
  await expect(previewRow(page, 1).locator('td').nth(7)).toHaveText('2 · School Life')
  await expect(previewRow(page, 1).locator('td').nth(8)).toHaveText('A')
  await expect(previewRow(page, 2).locator('td').nth(8)).toHaveText('B')
  await expect(previewRow(page, 3).locator('td').nth(8)).toHaveText('')
  // A section without a unit names a place of nothing, and only A and B are
  // sections — lowercase is not normalized into one. A padded section trims
  // to a valid one.
  await expect(previewRow(page, 4)).toContainText('有分节但未填写单元')
  await expect(previewRow(page, 5)).toContainText('分节应为 A 或 B：a')
  await expect(previewRow(page, 6).locator('td').nth(8)).toHaveText('A')
  await expect(page.locator('.batch-blockers')).toContainText('存在 2 行无效数据')
  await expect(submitButton(page)).toBeDisabled()
  await submitButton(page).click({ force: true })
  expect(payloads).toHaveLength(0)

  // Corrected rows import with their sections; the padded one trims to A.
  const fixed = [
    'apple\t\t\tn.\t苹果\t\t2\tA',
    'banana\t\t\t\t香蕉\t\t2\tB',
    'cherry\t\t\t\t樱桃\t\t5',
    'date\t\t\t\t枣\t\t2\tA',
    'egg\t\t\t\t蛋\t\t2\tB',
    'fig\t\t\t\t无花果\t\t2\t A '
  ].join('\n')
  await dataInput(page).fill(fixed)
  await expect(page.locator('.batch-summary')).toContainText('数据行 6 条，有效 6 条，无效 0 条')
  await submitButton(page).click()
  await expect(page.locator('.batch-result')).toBeVisible()
  expect(payloads).toEqual([{
    bookId: starterBook.id,
    entries: [
      { word: 'apple', partOfSpeech: 'n.', meaning: '苹果', unitId: 'unit-2', section: 'A' },
      { word: 'banana', meaning: '香蕉', unitId: 'unit-2', section: 'B' },
      { word: 'cherry', meaning: '樱桃', unitId: 'unit-5' },
      { word: 'date', meaning: '枣', unitId: 'unit-2', section: 'A' },
      { word: 'egg', meaning: '蛋', unitId: 'unit-2', section: 'B' },
      { word: 'fig', meaning: '无花果', unitId: 'unit-2', section: 'A' }
    ]
  }])
})

test('resolves a CSV section column and a JSON section field into the same entries', async ({ page }) => {
  await openBatchPage(page)
  const payloads = await acceptBatch(page)
  await selectBook(page)

  const csv = [
    'word,meaning,unit,section',
    'apple,苹果,2,A',
    'banana,香蕉,2,B',
    'cherry,樱桃,5,'
  ].join('\r\n')
  await page.locator('.batch-file-input').setInputFiles({
    name: 'words.csv',
    mimeType: 'text/csv',
    buffer: Buffer.from(csv, 'utf-8')
  })
  await expect(page.locator('.batch-summary')).toContainText('数据行 3 条，有效 3 条，无效 0 条')
  await expect(previewRow(page, 2).locator('td').nth(8)).toHaveText('A')

  await submitButton(page).click()
  await expect(page.locator('.batch-result')).toBeVisible()

  await selectFormat(page, 'JSON')
  await dataInput(page).fill(JSON.stringify([
    { word: 'apple', meaning: '苹果', unit: '2', section: 'A' },
    { word: 'banana', meaning: '香蕉', unit: '2', section: 'B' },
    { word: 'cherry', meaning: '樱桃', unit: '5' }
  ]))
  await expect(page.locator('.batch-summary')).toContainText('数据行 3 条，有效 3 条，无效 0 条')
  await submitButton(page).click()
  await expect(page.locator('.batch-result')).toBeVisible()

  expect(payloads).toEqual([
    {
      bookId: starterBook.id,
      entries: [
        { word: 'apple', meaning: '苹果', unitId: 'unit-2', section: 'A' },
        { word: 'banana', meaning: '香蕉', unitId: 'unit-2', section: 'B' },
        { word: 'cherry', meaning: '樱桃', unitId: 'unit-5' }
      ]
    },
    {
      bookId: starterBook.id,
      entries: [
        { word: 'apple', meaning: '苹果', unitId: 'unit-2', section: 'A' },
        { word: 'banana', meaning: '香蕉', unitId: 'unit-2', section: 'B' },
        { word: 'cherry', meaning: '樱桃', unitId: 'unit-5' }
      ]
    }
  ])
})

test('refuses a non-string JSON section value instead of converting it', async ({ page }) => {
  await openBatchPage(page)
  const payloads = await acceptBatch(page)
  await selectBook(page)

  await selectFormat(page, 'JSON')
  await dataInput(page).fill(JSON.stringify([
    { word: 'apple', meaning: '苹果', unit: '2', section: 2 },
    { word: 'banana', meaning: '香蕉' }
  ]))

  await expect(page.locator('.batch-summary')).toContainText('数据行 2 条，有效 1 条，无效 1 条')
  await expect(previewRow(page, 1)).toContainText('字段 section 应为字符串')
  await expect(page.locator('.batch-blockers')).toContainText('存在 1 行无效数据')
  await expect(submitButton(page)).toBeDisabled()
  await submitButton(page).click({ force: true })
  expect(payloads).toHaveLength(0)
})

test('re-checks the unit column when a file replaces pasted text', async ({ page }) => {
  await openBatchPage(page)
  const payloads = await acceptBatch(page)
  await selectBook(page)

  await dataInput(page).fill('apple\t\t\t\t苹果\t\t2')
  await unitResolved(page, 1, '2 · School Life')

  // A file replaces the text; its unknown unit number takes over the preview.
  await page.locator('.batch-file-input').setInputFiles({
    name: 'words.tsv',
    mimeType: 'text/tab-separated-values',
    buffer: Buffer.from('banana\t\t\t\t香蕉\t\t9\ncherry\t\t\t\t樱桃\t\t5\n', 'utf-8')
  })
  await expect(page.locator('.batch-summary')).toContainText('数据行 2 条，有效 1 条，无效 1 条')
  await expect(previewRow(page, 1)).toContainText('未知单元：9')
  await expect(previewRow(page, 2)).toContainText('有效')
  await expect(submitButton(page)).toBeDisabled()

  // Another file replaces it again with valid units.
  await page.locator('.batch-file-input').setInputFiles({
    name: 'words.tsv',
    mimeType: 'text/tab-separated-values',
    buffer: Buffer.from('date\t\t\t\t枣\t\t2\n', 'utf-8')
  })
  await expect(page.locator('.batch-summary')).toContainText('数据行 1 条，有效 1 条，无效 0 条')
  await unitResolved(page, 1, '2 · School Life')
  await submitButton(page).click()
  await expect(page.locator('.batch-result')).toBeVisible()
  expect(payloads).toEqual([{
    bookId: starterBook.id,
    entries: [{ word: 'date', meaning: '枣', unitId: 'unit-2' }]
  }])
})
