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

const batchRoute = /\/admin\/vocabulary\/batch$/

async function openBatchPage(page: Page) {
  await page.route('**/admin/auth/session', (route) =>
    json(route, { success: true, data: admin }))
  await page.route(/\/api\/vocabulary-books\/all$/, (route) =>
    json(route, { success: true, data: { books: [starterBook] } }))

  await page.goto('/#/import/batch')
  await expect(page.locator('.session')).toContainText(admin.username)
}

async function selectBook(page: Page) {
  await page.locator('.batch-book-select').click()
  await page.locator('.el-select-dropdown__item').first().click()
}

function tsvInput(page: Page) {
  return page.locator('.batch-input textarea')
}

function submitButton(page: Page) {
  return page.getByRole('button', { name: '提交导入' })
}

function previewRows(page: Page) {
  return page.locator('.batch-preview .el-table__body .el-table__row')
}

/** The row whose first cell is this source line number. */
function previewRow(page: Page, lineNumber: number) {
  return previewRows(page).filter({
    has: page.locator('td:first-child', { hasText: new RegExp(`^\\s*${lineNumber}\\s*$`) })
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

function refuseBatch(page: Page, status: number, body: Record<string, unknown>) {
  return answerBatch(page, (route) => json(route, { success: false, ...body }, status))
}

const validTsv = 'apple\t/ˈæp.əl/\t/ˈæp.əl/\tn.\t苹果\tI eat an apple.\nbanana\t\t\tn.\t香蕉\n'

test('submits the preview as one batch in source order and shows the result', async ({ page }) => {
  await openBatchPage(page)
  const payloads = await answerBatch(page, (route) =>
    json(route, { success: true, data: { total: 4, created: 3, reused: 1 } }))

  // A byte-order mark before a comment, a blank line, padded columns, and a
  // sixth column left empty. Were the mark kept, the first line would not be a
  // comment and would parse as a valid row for the word "# word".
  const text = [
    '\uFEFF# word\tuk\tus\tpos\tmeaning\texample',
    '  apple \t /ˈæp.əl/ \t\t n. \t 苹果 \t I eat an apple. ',
    '',
    'banana\t\t\t\t香蕉\t',
    'cherry\t\t/ˈtʃer.i/\t\t樱桃',
    'date\t\t\tn.\t枣',
    ''
  ].join('\n')
  await selectBook(page)
  await tsvInput(page).fill(text)

  await expect(page.locator('.batch-summary')).toContainText('数据行 4 条，有效 4 条，无效 0 条')
  await expect(previewRows(page).locator('td:first-child')).toHaveText(['2', '4', '5', '6'])
  await submitButton(page).click()

  await expect(page.locator('.batch-result')).toContainText('总计 4 条，新增 3 条，复用 1 条')
  // Every column trimmed, and a blank optional column absent rather than an
  // empty string the server would have to interpret.
  expect(payloads).toEqual([{
    bookId: starterBook.id,
    entries: [
      { word: 'apple', phoneticUk: '/ˈæp.əl/', partOfSpeech: 'n.', meaning: '苹果', example: 'I eat an apple.' },
      { word: 'banana', meaning: '香蕉' },
      { word: 'cherry', phoneticUs: '/ˈtʃer.i/', meaning: '樱桃' },
      { word: 'date', partOfSpeech: 'n.', meaning: '枣' }
    ]
  }])
  // Cleared so the same batch is not sent again by accident; the book stays for
  // the next batch into it.
  await expect(tsvInput(page)).toHaveValue('')
  await expect(previewRows(page)).toHaveCount(0)
  await expect(page.locator('.batch-book-select')).toContainText(starterBook.bookName)
})

test('reads a chosen file into the preview without uploading it', async ({ page }) => {
  await openBatchPage(page)
  const payloads = await answerBatch(page, (route) =>
    json(route, { success: true, data: { total: 3, created: 3, reused: 0 } }))

  // CRLF endings, a lone CR, and a byte-order mark, as Windows editors and
  // spreadsheet exports write them. Only a file can carry these line breaks: a
  // textarea normalizes every one of them to LF as soon as its value is set.
  const content = [
    '\uFEFF# exported\r\n',
    'apple\t/ˈæp.əl/\t/ˈæp.əl/\tn.\t苹果\tI eat an apple.\r\n',
    '\r\n',
    'banana\t\t\tn.\t香蕉\r',
    'cherry\t\t\t\t樱桃\t\r\n'
  ].join('')
  await selectBook(page)
  await page.locator('.batch-file-input').setInputFiles({
    name: 'words.tsv',
    mimeType: 'text/tab-separated-values',
    buffer: Buffer.from(content, 'utf-8')
  })

  await expect(page.locator('.batch-summary')).toContainText('数据行 3 条，有效 3 条，无效 0 条')
  await expect(previewRows(page).locator('td:first-child')).toHaveText(['2', '4', '5'])
  expect(payloads).toHaveLength(0)

  await submitButton(page).click()
  await expect(page.locator('.batch-result')).toBeVisible()
  expect(payloads).toEqual([{
    bookId: starterBook.id,
    entries: [
      { word: 'apple', phoneticUk: '/ˈæp.əl/', phoneticUs: '/ˈæp.əl/', partOfSpeech: 'n.', meaning: '苹果', example: 'I eat an apple.' },
      { word: 'banana', partOfSpeech: 'n.', meaning: '香蕉' },
      { word: 'cherry', meaning: '樱桃' }
    ]
  }])
})

test('marks invalid rows with their physical line number and does not submit', async ({ page }) => {
  await openBatchPage(page)
  const payloads = await answerBatch(page, (route) =>
    json(route, { success: true, data: { total: 0, created: 0, reused: 0 } }))

  const text = [
    '# comment',
    'apple\t\t\tn.\t苹果',
    '',
    'too\tfew\tcolumns',
    '\t\t\tn.\t无词',
    'orphan\t\t\tn.\t',
    '\t\t\tn.\t',
    '  # not a comment once indented'
  ].join('\n')
  await selectBook(page)
  await tsvInput(page).fill(text)

  await expect(page.locator('.batch-summary')).toContainText('数据行 6 条，有效 1 条，无效 5 条')
  // Blank and comment lines are not rows, but they are lines: the numbers are
  // the ones an editor shows, so the administrator can find the row.
  await expect(previewRow(page, 4)).toContainText('列数应为 5 或 6，实际为 3')
  await expect(previewRow(page, 5)).toContainText('缺少单词')
  await expect(previewRow(page, 6)).toContainText('缺少释义')
  await expect(previewRow(page, 7)).toContainText('缺少单词；缺少释义')
  await expect(previewRow(page, 8)).toContainText('列数应为 5 或 6，实际为 1')
  await expect(previewRow(page, 2)).toContainText('有效')

  await page.locator('.batch-only-invalid').click()
  await expect(previewRows(page).locator('td:first-child')).toHaveText(['4', '5', '6', '7', '8'])

  await expect(page.locator('.batch-blockers')).toContainText('存在 5 行无效数据')
  await expect(submitButton(page)).toBeDisabled()
  await submitButton(page).click({ force: true })
  expect(payloads).toHaveLength(0)
})

test('refuses to submit without a book or without data rows', async ({ page }) => {
  await openBatchPage(page)
  const payloads = await answerBatch(page, (route) =>
    json(route, { success: true, data: { total: 0, created: 0, reused: 0 } }))

  await expect(page.locator('.batch-blockers')).toContainText('请选择教材')
  await expect(page.locator('.batch-blockers')).toContainText('没有可导入的数据行')
  await expect(submitButton(page)).toBeDisabled()

  await tsvInput(page).fill(validTsv)
  await expect(page.locator('.batch-blockers')).toContainText('请选择教材')
  await expect(page.locator('.batch-blockers')).not.toContainText('没有可导入的数据行')
  await expect(submitButton(page)).toBeDisabled()

  await selectBook(page)
  await tsvInput(page).fill('# only a comment\n\n')
  await expect(page.locator('.batch-blockers')).toContainText('没有可导入的数据行')
  await expect(submitButton(page)).toBeDisabled()

  await submitButton(page).click({ force: true })
  expect(payloads).toHaveLength(0)
})

test('refuses a batch over 500 entries and pages the preview', async ({ page }) => {
  await openBatchPage(page)
  const payloads = await answerBatch(page, (route) =>
    json(route, { success: true, data: { total: 0, created: 0, reused: 0 } }))

  const lines = Array.from({ length: 501 }, (_, index) => `word${index + 1}\t\t\t\t释义${index + 1}`)
  await selectBook(page)
  await tsvInput(page).fill(lines.join('\n'))

  await expect(page.locator('.batch-summary')).toContainText('数据行 501 条，有效 501 条，无效 0 条')
  await expect(page.locator('.batch-blockers')).toContainText('超过单批 500 条上限，请把超出的 1 条拆到下一批')
  await expect(submitButton(page)).toBeDisabled()
  // One page at a time, so a large paste does not render every row at once.
  await expect(previewRows(page)).toHaveCount(100)

  await submitButton(page).click({ force: true })
  expect(payloads).toHaveLength(0)
})

test('refuses a payload over 1 MiB counted in UTF-8 bytes', async ({ page }) => {
  await openBatchPage(page)
  const payloads = await answerBatch(page, (route) =>
    json(route, { success: true, data: { total: 0, created: 0, reused: 0 } }))

  // 400 entries of 1,000 Chinese characters each: about 400,000 characters, well
  // under 1 MiB as a string length, but three bytes each once encoded, which is
  // what the server's limit counts.
  const example = '词'.repeat(1000)
  const lines = Array.from({ length: 400 }, (_, index) => `word${index}\t\t\t\t释义\t${example}`)
  await selectBook(page)
  await tsvInput(page).fill(lines.join('\n'))

  await expect(page.locator('.batch-summary')).toContainText('数据行 400 条，有效 400 条，无效 0 条')
  await expect(page.locator('.batch-blockers')).toContainText('请求体超过 1 MiB，请拆成更小的批次')
  await expect(submitButton(page)).toBeDisabled()

  await submitButton(page).click({ force: true })
  expect(payloads).toHaveLength(0)
})

test('rejects a file over 1 MiB without reading it', async ({ page }) => {
  await openBatchPage(page)

  await page.locator('.batch-file-input').setInputFiles({
    name: 'too-large.tsv',
    mimeType: 'text/tab-separated-values',
    buffer: Buffer.alloc(1_048_577, 'a')
  })

  await expect(page.locator('.el-message--error')).toContainText('文件超过 1 MiB，请拆分后分批导入')
  await expect(tsvInput(page)).toHaveValue('')
})

test('shows server entry errors on the source lines they belong to', async ({ page }) => {
  await openBatchPage(page)
  await refuseBatch(page, 400, {
    message: '2 entries are invalid.',
    errors: [
      { index: 1, message: 'Meaning is required.' },
      { index: 2, message: 'Word is required.' }
    ]
  })

  // Entry 1 is on line 3 and entry 2 on line 5, because a comment and a blank
  // line sit between them.
  const text = 'apple\t\t\tn.\t苹果\n# comment\nbanana\t\t\tn.\t香蕉\n\ncherry\t\t\tn.\t樱桃'
  await selectBook(page)
  await tsvInput(page).fill(text)
  await submitButton(page).click()

  await expect(page.locator('.batch-server-summary')).toContainText('2 行未通过服务端校验，整批未写入')
  await expect(previewRows(page).locator('td:first-child')).toHaveText(['3', '5'])
  await expect(previewRow(page, 3)).toContainText('服务端：Meaning is required.')
  await expect(previewRow(page, 5)).toContainText('服务端：Word is required.')
  await expect(tsvInput(page)).toHaveValue(text)
  await expect(submitButton(page)).toBeDisabled()

  // The verdict was for that text; editing it withdraws the verdict.
  await tsvInput(page).fill(text.replace('香蕉', '香蕉 '))
  await expect(page.locator('.batch-server-summary')).toHaveCount(0)
  await expect(submitButton(page)).toBeEnabled()
})

test('passes a 400 without entry errors through with the server message', async ({ page }) => {
  await openBatchPage(page)
  await refuseBatch(page, 400, { message: 'A batch can contain at most 500 entries.' })

  await selectBook(page)
  await tsvInput(page).fill(validTsv)
  await submitButton(page).click()

  await expect(page.locator('.el-message--error')).toContainText('A batch can contain at most 500 entries.')
  await expect(tsvInput(page)).toHaveValue(validTsv)
})

test('ignores entry errors whose shape is not the documented one', async ({ page }) => {
  await openBatchPage(page)
  await refuseBatch(page, 400, {
    message: 'Malformed entry errors.',
    errors: [{ index: '1', message: 'Meaning is required.' }]
  })

  await selectBook(page)
  await tsvInput(page).fill(validTsv)
  await submitButton(page).click()

  // A string index is not an index; pointing at a row on its strength could
  // point at the wrong one.
  await expect(page.locator('.el-message--error')).toContainText('Malformed entry errors.')
  await expect(page.locator('.batch-server-summary')).toHaveCount(0)
})

const mappedStatuses = [
  { status: 413, expected: '请求体超过 1 MiB，请拆分为更小的批次后重试' },
  { status: 404, expected: '所选教材不存在，请刷新后重新选择' },
  { status: 409, expected: '该单词或词义与现有数据冲突' },
  { status: 422, expected: '所选教材已停用，无法导入新词义' },
  { status: 503, expected: '服务繁忙，整批未写入，请稍后重试' }
]

for (const { status, expected } of mappedStatuses) {
  test(`explains a ${status} in its own words and keeps the batch`, async ({ page }) => {
    await openBatchPage(page)
    await refuseBatch(page, status, { message: 'Server envelope message.' })

    await selectBook(page)
    await tsvInput(page).fill(validTsv)
    await submitButton(page).click()

    await expect(page.locator('.el-message--error')).toContainText(expected)
    await expect(page.locator('.el-message--error')).not.toContainText('Server envelope message.')
    await expect(tsvInput(page)).toHaveValue(validTsv)
    await expect(submitButton(page)).toBeEnabled()
  })
}

test('says a network failure leaves the outcome unknown and resubmission safe', async ({ page }) => {
  await openBatchPage(page)
  await page.route(batchRoute, (route) => route.abort('failed'))

  await selectBook(page)
  await tsvInput(page).fill(validTsv)
  await submitButton(page).click()

  await expect(page.locator('.el-message--error'))
    .toContainText('网络错误，无法确认是否已写入；重新提交同一批是安全的')
  await expect(tsvInput(page)).toHaveValue(validTsv)
})

test('redirects to the login page when the session has expired', async ({ page }) => {
  await openBatchPage(page)
  await refuseBatch(page, 401, { message: 'Unauthorized' })

  await selectBook(page)
  await tsvInput(page).fill(validTsv)
  await submitButton(page).click()

  await expect(page).toHaveURL(/#\/login/)
})

test('sends one request however often the button is clicked', async ({ page }) => {
  await openBatchPage(page)
  let release: () => void = () => {}
  const released = new Promise<void>((resolve) => { release = resolve })
  const payloads = await answerBatch(page, async (route) => {
    await released
    await json(route, { success: true, data: { total: 2, created: 2, reused: 0 } })
  })

  await selectBook(page)
  await tsvInput(page).fill(validTsv)
  const button = submitButton(page)
  await button.dblclick()
  await button.click({ force: true })

  await expect(button).toBeDisabled()
  await expect(tsvInput(page).locator('..')).toHaveClass(/is-disabled/)
  release()

  await expect(page.locator('.batch-result')).toBeVisible()
  expect(payloads).toHaveLength(1)
})
