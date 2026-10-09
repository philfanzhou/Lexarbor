import AxeBuilder from '@axe-core/playwright'
import { strToU8, zipSync } from 'fflate'
import { expect, test, type Page, type Route } from '@playwright/test'
import { mockAntiforgery } from './support/antiforgery'

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


const books = [{ id: 'book-a', bookName: 'Book A', status: true }, { id: 'book-b', bookName: 'Book B', status: true }]
const units = [{ id: 'unit-2', bookId: 'book-a', number: 2, title: 'Two', meaningCount: 0 }, { id: 'unit-5', bookId: 'book-a', number: 5, title: 'Five', meaningCount: 0 }]
const json = (route: Route, data: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(data) })
type Format = 'tsv' | 'csv' | 'json' | 'xlsx'
type Input = { word: string; meaning: string; unit?: string | null; section?: string | null; entryKind?: string | number | null }
const preview = (page: Page) => page.locator('.batch-preview .el-table__body .el-table__row')
const submit = (page: Page) => page.getByRole('button', { name: '提交导入' })
async function pick(page: Page, selector: string, label: string) {
  await page.locator(selector).click()
  await page.getByRole('option', { name: label, exact: true }).click()
}
async function setup(page: Page, path = '/import/batch') {
  await page.route('**/admin/auth/session', route => json(route, { success: true, data: { username: 'admin', roles: ['admin'] } }))
  await mockAntiforgery(page)
  await page.route('**/admin/system/version', route => json(route, { success: true, data: { version: '1', revision: null, channel: 'test' } }))
  await page.route('**/api/vocabulary-books/all', route => json(route, { success: true, data: { books } }))
  await page.route(/\/admin\/vocabulary-books\/[^/]+\/units$/, route => json(route, { success: true, data: { units: route.request().url().includes('book-a') ? units : [{ ...units[0], id: 'other-2', bookId: 'book-b' }] } }))
  await page.goto(`/#${path}`)
}
async function input(page: Page, format: Format, data: Input[]) {
  const cells = data.map(row => [row.word, row.meaning, row.unit ?? '', row.section ?? '', String(row.entryKind ?? '')])
  if (format === 'xlsx') {
    await page.locator('.batch-file-input').setInputFiles({ name: 'vocabulary.xlsx', mimeType: 'application/octet-stream', buffer: Buffer.from(simpleWorkbook(['word', 'meaning', 'unit', 'section', 'entry_kind'], cells)) })
  } else {
    const text = format === 'json' ? JSON.stringify(data) : format === 'tsv'
      ? data.map(row => [row.word, '', '', '', row.meaning, '', row.unit ?? '', row.section ?? '', row.entryKind ?? ''].join('\t')).join('\n')
      : ['word,meaning,unit,section,entry_kind', ...cells.map(row => row.join(','))].join('\n')
    await page.locator('.batch-file-input').setInputFiles({ name: `vocabulary.${format}`, mimeType: 'text/plain', buffer: Buffer.from(text) })
  }
  await expect(preview(page)).toHaveCount(data.length)
}

for (const format of ['tsv', 'csv', 'json', 'xlsx'] as const) {
  for (const mode of ['word', 'phrase'] as const) {
    test(`${format}: ${mode} mode follows the full decision table and sends the final preview`, async ({ page }) => {
      const payloads: unknown[] = []
      await page.route('**/admin/vocabulary/batch', route => {
        payloads.push(route.request().postDataJSON())
        return json(route, { success: true, data: { total: 5, created: 3, reused: 2 } })
      })
      await setup(page, `/import/batch/${mode === 'word' ? 'words' : 'phrases'}`)
      await pick(page, '.batch-book-select', 'Book A')
      const item = { word: mode === 'word' ? 'apple' : 'take off', meaning: 'meaning' }
      await input(page, format, [item, { ...item, section: 'A' }])
      await expect(submit(page)).toBeDisabled()
      await expect(preview(page).nth(0)).toContainText('有类别但未填写单元')
      await expect(preview(page).nth(1)).toContainText('有分节但未填写单元')
      await pick(page, '.batch-default-unit', '第 2 单元 Two')
      const opposite = mode === 'word' ? 'phrase' : 'word'
      const bad = [
        { ...item, entryKind: opposite }, { ...item, entryKind: 'Word' }, { ...item, entryKind: 'invalid' },
        { ...item, unit: '02' }, { ...item, unit: 'Unit 2' }, { ...item, unit: '99' }, { ...item, section: 'a' }
      ]
      await input(page, format, bad)
      for (const [index, reason] of ['请切换混合词汇模式', '类别应为 word 或 phrase：Word', '类别应为 word 或 phrase：invalid', '未知单元：02', '未知单元：Unit 2', '未知单元：99', '分节应为 A 或 B：a'].entries()) {
        await expect(preview(page).nth(index)).toContainText(reason)
      }
      await expect(submit(page)).toBeDisabled()
      await submit(page).click({ force: true })
      expect(payloads).toHaveLength(0)
      const valid: Input[] = [
        { ...item, section: 'A' }, { ...item, unit: '5', entryKind: mode },
        { ...item, unit: '2', entryKind: ` ${mode} ` }, { ...item, unit: null, entryKind: null }, { ...item, section: 'A' }
      ]
      await input(page, format, valid)
      await expect(submit(page)).toBeEnabled()
      await expect(page.locator('.batch-category-summary')).toContainText(`${mode === 'word' ? '单词' : '短语'} 5 条`)
      await expect(page.locator('.batch-category-summary')).toContainText('未分类 0 条')
      const expectedUnits = ['2 · Two', '5 · Five', '2 · Two', '2 · Two', '2 · Two']
      for (const [index, label] of expectedUnits.entries()) {
        const cells = preview(page).nth(index).locator('td')
        await expect(cells.nth(7)).toHaveText(label)
        await expect(cells.nth(12)).toHaveText(index === 0 || index === 3 || index === 4 ? `${mode}（页面默认）` : mode)
        await expect(cells.nth(13)).toHaveText(index === 1 || index === 2 ? '文件' : '页面默认')
      }
      await submit(page).click()
      await expect(page.locator('.batch-result')).toContainText('新增与复用统计的是词义数量')
      expect(payloads).toEqual([{ bookId: 'book-a', entries: valid.map((row, index) => ({
        word: item.word, meaning: item.meaning, unitId: index === 1 ? 'unit-5' : 'unit-2', entryKind: mode, ...(row.section ? { section: row.section } : {})
      })) }])
    })
  }
}

for (const format of ['tsv', 'csv', 'json', 'xlsx'] as const) {
  test(`${format}: mixed mode preserves row kinds and legacy unassigned rows`, async ({ page }) => {
    const payloads: unknown[] = []
    await page.route('**/admin/vocabulary/batch', route => { payloads.push(route.request().postDataJSON()); return json(route, { success: true, data: { total: 3, created: 3, reused: 0 } }) })
    await setup(page)
    await pick(page, '.batch-book-select', 'Book A')
    await input(page, format, [{ word: 'apple', meaning: 'fruit', unit: '2', entryKind: 'word' }, { word: 'take off', meaning: 'leave', unit: '5', entryKind: 'phrase' }, { word: 'space in text', meaning: 'uncategorized', unit: null, entryKind: null }])
    await expect(page.locator('.batch-category-summary')).toContainText('单词 1 条 · 短语 1 条 · 未分类 1 条')
    await expect(page.locator('.batch-unclassified')).toContainText('未分类行不会显示在短语管理')
    await expect(page.locator('.batch-default-unit')).toHaveCount(0)
    await submit(page).click()
    // Polled: the write first waits for the antiforgery token, so the captured
    // payload can trail the click on a loaded CI runner.
    await expect.poll(() => payloads).toEqual([{ bookId: 'book-a', entries: [{ word: 'apple', meaning: 'fruit', unitId: 'unit-2', entryKind: 'word' }, { word: 'take off', meaning: 'leave', unitId: 'unit-5', entryKind: 'phrase' }, { word: 'space in text', meaning: 'uncategorized' }] }])
  })
}

test('JSON typed-invalid fields cannot be repaired by defaults', async ({ page }) => {
  let posts = 0
  await page.route('**/admin/vocabulary/batch', route => { posts++; return json(route, {}) })
  await setup(page, '/import/batch/phrases')
  await pick(page, '.batch-book-select', 'Book A')
  await pick(page, '.batch-default-unit', '第 2 单元 Two')
  await input(page, 'json', [{ word: 'take off', meaning: 'leave', entryKind: 1 }, { word: 'take off', meaning: 'leave', unit: 2 as unknown as string }, { word: 'take off', meaning: 'leave', section: false as unknown as string }])
  for (const [index, field] of ['entryKind', 'unit', 'section'].entries()) await expect(preview(page).nth(index)).toContainText(`字段 ${field} 应为字符串`)
  await submit(page).click({ force: true })
  expect(posts).toBe(0)
})

test('deep links and refresh have matching modes, one active navigation item, and preserve raw input on switches', async ({ page }) => {
  await setup(page, '/import/batch/phrases')
  await expect(page.getByRole('heading', { name: '批量短语导入' })).toBeVisible()
  await page.reload()
  await expect(page.locator('.batch-mode .is-active')).toContainText('短语')
  await pick(page, '.batch-book-select', 'Book A')
  const raw = 'take off\t\t\t\tleave'
  await page.getByRole('textbox', { name: '导入数据' }).fill(raw)
  for (const [label, path, heading] of [['混合词汇', '/import/batch', '批量导入'], ['单词', '/import/batch/words', '批量单词导入'], ['短语', '/import/batch/phrases', '批量短语导入']]) {
    await page.locator('.batch-mode').getByText(label!, { exact: true }).click()
    await expect(page).toHaveURL(new RegExp(`#${path}$`))
    await expect(page.getByRole('heading', { name: heading!, exact: true })).toBeVisible()
    await expect(page.getByRole('textbox', { name: '导入数据' })).toHaveValue(raw)
    await expect(page.locator('.app-nav .is-active')).toHaveCount(1)
    await expect(page.locator('.app-nav [aria-current="page"]')).toHaveCount(1)
  }
})

test('mode/default changes invalidate server errors; book switches clear only default assignment', async ({ page }) => {
  const payloads: unknown[] = []
  await page.route('**/admin/vocabulary/batch', route => { payloads.push(route.request().postDataJSON()); return json(route, { success: false, errors: [{ index: 1, message: 'Unit gone' }] }, 400) })
  await setup(page, '/import/batch/phrases')
  await pick(page, '.batch-book-select', 'Book A')
  await pick(page, '.batch-default-unit', '第 2 单元 Two')
  await input(page, 'csv', [{ word: 'take off', meaning: 'leave' }, { word: 'look after', meaning: 'care' }])
  await submit(page).click()
  await expect(preview(page)).toHaveCount(1)
  await expect(preview(page)).toContainText('Unit gone')
  await expect(preview(page).locator('td:first-child')).toHaveText('3')
  await pick(page, '.batch-default-unit', '第 5 单元 Five')
  await expect(page.locator('.batch-server-summary')).toHaveCount(0)
  await expect(preview(page)).toHaveCount(2)
  await page.locator('.batch-mode').getByText('单词', { exact: true }).click()
  await expect(preview(page).nth(0)).toContainText('word（页面默认）')
  await expect(page.getByRole('textbox', { name: '导入数据' })).toHaveValue('word,meaning,unit,section,entry_kind\ntake off,leave,,,\nlook after,care,,,')
  await pick(page, '.batch-book-select', 'Book B')
  await expect(page.locator('.batch-default-unit')).toContainText('仅补充未填写单元的行')
  await expect(submit(page)).toBeDisabled()
  await pick(page, '.batch-default-unit', '第 2 单元 Two')
  await expect(submit(page)).toBeEnabled()
  expect(payloads).toHaveLength(1)
})

test('in-flight mode/default changes are disabled, route switches blocked, one snapshot POST sent', async ({ page }) => {
  let release: (() => void) | undefined
  const gate = new Promise<void>(resolve => { release = resolve })
  const payloads: unknown[] = []
  await page.route('**/admin/vocabulary/batch', async route => { payloads.push(route.request().postDataJSON()); await gate; await json(route, { success: true, data: { total: 1, created: 1, reused: 0 } }) })
  await setup(page, '/import/batch/phrases')
  await pick(page, '.batch-book-select', 'Book A')
  await pick(page, '.batch-default-unit', '第 2 单元 Two')
  await input(page, 'json', [{ word: 'take off', meaning: 'leave' }])
  await submit(page).click()
  await expect.poll(() => payloads.length).toBe(1)
  for (const radio of await page.locator('.batch-mode input').all()) await expect(radio).toBeDisabled()
  await expect(page.locator('.batch-default-unit input')).toBeDisabled()
  await page.getByRole('link', { name: '批量单词导入', exact: true }).click()
  await expect(page).toHaveURL(/#\/import\/batch\/phrases$/)
  await submit(page).click({ force: true })
  expect(payloads).toHaveLength(1)
  release?.()
  await expect(page.locator('.batch-result')).toBeVisible()
  expect(payloads).toEqual([{ bookId: 'book-a', entries: [{ word: 'take off', meaning: 'leave', unitId: 'unit-2', entryKind: 'phrase' }] }])
})

for (const width of [1440, 768]) {
  test(`uniform mode full-page layout and accessibility at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 1000 })
    await setup(page, '/import/batch/phrases')
    await pick(page, '.batch-book-select', 'Book A')
    await pick(page, '.batch-default-unit', '第 2 单元 Two')
    await input(page, 'csv', [{ word: 'take off', meaning: 'leave' }])
    await expect(page.locator('.el-select-dropdown:visible')).toHaveCount(0)
    await page.waitForTimeout(300)
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
    expect((await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze()).violations).toEqual([])
  })
}

for (const status of [404, 422, 413, 503, 0, 401, 403]) {
  test(`uniform write ${status || 'unknown'} preserves input or the existing auth redirect`, async ({ page }) => {
    let posts = 0
    await page.route('**/admin/vocabulary/batch', route => { posts++; return status ? json(route, { success: false, message: 'Rejected' }, status) : route.abort('failed') })
    await setup(page, '/import/batch/phrases')
    await pick(page, '.batch-book-select', 'Book A')
    await pick(page, '.batch-default-unit', '第 2 单元 Two')
    await input(page, 'json', [{ word: 'take off', meaning: 'leave' }])
    const raw = await page.getByRole('textbox', { name: '导入数据' }).inputValue()
    await submit(page).click()
    if (status === 401 || status === 403) await expect(page).toHaveURL(status === 401 ? /#\/login/ : /#\/forbidden$/)
    else {
      await expect(page.locator('.el-message--error')).toContainText(status === 404 ? '不存在' : status === 422 ? '停用' : status === 413 ? '1 MiB' : status === 503 ? '整批未写入' : '无法确认是否已写入')
      await expect(page.getByRole('textbox', { name: '导入数据' })).toHaveValue(raw)
      await expect(page.locator('.batch-default-unit')).toContainText('第 2 单元 Two')
    }
    expect(posts).toBe(1)
  })
}

test('late units cannot restore defaults for a replaced book', async ({ page }) => {
  await setup(page, '/import/batch/phrases')
  let release: (() => void) | undefined
  const gate = new Promise<void>(resolve => { release = resolve })
  let started = false
  await page.route(/\/admin\/vocabulary-books\/[^/]+\/units$/, async route => {
    const first = route.request().url().includes('book-a')
    if (first) { started = true; await gate }
    await json(route, { success: true, data: { units: first ? units : [{ ...units[0], id: 'other-2', bookId: 'book-b', title: 'Other' }] } })
  })
  await pick(page, '.batch-book-select', 'Book A')
  await expect.poll(() => started).toBe(true)
  await pick(page, '.batch-book-select', 'Book B')
  await input(page, 'json', [{ word: 'take off', meaning: 'leave' }])
  await pick(page, '.batch-default-unit', '第 2 单元 Other')
  release?.()
  await expect(preview(page)).toContainText('2 · Other')
  await page.locator('.batch-default-unit').click()
  await expect(page.getByRole('option', { name: '第 5 单元 Five' })).toHaveCount(0)
  await page.keyboard.press('Escape')
})

test('a workbook survives mode changes, defaults re-resolve, replacing/removing files writes nothing', async ({ page }) => {
  let posts = 0
  await page.route('**/admin/vocabulary/batch', route => { posts++; return json(route, {}) })
  await setup(page, '/import/batch/phrases')
  await pick(page, '.batch-book-select', 'Book A')
  await pick(page, '.batch-default-unit', '第 2 单元 Two')
  await input(page, 'xlsx', [{ word: 'take off', meaning: 'leave' }])
  await expect(preview(page)).toContainText('phrase（页面默认）')
  await page.locator('.batch-mode').getByText('单词', { exact: true }).click()
  await expect(page.getByRole('textbox', { name: '导入数据' })).toHaveAttribute('placeholder', 'vocabulary.xlsx')
  await expect(preview(page)).toContainText('word（页面默认）')
  await page.locator('.batch-mode').getByText('混合词汇', { exact: true }).click()
  await expect(preview(page).locator('td').nth(7)).toHaveText('')
  await expect(preview(page).locator('td').nth(12)).toHaveText('未分类')
  await input(page, 'xlsx', [{ word: 'another phrase', meaning: 'another' }])
  await expect(preview(page)).toContainText('another phrase')
  await expect(preview(page)).not.toContainText('take off')
  await page.getByRole('button', { name: '移除文件' }).click()
  await expect(preview(page)).toHaveCount(0)
  expect(posts).toBe(0)
})

test('leaving an in-flight batch invalidates success without automatically replaying it', async ({ page }) => {
  let release: (() => void) | undefined
  const gate = new Promise<void>(resolve => { release = resolve })
  let posts = 0
  await page.route('**/admin/vocabulary/batch', async route => { posts++; await gate; await json(route, { success: true, data: { total: 1, created: 1, reused: 0 } }) })
  await setup(page, '/import/batch/phrases')
  await pick(page, '.batch-book-select', 'Book A')
  await pick(page, '.batch-default-unit', '第 2 单元 Two')
  await input(page, 'json', [{ word: 'take off', meaning: 'leave' }])
  await submit(page).click()
  await expect.poll(() => posts).toBe(1)
  await page.getByRole('link', { name: '单条导入', exact: true }).click()
  await expect(page.getByRole('heading', { name: '单条导入' })).toBeVisible()
  await page.getByRole('link', { name: '批量短语导入', exact: true }).click()
  await page.getByRole('textbox', { name: '导入数据' }).fill('look after\t\t\t\tcare')
  const response = page.waitForResponse('**/admin/vocabulary/batch')
  release?.(); await response
  await expect(page.getByRole('textbox', { name: '导入数据' })).toHaveValue('look after\t\t\t\tcare')
  await expect(page.locator('.batch-result')).toHaveCount(0)
  expect(posts).toBe(1)
})

for (const suffix of ['words', 'phrases']) {
  test(`unauthenticated ${suffix} deep link retains the existing hosted allowlist boundary`, async ({ page }) => {
    await page.route('**/admin/auth/session', route => json(route, { success: false, message: 'Unauthorized' }, 401))
    let start: URL | undefined
    await page.route(/\/admin\/auth\/start(?:\?.*)?$/, route => {
      start = new URL(route.request().url())
      return route.fulfill({ status: 200, contentType: 'text/html', body: '<!doctype html><html><title>Hosted start fixture</title></html>' })
    })
    await page.goto(`/#/import/batch/${suffix}`)
    await expect(page).toHaveURL(/#\/login\?redirect=/)
    await page.getByRole('button', { name: /登录/ }).click()
    await expect.poll(() => start?.pathname).toBe('/admin/auth/start')
    expect(start?.searchParams.has('returnUrl')).toBe(false)
    // The unchanged server defaults an absent returnUrl to /#/books.
  })
}

test('a slow file read blocks writes and cannot replace the file selected after it', async ({ page }) => {
  await page.addInitScript(() => {
    const state = window as Window & { releaseOldFile?: () => void; oldFileFinished?: boolean }
    const original = File.prototype.arrayBuffer
    File.prototype.arrayBuffer = async function () {
      if (this.name === 'slow.tsv') await new Promise<void>(resolve => { state.releaseOldFile = resolve })
      const answer = await original.call(this)
      if (this.name === 'slow.tsv') state.oldFileFinished = true
      return answer
    }
  })
  const payloads: unknown[] = []
  await page.route('**/admin/vocabulary/batch', route => { payloads.push(route.request().postDataJSON()); return json(route, { success: true, data: { total: 1, created: 1, reused: 0 } }) })
  await setup(page, '/import/batch/phrases')
  await pick(page, '.batch-book-select', 'Book A')
  await pick(page, '.batch-default-unit', '第 2 单元 Two')
  await page.getByRole('textbox', { name: '导入数据' }).fill('old draft\t\t\t\told')
  await expect(submit(page)).toBeEnabled()
  await page.locator('.batch-file-input').setInputFiles({ name: 'slow.tsv', mimeType: 'text/plain', buffer: Buffer.from('old file\t\t\t\told') })
  await expect(page.locator('.batch-blockers')).toContainText('正在读取本地文件')
  await submit(page).click({ force: true })
  expect(payloads).toHaveLength(0)
  await input(page, 'csv', [{ word: 'new file', meaning: 'new' }])
  await page.evaluate(() => (window as Window & { releaseOldFile?: () => void }).releaseOldFile?.())
  await expect.poll(() => page.evaluate(() => (window as Window & { oldFileFinished?: boolean }).oldFileFinished)).toBe(true)
  await expect(preview(page)).toContainText('new file')
  await expect(preview(page)).not.toContainText('old file')
  await submit(page).click()
  // Polled for the same reason as the mixed-mode tests above.
  await expect.poll(() => payloads).toEqual([{ bookId: 'book-a', entries: [{ word: 'new file', meaning: 'new', unitId: 'unit-2', entryKind: 'phrase' }] }])
})
