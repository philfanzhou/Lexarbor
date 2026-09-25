import { strToU8, zipSync } from 'fflate'
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
const workerChunk = /\/assets\/xlsxWorker-[^/]*\.js$/

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

function selectedFormat(page: Page) {
  return page.locator('.batch-format .el-radio-button.is-active')
}

function dataInput(page: Page) {
  return page.locator('.batch-input textarea')
}

function chooseFile(page: Page, name: string, buffer: Buffer | Uint8Array) {
  return page.locator('.batch-file-input').setInputFiles({ name, mimeType: 'application/octet-stream', buffer: Buffer.from(buffer) })
}

function submitButton(page: Page) {
  return page.getByRole('button', { name: '提交导入' })
}

function removeFileButton(page: Page) {
  return page.locator('.batch-remove-file')
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

// A minimal SpreadsheetML workbook, written here so that no binary fixture is
// committed. Cells must be in row and column order, as Excel writes them.

const mainNs = 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'
const relNs = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships'
const xmlDeclaration = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'

function escapeXml(value: string) {
  return value.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;')
}

/** Inline string. */
function inline(ref: string, value: string) {
  return `<c r="${ref}" t="inlineStr"><is><t xml:space="preserve">${escapeXml(value)}</t></is></c>`
}

function worksheet(rows: string[], extra = '') {
  return `${xmlDeclaration}<worksheet xmlns="${mainNs}" xmlns:r="${relNs}"><sheetData>${rows.join('')}</sheetData>${extra}</worksheet>`
}

function row(number: number, cells: string[]) {
  return `<row r="${number}">${cells.join('')}</row>`
}

interface WorkbookOptions {
  sheets: { name: string; xml: string }[]
  sharedStrings?: string[]
  /** More entries, such as padding, added to the archive as they are. */
  extraEntries?: Record<string, Uint8Array>
}

function workbook({ sheets, sharedStrings = [], extraEntries = {} }: WorkbookOptions): Uint8Array {
  const files: Record<string, Uint8Array> = {
    '[Content_Types].xml': strToU8(`${xmlDeclaration}<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">`
      + '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
      + '<Default Extension="xml" ContentType="application/xml"/>'
      + '<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>'
      + sheets.map((_, index) => `<Override PartName="/xl/worksheets/sheet${index + 1}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>`).join('')
      + '<Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/>'
      + '<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>'
      + '</Types>'),
    '_rels/.rels': strToU8(`${xmlDeclaration}<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">`
      + `<Relationship Id="rId1" Type="${relNs}/officeDocument" Target="xl/workbook.xml"/></Relationships>`),
    'xl/workbook.xml': strToU8(`${xmlDeclaration}<workbook xmlns="${mainNs}" xmlns:r="${relNs}"><sheets>`
      + sheets.map((sheet, index) => `<sheet name="${escapeXml(sheet.name)}" sheetId="${index + 1}" r:id="rId${index + 1}"/>`).join('')
      + '</sheets></workbook>'),
    'xl/_rels/workbook.xml.rels': strToU8(`${xmlDeclaration}<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">`
      + sheets.map((_, index) => `<Relationship Id="rId${index + 1}" Type="${relNs}/worksheet" Target="worksheets/sheet${index + 1}.xml"/>`).join('')
      + `<Relationship Id="rIdStrings" Type="${relNs}/sharedStrings" Target="sharedStrings.xml"/>`
      + `<Relationship Id="rIdStyles" Type="${relNs}/styles" Target="styles.xml"/>`
      + '</Relationships>'),
    'xl/sharedStrings.xml': strToU8(`${xmlDeclaration}<sst xmlns="${mainNs}" count="${sharedStrings.length}" uniqueCount="${sharedStrings.length}">`
      + sharedStrings.map((value) => `<si><t xml:space="preserve">${escapeXml(value)}</t></si>`).join('')
      + '</sst>'),
    // Style 1 is the built-in date format 14, which marks a number as a date.
    'xl/styles.xml': strToU8(`${xmlDeclaration}<styleSheet xmlns="${mainNs}">`
      + '<cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>'
      + '<xf numFmtId="14" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/></cellXfs>'
      + '</styleSheet>')
  }
  sheets.forEach((sheet, index) => {
    files[`xl/worksheets/sheet${index + 1}.xml`] = strToU8(sheet.xml)
  })
  return zipSync({ ...files, ...extraEntries }, { level: 9 })
}

/** A workbook whose first sheet holds a header and the given data rows, as inline strings from row 1. */
function simpleWorkbook(header: string[], data: string[][] = []) {
  const columns = 'ABCDEFGH'
  const rows = [header, ...data].map((values, index) =>
    row(index + 1, values.flatMap((value, column) =>
      value === '' ? [] : [inline(`${columns[column]}${index + 1}`, value)])))
  return workbook({ sheets: [{ name: 'Sheet1', xml: worksheet(rows) }] })
}

// The first sheet starts two rows down, orders its header differently from the
// TSV columns and leaves phonetic_uk out, and mixes shared and inline strings,
// numbers, booleans, dates, formulas with cached values, a merged cell, an
// error value, and a blank row in the middle.
const sharedStrings = ['meaning', 'example', 'part_of_speech', 'phonetic_us', '苹果', 'n.', '香蕉', 'banana']
const shared = (ref: string, value: string, style = '') =>
  `<c r="${ref}"${style} t="s"><v>${sharedStrings.indexOf(value)}</v></c>`

const firstSheet = worksheet([
  row(3, [shared('A3', 'meaning'), inline('B3', 'word'), shared('C3', 'example'), shared('D3', 'part_of_speech'), shared('E3', 'phonetic_us')]),
  row(4, [shared('A4', '苹果'), inline('B4', ' apple '), '<c r="C4" t="str"><f>"I eat "&amp;"an apple."</f><v>I eat an apple.</v></c>', shared('D4', 'n.')]),
  row(5, [shared('A5', '香蕉'), shared('B5', 'banana'), '<c r="C5"><v>0.1</v></c>', '<c r="E5"><v>2</v></c>']),
  row(6, [inline('A6', '樱桃'), inline('B6', 'cherry'), '<c r="C6" t="b"><v>1</v></c>', '<c r="D6" t="b"><v>0</v></c>']),
  row(8, [inline('A8', '枣'), inline('B8', 'date'), '<c r="C8" s="1"><v>45000</v></c>', '<c r="E8"><f>1+1</f><v>2</v></c>']),
  row(9, [inline('A9', '蛋'), inline('B9', 'egg'), '<c r="C9" s="1"><v>45000.5</v></c>']),
  row(10, [inline('A10', '无花果'), inline('B10', 'fig'), inline('D10', 'n.')]),
  row(11, [inline('A11', '葡萄'), inline('B11', 'grape'), '<c r="C11" t="e"><v>#N/A</v></c>', '<c r="D11"/>'])
], '<mergeCells count="1"><mergeCell ref="D10:D11"/></mergeCells>')

const secondSheet = worksheet([row(1, [inline('A1', 'not read')])])

const fullWorkbook = workbook({
  sheets: [{ name: '词表', xml: firstSheet }, { name: 'Other', xml: secondSheet }],
  sharedStrings
})

// The same batch as TSV: word, phonetic_uk, phonetic_us, part_of_speech, meaning, example.
const sameBatchTsv = [
  ' apple \t\t\tn.\t苹果\tI eat an apple.',
  'banana\t\t2\t\t香蕉\t0.1',
  'cherry\t\t\tFALSE\t樱桃\tTRUE',
  'date\t\t2\t\t枣\t2023-03-15',
  'egg\t\t\t\t蛋\t2023-03-15T12:00:00',
  'fig\t\t\tn.\t无花果',
  'grape\t\t\t\t葡萄'
].join('\n')

const sameBatchEntries = [
  { word: 'apple', partOfSpeech: 'n.', meaning: '苹果', example: 'I eat an apple.' },
  { word: 'banana', phoneticUs: '2', meaning: '香蕉', example: '0.1' },
  { word: 'cherry', partOfSpeech: 'FALSE', meaning: '樱桃', example: 'TRUE' },
  { word: 'date', phoneticUs: '2', meaning: '枣', example: '2023-03-15' },
  { word: 'egg', meaning: '蛋', example: '2023-03-15T12:00:00' },
  { word: 'fig', partOfSpeech: 'n.', meaning: '无花果' },
  { word: 'grape', meaning: '葡萄' }
]

test('imports the first sheet of a workbook with the same payload as the same batch in TSV', async ({ page }) => {
  await openBatchPage(page)
  const payloads = await acceptBatch(page)
  await selectBook(page)

  await dataInput(page).fill(sameBatchTsv)
  await expect(page.locator('.batch-summary')).toContainText('数据行 7 条，有效 7 条，无效 0 条')
  await submitButton(page).click()
  await expect(dataInput(page)).toHaveValue('')

  await chooseFile(page, 'Words.XLSX', fullWorkbook)
  await expect(selectedFormat(page)).toHaveText('Excel')
  await expect(page.locator('.batch-sheet-notice')).toContainText('工作簿共有 2 个工作表，只读取第一个「词表」')
  await expect(page.locator('.batch-summary')).toContainText('数据行 7 条，有效 7 条，无效 0 条')
  // Sheet row numbers: the header is on row 3 and row 7 is blank.
  await expect(previewRows(page).locator('td:first-child')).toHaveText(['4', '5', '6', '8', '9', '10', '11'])
  await expect(previewRow(page, 4).locator('td')).toHaveText(['4', 'apple', '', '', 'n.', '苹果', 'I eat an apple.', '有效'])
  await expect(previewRow(page, 5).locator('td')).toHaveText(['5', 'banana', '', '2', '', '香蕉', '0.1', '有效'])
  await expect(previewRow(page, 6).locator('td')).toHaveText(['6', 'cherry', '', '', 'FALSE', '樱桃', 'TRUE', '有效'])
  await expect(previewRow(page, 8).locator('td')).toHaveText(['8', 'date', '', '2', '', '枣', '2023-03-15', '有效'])
  await expect(previewRow(page, 9).locator('td')).toHaveText(['9', 'egg', '', '', '', '蛋', '2023-03-15T12:00:00', '有效'])
  await expect(previewRow(page, 11).locator('td')).toHaveText(['11', 'grape', '', '', '', '葡萄', '', '有效'])
  await expect(previewRow(page, 10)).not.toContainText('not read')

  await submitButton(page).click()
  await expect(page.locator('.batch-result')).toBeVisible()
  // A successful import clears the file as well as the text.
  await expect(removeFileButton(page)).toHaveCount(0)
  await expect(selectedFormat(page)).toHaveText('TSV')
  await expect(dataInput(page)).toBeEnabled()

  expect(payloads).toEqual([
    { bookId: starterBook.id, entries: sameBatchEntries },
    { bookId: starterBook.id, entries: sameBatchEntries }
  ])
})

const headerErrors = [
  {
    name: 'a missing meaning column',
    header: ['word', 'example'],
    data: [['apple', 'I eat an apple.']],
    expected: '表头缺少必需的 meaning 列'
  },
  {
    name: 'an unknown header',
    header: ['word', 'meanings'],
    data: [['apple', '苹果']],
    expected: '第 2 列表头「meanings」无法识别'
  },
  {
    name: 'a value under a blank header',
    header: ['word', 'meaning'],
    data: [['apple', '苹果', 'n.']],
    expected: '第 3 列表头为空，但该列有数据'
  }
]

for (const { name, header, data, expected } of headerErrors) {
  test(`refuses a workbook with ${name} as a whole`, async ({ page }) => {
    await openBatchPage(page)
    const payloads = await acceptBatch(page)
    await selectBook(page)

    await chooseFile(page, 'words.xlsx', simpleWorkbook(header, data))

    await expect(page.locator('.batch-parse-error')).toContainText(expected)
    await expect(page.locator('.batch-preview')).toHaveCount(0)
    await expect(page.locator('.batch-blockers')).toContainText(expected)
    await expect(submitButton(page)).toBeDisabled()
    await submitButton(page).click({ force: true })
    expect(payloads).toHaveLength(0)
  })
}

test('ignores a blank column beyond the header and says a header alone has no data rows', async ({ page }) => {
  await openBatchPage(page)
  await selectBook(page)

  await chooseFile(page, 'words.xlsx', simpleWorkbook(['word', 'meaning', ' '], [['apple', '苹果', '  ']]))
  await expect(page.locator('.batch-summary')).toContainText('数据行 1 条，有效 1 条，无效 0 条')
  await expect(page.locator('.batch-sheet-notice')).toHaveCount(0)

  await chooseFile(page, 'header-only.xlsx', simpleWorkbook(['word', 'meaning']))
  await expect(page.locator('.batch-parse-error')).toHaveCount(0)
  await expect(page.locator('.batch-blockers')).toContainText('没有可导入的数据行')
  await expect(submitButton(page)).toBeDisabled()
})

/** 40 MiB of zeros, which deflate compresses to about 40 KiB. */
const padding = new Uint8Array(40 * 1_048_576)

/** Rewrites the uncompressed size the central directory declares for one entry. */
function forgeDeclaredSize(zip: Uint8Array, entryName: string, declared: number) {
  const bytes = Buffer.from(zip)
  const name = Buffer.from(entryName)
  for (let offset = bytes.length - 46; offset >= 0; offset--) {
    if (bytes.readUInt32LE(offset) !== 0x02014b50) continue
    const nameLength = bytes.readUInt16LE(offset + 28)
    if (bytes.subarray(offset + 46, offset + 46 + nameLength).equals(name)) {
      bytes.writeUInt32LE(declared, offset + 24)
      return bytes
    }
  }
  throw new Error(`No central directory entry for ${entryName}`)
}

test('refuses a workbook that unzips to more than 32 MiB, whether or not its sizes are true', async ({ page }) => {
  await openBatchPage(page)
  await selectBook(page)

  const bomb = workbook({
    sheets: [{ name: 'Sheet1', xml: worksheet([row(1, [inline('A1', 'word'), inline('B1', 'meaning')])]) }],
    extraEntries: { 'xl/media/padding.bin': padding }
  })
  expect(bomb.length).toBeLessThan(1_048_576)

  const files = [
    { name: 'bomb.xlsx', buffer: bomb, expected: '解压后的内容超过 32 MiB' },
    {
      name: 'forged.xlsx',
      buffer: forgeDeclaredSize(bomb, 'xl/media/padding.bin', 1000),
      expected: '压缩包中「xl/media/padding.bin」登记的大小与内容不符'
    }
  ]
  for (const { name, buffer, expected } of files) {
    await chooseFile(page, name, buffer)
    await expect(page.locator('.batch-parse-error')).toContainText(`无法读取 Excel 文件：${expected}`)
    await expect(submitButton(page)).toBeDisabled()

    // The page stays usable: the book can change and the file can be removed.
    await selectBook(page, 1)
    await expect(page.locator('.batch-book-select')).toContainText(otherBook.bookName)
    await selectBook(page, 0)
    await removeFileButton(page).click()
    await expect(page.locator('.batch-parse-error')).toHaveCount(0)
    await expect(selectedFormat(page)).toHaveText('TSV')
  }
})

test('refuses bytes that are not a workbook, a legacy .xls, and an oversized .xlsx', async ({ page }) => {
  await openBatchPage(page)

  await chooseFile(page, 'words.xlsx', Buffer.from('word,meaning\napple,苹果\n', 'utf-8'))
  await expect(page.locator('.batch-parse-error')).toContainText('无法读取 Excel 文件')
  await expect(page.locator('.batch-preview')).toHaveCount(0)
  await removeFileButton(page).click()

  await chooseFile(page, 'words.xls', simpleWorkbook(['word', 'meaning'], [['apple', '苹果']]))
  await expect(page.locator('.el-message--error').last())
    .toContainText('不支持的文件类型，请选择 .tsv、.txt 或 .csv 文件，也可以选择 .json 文件或 .xlsx 文件')
  await expect(removeFileButton(page)).toHaveCount(0)

  await chooseFile(page, 'too-large.xlsx', Buffer.alloc(1_048_577, 'a'))
  await expect(page.locator('.el-message--error').last()).toContainText('文件超过 1 MiB，请拆分后分批导入')
  await expect(removeFileButton(page)).toHaveCount(0)
  await expect(selectedFormat(page)).toHaveText('TSV')
})

test('locks the text and format while a workbook is loaded, and removing it returns to pasting', async ({ page }) => {
  await openBatchPage(page)
  await selectBook(page)
  await page.locator('.batch-format').getByText('CSV', { exact: true }).click()
  await dataInput(page).fill('word,meaning\napple,苹果')

  await chooseFile(page, 'words.xlsx', simpleWorkbook(['word', 'meaning'], [['banana', '香蕉']]))
  await expect(page.locator('.batch-summary')).toContainText('数据行 1 条，有效 1 条，无效 0 条')
  await expect(selectedFormat(page)).toHaveText('Excel')
  await expect(dataInput(page)).toBeDisabled()
  await expect(dataInput(page)).toHaveValue('')
  await expect(dataInput(page)).toHaveAttribute('placeholder', 'words.xlsx')
  for (const label of ['TSV', 'CSV', 'JSON', 'Excel']) {
    await expect(page.locator('.batch-format .el-radio-button').filter({ hasText: label })).toHaveClass(/is-disabled/)
  }

  await removeFileButton(page).click()
  await expect(selectedFormat(page)).toHaveText('TSV')
  await expect(dataInput(page)).toBeEnabled()
  await expect(dataInput(page)).toHaveValue('')
  await expect(page.locator('.batch-preview')).toHaveCount(0)
  await expect(page.locator('.batch-blockers')).toContainText('没有可导入的数据行，请粘贴 TSV 文本或选择文件')

  // A text file chosen after a workbook replaces it.
  await chooseFile(page, 'words.xlsx', simpleWorkbook(['word', 'meaning'], [['banana', '香蕉']]))
  await expect(removeFileButton(page)).toBeVisible()
  await chooseFile(page, 'words.csv', Buffer.from('word,meaning\ncherry,樱桃\n', 'utf-8'))
  await expect(removeFileButton(page)).toHaveCount(0)
  await expect(selectedFormat(page)).toHaveText('CSV')
  await expect(dataInput(page)).toHaveValue('word,meaning\ncherry,樱桃\n')
})

test('shows server entry errors on sheet rows and withdraws them when the file or book changes', async ({ page }) => {
  await openBatchPage(page)
  await page.route(batchRoute, (route) => json(route, {
    success: false,
    message: '1 entry is invalid.',
    errors: [{ index: 1, message: 'Word is required.' }]
  }, 400))

  // Entry 1 is on sheet row 5: the header is on row 2 and row 4 is blank.
  const cells = worksheet([
    row(2, [inline('A2', 'word'), inline('B2', 'meaning')]),
    row(3, [inline('A3', 'apple'), inline('B3', '苹果')]),
    row(5, [inline('A5', 'banana'), inline('B5', '香蕉')])
  ])
  const book = workbook({ sheets: [{ name: 'Sheet1', xml: cells }] })
  const serverSummary = page.locator('.batch-server-summary')

  await selectBook(page)
  await chooseFile(page, 'words.xlsx', book)
  await submitButton(page).click()
  await expect(serverSummary).toContainText('1 行未通过服务端校验，整批未写入')
  await expect(previewRows(page).locator('td:first-child')).toHaveText(['5'])
  await expect(previewRow(page, 5)).toContainText('服务端：Word is required.')

  await chooseFile(page, 'words.xlsx', book)
  await expect(serverSummary).toHaveCount(0)
  await expect(submitButton(page)).toBeEnabled()

  await submitButton(page).click()
  await expect(serverSummary).toBeVisible()
  await selectBook(page, 1)
  await expect(serverSummary).toHaveCount(0)
})

test('loads the Excel reader only once an .xlsx file is chosen', async ({ page }) => {
  const workerRequests: string[] = []
  page.on('request', (request) => {
    if (workerChunk.test(request.url())) workerRequests.push(request.url())
  })

  await openBatchPage(page)
  await selectBook(page)
  await dataInput(page).fill('apple\t\t\t\t苹果')
  await chooseFile(page, 'words.csv', Buffer.from('word,meaning\napple,苹果\n', 'utf-8'))
  await expect(page.locator('.batch-summary')).toContainText('数据行 1 条')
  expect(workerRequests).toHaveLength(0)

  await chooseFile(page, 'words.xlsx', simpleWorkbook(['word', 'meaning'], [['apple', '苹果']]))
  await expect(page.locator('.batch-summary')).toContainText('数据行 1 条，有效 1 条，无效 0 条')
  expect(workerRequests).toHaveLength(1)
})

test('gives up on a workbook after 15 seconds and terminates its reader', async ({ page }) => {
  // A reader that never answers stands in for a workbook that takes too long.
  await page.route(workerChunk, (route) => route.fulfill({
    contentType: 'text/javascript',
    body: 'self.onmessage = () => { for (;;) {} }'
  }))
  await page.clock.install()
  await openBatchPage(page)

  await chooseFile(page, 'slow.xlsx', simpleWorkbook(['word', 'meaning'], [['apple', '苹果']]))
  await expect(page.locator('.batch-blockers')).toContainText('正在读取 Excel 文件')

  await page.clock.runFor(14_000)
  await expect(page.locator('.batch-parse-error')).toHaveCount(0)
  await page.clock.runFor(1_000)
  await expect(page.locator('.batch-parse-error')).toContainText('Excel 解析超时，文件可能已损坏或过大')
  await removeFileButton(page).click()
  await expect(selectedFormat(page)).toHaveText('TSV')
})
