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

function json(route: Route, data: unknown, status = 200) {
  return route.fulfill({
    status,
    contentType: 'application/json',
    body: JSON.stringify(data)
  })
}

const batchRoute = /\/admin\/vocabulary\/batch$/

async function openBatchPage(page: Page) {
  await page.route('**/admin/auth/session', (route) =>
    json(route, { success: true, data: admin }))
  await page.route(/\/api\/vocabulary-books\/all$/, (route) =>
    json(route, { success: true, data: { books: [starterBook, otherBook] } }))

  await page.goto('/#/import/batch')
  await expect(page.locator('.session')).toContainText(admin.username)
}

async function selectBook(page: Page, index = 0) {
  await page.locator('.batch-book-select').click()
  await page.locator('.el-select-dropdown__item').nth(index).click()
}

async function selectFormat(page: Page, label: 'TSV' | 'CSV') {
  await page.locator('.batch-format').getByText(label, { exact: true }).click()
}

function selectedFormat(page: Page) {
  return page.locator('.batch-format .el-radio-button.is-active')
}

function dataInput(page: Page) {
  return page.locator('.batch-input textarea')
}

function chooseFile(page: Page, name: string, buffer: Buffer) {
  return page.locator('.batch-file-input').setInputFiles({ name, mimeType: 'application/octet-stream', buffer })
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
    json(route, { success: true, data: { total: 1, created: 1, reused: 0 } }))
}

/** 苹果 in GBK, which is not valid UTF-8. */
const gbkApple = Buffer.from([0xc6, 0xbb, 0xb9, 0xfb])

// One batch written as TSV and as CSV. The CSV orders its header differently
// from the TSV columns and leaves phonetic_uk and phonetic_us out; it carries a
// comma, a doubled quote, and line breaks inside quoted fields, and a trailing
// column whose header and values are all blank, as spreadsheet exports write.
const sameBatchTsv = [
  'apple\t\t\tn.\t苹果，红色的水果\tI eat an apple, every day.',
  'banana\t\t\t\t香蕉\tHe said "yes".',
  'cherry\t\t\tn.\t樱桃',
  'date\t\t\t\t枣'
].join('\n')

const sameBatchCsvLines = [
  'meaning,word,example,part_of_speech,',
  '苹果，红色的水果,apple,"I eat an apple, every day.",n.,',
  '"香蕉",banana,"He said ""yes"".",,',
  '"',
  ' 樱桃',
  '",cherry,,n.,',
  '枣,date,,,'
]

const sameBatchEntries = [
  { word: 'apple', partOfSpeech: 'n.', meaning: '苹果，红色的水果', example: 'I eat an apple, every day.' },
  { word: 'banana', meaning: '香蕉', example: 'He said "yes".' },
  { word: 'cherry', partOfSpeech: 'n.', meaning: '樱桃' },
  { word: 'date', meaning: '枣' }
]

test('imports a CSV file with the same payload as the same batch in TSV', async ({ page }) => {
  await openBatchPage(page)
  const payloads = await acceptBatch(page)
  await selectBook(page)

  await dataInput(page).fill(sameBatchTsv)
  await expect(page.locator('.batch-summary')).toContainText('数据行 4 条，有效 4 条，无效 0 条')
  await submitButton(page).click()
  await expect(dataInput(page)).toHaveValue('')

  // A byte-order mark and CRLF endings, which only a file can carry: a text
  // area normalizes every line break to LF.
  await expect(selectedFormat(page)).toHaveText('TSV')
  await chooseFile(page, 'WORDS.CSV', Buffer.from(`\uFEFF${sameBatchCsvLines.join('\r\n')}\r\n`, 'utf-8'))

  await expect(selectedFormat(page)).toHaveText('CSV')
  await expect(page.locator('.batch-summary')).toContainText('数据行 4 条，有效 4 条，无效 0 条')
  // Each record at the line it starts on: the cherry record spans lines 4 to 6.
  await expect(previewRows(page).locator('td:first-child')).toHaveText(['2', '3', '4', '7'])
  await expect(previewRow(page, 2)).toContainText('I eat an apple, every day.')
  await expect(previewRow(page, 3)).toContainText('He said "yes".')
  await submitButton(page).click()
  await expect(page.locator('.batch-result')).toBeVisible()

  expect(payloads).toEqual([
    { bookId: starterBook.id, entries: sameBatchEntries },
    { bookId: starterBook.id, entries: sameBatchEntries }
  ])
})

test('parses pasted text as CSV once CSV is selected', async ({ page }) => {
  await openBatchPage(page)
  const payloads = await acceptBatch(page)
  await selectBook(page)

  await selectFormat(page, 'CSV')
  await expect(dataInput(page)).toHaveAttribute('placeholder', /第一行为表头，逗号分隔/)
  await dataInput(page).fill(sameBatchCsvLines.join('\n'))

  await expect(page.locator('.batch-summary')).toContainText('数据行 4 条，有效 4 条，无效 0 条')
  await submitButton(page).click()
  await expect(page.locator('.batch-result')).toBeVisible()
  expect(payloads).toEqual([{ bookId: starterBook.id, entries: sameBatchEntries }])
})

test('numbers CSV records by the line they start on and checks their field count', async ({ page }) => {
  await openBatchPage(page)
  const payloads = await acceptBatch(page)
  await selectBook(page)

  const lines = [
    'word,meaning,example',
    'apple,苹果,"line one',
    'line two"',
    '# hash,井号,',
    ',,',
    'banana,香蕉',
    ' , , ',
    'cherry,樱桃,,extra'
  ]
  await chooseFile(page, 'records.csv', Buffer.from(lines.join('\r\n'), 'utf-8'))

  await expect(page.locator('.batch-summary')).toContainText('数据行 4 条，有效 2 条，无效 2 条')
  // Blank records are skipped but counted; a record starting with # is data.
  await expect(previewRows(page).locator('td:first-child')).toHaveText(['2', '4', '6', '8'])
  await expect(previewRow(page, 4)).toContainText('有效')
  await expect(previewRow(page, 6)).toContainText('列数应为 3，实际为 2')
  await expect(previewRow(page, 8)).toContainText('列数应为 3，实际为 4')
  await expect(submitButton(page)).toBeDisabled()

  // Fixing the bad records leaves the line break inside the quoted field as LF.
  const fixed = [...lines.slice(0, 5), 'banana,香蕉,', ' , , ', 'cherry,樱桃,']
  await chooseFile(page, 'records.csv', Buffer.from(fixed.join('\r\n'), 'utf-8'))
  await expect(page.locator('.batch-summary')).toContainText('数据行 4 条，有效 4 条，无效 0 条')
  await submitButton(page).click()
  await expect(page.locator('.batch-result')).toBeVisible()
  expect(payloads).toEqual([{
    bookId: starterBook.id,
    entries: [
      { word: 'apple', meaning: '苹果', example: 'line one\nline two' },
      { word: '# hash', meaning: '井号' },
      { word: 'banana', meaning: '香蕉' },
      { word: 'cherry', meaning: '樱桃' }
    ]
  }])
})

const fileLevelErrors = [
  {
    name: 'a missing meaning column',
    csv: 'word,example\napple,I eat an apple.',
    expected: '表头缺少必需的 meaning 列'
  },
  {
    name: 'an unknown header',
    csv: 'word,phonetic_uk,meanings\napple,,苹果',
    expected: '第 3 列表头「meanings」无法识别；支持：word、phonetic_uk、phonetic_us、part_of_speech、meaning、example（CSV 只支持逗号分隔）'
  },
  {
    name: 'a semicolon-separated file',
    csv: 'word;meaning\napple;苹果',
    expected: '第 1 列表头「word;meaning」无法识别'
  },
  {
    name: 'a duplicate header',
    csv: 'word,meaning,WORD\napple,苹果,apple',
    expected: '第 3 列表头「WORD」与第 1 列重复'
  },
  {
    name: 'a blank header over values',
    csv: 'word,,meaning\napple,n.,苹果',
    expected: '第 2 列表头为空，但该列有数据'
  },
  {
    name: 'an unclosed quote',
    csv: 'word,meaning\napple,苹果\nbanana,"香蕉\ncherry,樱桃',
    expected: '第 3 行开始的引号没有闭合'
  },
  {
    name: 'text after a closing quote',
    csv: 'word,meaning\napple,"苹果"x',
    expected: '第 2 行：引号字段结束后应为逗号或换行'
  }
]

for (const { name, csv, expected } of fileLevelErrors) {
  test(`refuses a CSV with ${name} as a whole`, async ({ page }) => {
    await openBatchPage(page)
    const payloads = await acceptBatch(page)
    await selectBook(page)

    await selectFormat(page, 'CSV')
    await dataInput(page).fill(csv)

    await expect(page.locator('.batch-parse-error')).toContainText(expected)
    await expect(page.locator('.batch-preview')).toHaveCount(0)
    await expect(page.locator('.batch-blockers')).toContainText(expected)
    await expect(page.locator('.batch-blockers')).not.toContainText('没有可导入的数据行')
    await expect(submitButton(page)).toBeDisabled()
    await submitButton(page).click({ force: true })
    expect(payloads).toHaveLength(0)
  })
}

test('lists only the file-level error and the missing book as blockers', async ({ page }) => {
  await openBatchPage(page)

  await selectFormat(page, 'CSV')
  await dataInput(page).fill('word\napple')

  await expect(page.locator('.batch-blockers li')).toHaveText(['请选择教材', '表头缺少必需的 meaning 列'])
})

test('says a CSV with only a header has no data rows', async ({ page }) => {
  await openBatchPage(page)
  await selectBook(page)

  await selectFormat(page, 'CSV')
  await dataInput(page).fill('word,meaning\n,\n')

  await expect(page.locator('.batch-parse-error')).toHaveCount(0)
  await expect(page.locator('.batch-blockers')).toContainText('没有可导入的数据行')
  await expect(submitButton(page)).toBeDisabled()
})

test('refuses files that are not UTF-8 and keeps the text and format', async ({ page }) => {
  await openBatchPage(page)
  await selectBook(page)
  await dataInput(page).fill('apple\t\t\t\t苹果')

  const files = [
    { name: 'words.csv', buffer: Buffer.concat([Buffer.from('word,meaning\napple,'), gbkApple, Buffer.from('\n')]) },
    { name: 'words.tsv', buffer: Buffer.concat([Buffer.from('apple\t\t\t\t'), gbkApple, Buffer.from('\n')]) }
  ]
  for (const { name, buffer } of files) {
    await chooseFile(page, name, buffer)
    await expect(page.locator('.el-message--error').last())
      .toContainText('文件不是 UTF-8 编码，请另存为 UTF-8（Excel：CSV UTF-8（逗号分隔））后重试')
    await expect(dataInput(page)).toHaveValue('apple\t\t\t\t苹果')
    await expect(selectedFormat(page)).toHaveText('TSV')
  }
})

test('imports UTF-8 files with or without a byte-order mark', async ({ page }) => {
  await openBatchPage(page)
  const payloads = await acceptBatch(page)
  await selectBook(page)

  for (const prefix of ['\uFEFF', '']) {
    await chooseFile(page, 'words.csv', Buffer.from(`${prefix}word,meaning\napple,苹果\n`, 'utf-8'))
    await expect(page.locator('.batch-summary')).toContainText('数据行 1 条，有效 1 条，无效 0 条')
    await submitButton(page).click()
    await expect(dataInput(page)).toHaveValue('')
  }

  const expected = { bookId: starterBook.id, entries: [{ word: 'apple', meaning: '苹果' }] }
  expect(payloads).toEqual([expected, expected])
})

test('refuses an unsupported extension and an oversized CSV before reading them', async ({ page }) => {
  await openBatchPage(page)

  await chooseFile(page, 'words.xls', Buffer.from('word,meaning\napple,苹果\n', 'utf-8'))
  await expect(page.locator('.el-message--error').last())
    .toContainText('不支持的文件类型，请选择 .tsv、.txt 或 .csv 文件')
  await expect(dataInput(page)).toHaveValue('')

  await chooseFile(page, 'too-large.csv', Buffer.alloc(1_048_577, 'a'))
  await expect(page.locator('.el-message--error').last()).toContainText('文件超过 1 MiB，请拆分后分批导入')
  await expect(dataInput(page)).toHaveValue('')
  await expect(selectedFormat(page)).toHaveText('TSV')
})

test('shows server entry errors on CSV start lines and withdraws them on any change', async ({ page }) => {
  await openBatchPage(page)
  await page.route(batchRoute, (route) => json(route, {
    success: false,
    message: '2 entries are invalid.',
    errors: [
      { index: 1, message: 'Meaning is required.' },
      { index: 2, message: 'Word is required.' }
    ]
  }, 400))

  // Entry 1 starts on line 4 because entry 0 spans lines 2 and 3.
  const csv = 'word,meaning\napple,"苹果\n红色"\nbanana,香蕉\n\ncherry,樱桃'
  const serverSummary = page.locator('.batch-server-summary')
  async function submitAndExpectErrors() {
    await submitButton(page).click()
    await expect(serverSummary).toContainText('2 行未通过服务端校验，整批未写入')
  }

  await selectBook(page)
  await selectFormat(page, 'CSV')
  await dataInput(page).fill(csv)
  await submitAndExpectErrors()
  await expect(previewRows(page).locator('td:first-child')).toHaveText(['4', '6'])
  await expect(previewRow(page, 4)).toContainText('服务端：Meaning is required.')
  await expect(previewRow(page, 6)).toContainText('服务端：Word is required.')

  await dataInput(page).fill(`${csv} `)
  await expect(serverSummary).toHaveCount(0)
  await expect(submitButton(page)).toBeEnabled()

  await submitAndExpectErrors()
  await selectFormat(page, 'TSV')
  await expect(serverSummary).toHaveCount(0)
  await selectFormat(page, 'CSV')
  await expect(submitButton(page)).toBeEnabled()

  await submitAndExpectErrors()
  await selectBook(page, 1)
  await expect(serverSummary).toHaveCount(0)
  await expect(submitButton(page)).toBeEnabled()
})
