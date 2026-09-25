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

async function selectFormat(page: Page, label: 'TSV' | 'CSV' | 'JSON') {
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

function positionHeader(page: Page) {
  return page.locator('.batch-preview .el-table__header th').first()
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

// One batch written as TSV and as JSON. Both carry values with surrounding
// whitespace and blank optional values; the JSON writes a blank optional value
// as an empty string, as null, and by leaving the field out.
const sameBatchTsv = [
  '  apple \t\t\tn.\t 苹果 \tI eat an apple.',
  'banana\t\t\t\t香蕉',
  'cherry\t/ˈtʃeri/\t\t\t樱桃\t  '
].join('\n')

const sameBatchItems = [
  { word: '  apple ', phoneticUk: '', phoneticUs: null, partOfSpeech: 'n.', meaning: ' 苹果 ', example: 'I eat an apple.' },
  { meaning: '香蕉', word: 'banana' },
  { word: 'cherry', phoneticUk: '/ˈtʃeri/', phoneticUs: '  ', meaning: '樱桃', example: null }
]

const sameBatchEntries = [
  { word: 'apple', partOfSpeech: 'n.', meaning: '苹果', example: 'I eat an apple.' },
  { word: 'banana', meaning: '香蕉' },
  { word: 'cherry', phoneticUk: '/ˈtʃeri/', meaning: '樱桃' }
]

test('imports JSON files and pasted JSON with the same payload as the same batch in TSV', async ({ page }) => {
  await openBatchPage(page)
  const payloads = await acceptBatch(page)
  await selectBook(page)

  await dataInput(page).fill(sameBatchTsv)
  await expect(page.locator('.batch-summary')).toContainText('数据行 3 条，有效 3 条，无效 0 条')
  await expect(positionHeader(page)).toHaveText('行号')
  await submitButton(page).click()
  await expect(dataInput(page)).toHaveValue('')

  const pretty = JSON.stringify(sameBatchItems, null, 2)
  const files = [
    { name: 'words.json', buffer: Buffer.from(`\uFEFF${pretty.replaceAll('\n', '\r\n')}`, 'utf-8') },
    { name: 'WORDS.JSON', buffer: Buffer.from(JSON.stringify(sameBatchItems), 'utf-8') }
  ]
  for (const { name, buffer } of files) {
    await selectFormat(page, 'TSV')
    await chooseFile(page, name, buffer)
    await expect(selectedFormat(page)).toHaveText('JSON')
    await expect(page.locator('.batch-summary')).toContainText('数据行 3 条，有效 3 条，无效 0 条')
    await expect(positionHeader(page)).toHaveText('序号')
    await expect(previewRows(page).locator('td:first-child')).toHaveText(['1', '2', '3'])
    await submitButton(page).click()
    await expect(dataInput(page)).toHaveValue('')
  }

  await selectFormat(page, 'JSON')
  await expect(dataInput(page)).toHaveAttribute('placeholder', '[{"word":"apple","meaning":"苹果"}]')
  await dataInput(page).fill(pretty)
  await expect(page.locator('.batch-summary')).toContainText('数据行 3 条，有效 3 条，无效 0 条')
  await submitButton(page).click()
  await expect(page.locator('.batch-result')).toBeVisible()

  const expected = { bookId: starterBook.id, entries: sameBatchEntries }
  expect(payloads).toEqual([expected, expected, expected, expected])
})

const fileLevelErrors = [
  {
    name: 'a top-level object',
    text: '{"word":"apple","meaning":"苹果"}',
    expected: /^JSON 顶层必须是数组$/
  },
  {
    name: 'a top-level string',
    text: '"apple"',
    expected: /^JSON 顶层必须是数组$/
  },
  {
    name: 'a full { bookId, entries } payload',
    text: JSON.stringify({ bookId: starterBook.id, entries: [{ word: 'apple', meaning: '苹果' }] }),
    expected: /^JSON 顶层必须是数组；不接受 \{ bookId, entries \} 载荷，请只提供 entries 数组；教材以页面选择为准$/
  },
  {
    name: 'a syntax error',
    text: '[{"word": "a",}]',
    expected: /^JSON 语法错误：.*position 14/
  }
]

for (const { name, text, expected } of fileLevelErrors) {
  test(`refuses JSON with ${name} as a whole`, async ({ page }) => {
    await openBatchPage(page)
    const payloads = await acceptBatch(page)
    await selectBook(page)

    await selectFormat(page, 'JSON')
    await dataInput(page).fill(text)

    await expect(page.locator('.batch-parse-error .el-alert__title')).toHaveText(expected)
    await expect(page.locator('.batch-preview')).toHaveCount(0)
    await expect(page.locator('.batch-blockers li')).toHaveText([expected])
    await expect(submitButton(page)).toBeDisabled()
    await submitButton(page).click({ force: true })
    expect(payloads).toHaveLength(0)
  })
}

test('marks items invalid by their number and shows the values they were refused for', async ({ page }) => {
  await openBatchPage(page)
  const payloads = await acceptBatch(page)
  await selectBook(page)

  const items = [
    '{"word":"apple","meaning":"苹果"}',
    '{"word":"apple","meaning":"苹果","Word":"x","__proto__":{"meaning":"y"}}',
    '{"word":"banana","meaning":1,"example":true,"phoneticUk":["a"]}',
    '[]',
    'null',
    '"cherry"',
    '{"meaning":"枣"}',
    '{"word":"egg","meaning":null}',
    '{"word":"fig","mean":"无花果"}'
  ]
  await chooseFile(page, 'items.json', Buffer.from(`[\n${items.join(',\n')}\n]`, 'utf-8'))

  await expect(positionHeader(page)).toHaveText('序号')
  await expect(page.locator('.batch-summary')).toContainText('数据行 9 条，有效 1 条，无效 8 条')
  await expect(previewRows(page).locator('td:first-child')).toHaveText(['1', '2', '3', '4', '5', '6', '7', '8', '9'])
  await expect(previewRow(page, 1)).toContainText('有效')
  await expect(previewRow(page, 2)).toContainText('未知字段：Word、__proto__')
  await expect(previewRow(page, 3)).toContainText('字段 phoneticUk 应为字符串；字段 meaning 应为字符串；字段 example 应为字符串')
  await expect(previewRow(page, 3).locator('td')).toHaveText(
    ['3', 'banana', '["a"]', '', '', '1', 'true', /字段 phoneticUk 应为字符串/])
  for (const position of [4, 5, 6]) {
    await expect(previewRow(page, position)).toContainText('该项不是对象')
  }
  await expect(previewRow(page, 7)).toContainText('缺少单词')
  await expect(previewRow(page, 8)).toContainText('缺少释义')
  await expect(previewRow(page, 9)).toContainText('未知字段：mean；缺少释义')

  await expect(page.locator('.batch-blockers')).toContainText('存在 8 行无效数据')
  await expect(submitButton(page)).toBeDisabled()
  await submitButton(page).click({ force: true })
  expect(payloads).toHaveLength(0)
})

test('says an empty array has no data rows and leaves an empty text area without an error', async ({ page }) => {
  await openBatchPage(page)
  await selectBook(page)

  await selectFormat(page, 'JSON')
  await expect(page.locator('.batch-parse-error')).toHaveCount(0)
  await expect(page.locator('.batch-blockers')).toContainText('没有可导入的数据行，请粘贴 JSON 文本或选择文件')

  await dataInput(page).fill(' [ ] ')
  await expect(page.locator('.batch-parse-error')).toHaveCount(0)
  await expect(page.locator('.batch-blockers')).toContainText('没有可导入的数据行，请粘贴 JSON 文本或选择文件')
  await expect(submitButton(page)).toBeDisabled()
})

test('shows server entry errors on the items their index points to', async ({ page }) => {
  await openBatchPage(page)
  await page.route(batchRoute, (route) => json(route, {
    success: false,
    message: '2 entries are invalid.',
    errors: [
      { index: 1, message: 'Meaning is required.' },
      { index: 2, message: 'Word is required.' }
    ]
  }, 400))

  await selectBook(page)
  await selectFormat(page, 'JSON')
  await dataInput(page).fill(JSON.stringify([
    { word: 'apple', meaning: '苹果' },
    { word: 'banana', meaning: '香蕉' },
    { word: 'cherry', meaning: '樱桃' }
  ], null, 2))
  await submitButton(page).click()

  await expect(page.locator('.batch-server-summary')).toContainText('2 行未通过服务端校验，整批未写入')
  await expect(previewRows(page).locator('td:first-child')).toHaveText(['2', '3'])
  await expect(previewRow(page, 2)).toContainText('服务端：Meaning is required.')
  await expect(previewRow(page, 3)).toContainText('服务端：Word is required.')
})

test('refuses a JSON file that is not UTF-8 and an unsupported JSON Lines file', async ({ page }) => {
  await openBatchPage(page)
  await selectBook(page)
  await dataInput(page).fill('apple\t\t\t\t苹果')

  const gbk = Buffer.concat([Buffer.from('[{"word":"apple","meaning":"'), gbkApple, Buffer.from('"}]')])
  await chooseFile(page, 'words.json', gbk)
  await expect(page.locator('.el-message--error').last()).toContainText('文件不是 UTF-8 编码')
  await expect(dataInput(page)).toHaveValue('apple\t\t\t\t苹果')
  await expect(selectedFormat(page)).toHaveText('TSV')

  await chooseFile(page, 'words.jsonl', Buffer.from('{"word":"apple","meaning":"苹果"}\n', 'utf-8'))
  await expect(page.locator('.el-message--error').last())
    .toContainText('不支持的文件类型，请选择 .tsv、.txt 或 .csv 文件，也可以选择 .json 文件')
  await expect(dataInput(page)).toHaveValue('apple\t\t\t\t苹果')
  await expect(selectedFormat(page)).toHaveText('TSV')
})
