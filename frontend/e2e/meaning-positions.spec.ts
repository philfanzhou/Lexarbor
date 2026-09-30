import { expect, test, type Page, type Route } from '@playwright/test'

const book = { id: 'book-a', bookName: 'Disabled Book', status: false, displayOrder: 1 }
const units = [
  { id: 'unit-a', bookId: book.id, number: 1, title: 'One', meaningCount: 1 },
  { id: 'unit-b', bookId: book.id, number: 2, title: 'Two', meaningCount: 1 }
]
type Place = { unitId: string; section: 'A' | 'B' | null; entryKind: 'word' | 'phrase' | null }
const initial: Place[] = [
  { unitId: 'unit-a', section: 'A', entryKind: 'phrase' },
  { unitId: 'unit-a', section: 'B', entryKind: 'word' },
  { unitId: 'unit-b', section: null, entryKind: null }
]
const json = (route: Route, data: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(data) })
const same = (a: Place, b: Place) => a.unitId === b.unitId && a.section === b.section && a.entryKind === b.entryKind

async function setup(page: Page) {
  const places = initial.map(item => ({ ...item }))
  const writes: Array<{ method: string; url: string; body?: unknown; csrf: string | undefined }> = []
  let moveStatus = 200
  let unitsStatus = 200
  let releaseMove: (() => void) | null = null
  await page.route('**/admin/auth/session', route => json(route, { success: true, data: { username: 'admin', roles: ['admin'] } }))
  await page.route('**/admin/auth/method', route => json(route, { success: true, data: { method: 'password' } }))
  await page.route('**/admin/system/version', route => json(route, { success: true, data: { version: '1', revision: null, channel: 'test' } }))
  await page.route(/\/admin\/vocabulary-books\?/, route => json(route, { success: true, data: { items: [book], totalPage: 1, totalCount: 1 } }))
  await page.route('**/admin/vocabulary-books/book-a/units', route => unitsStatus === 200
    ? json(route, { success: true, data: { units } })
    : json(route, { success: false, message: 'Units unavailable' }, unitsStatus))
  await page.route('**/admin/vocabulary-books/book-a/phrase-positions**', route => {
    const url = new URL(route.request().url())
    const filtered = places.filter(place => place.entryKind === 'phrase'
      && (!url.searchParams.get('unitId') || url.searchParams.get('unitId') === place.unitId)
      && (!url.searchParams.get('section') || url.searchParams.get('section') === (place.section ?? 'none')))
    return json(route, { success: true, data: { items: filtered.map(place => ({
      ...place, bookId: book.id, meaningId: 'meaning-a', number: units.find(unit => unit.id === place.unitId)?.number,
      title: units.find(unit => unit.id === place.unitId)?.title, wordId: 'word-a', word: 'take off',
      phoneticUk: null, phoneticUs: null, partOfSpeech: 'v.', meaning: 'leave', example: null
    })), totalCount: filtered.length, totalPage: filtered.length ? 1 : 0 } })
  })
  await page.route('**/admin/vocabulary/word-a', route => json(route, { success: true, data: {
    id: 'word-a', word: 'take off', phoneticUk: null, phoneticUs: null,
    books: [{ id: book.id, bookName: book.bookName, status: book.status }],
    meanings: [{ id: 'meaning-a', vocabularyId: 'word-a', bookId: book.id, partOfSpeech: 'v.', meaning: 'leave', example: null,
      units: places.map(place => ({ ...place, number: units.find(unit => unit.id === place.unitId)?.number, title: units.find(unit => unit.id === place.unitId)?.title })) }]
  } }))
  await page.route('**/admin/vocabulary-books/book-a/meanings/meaning-a/positions**', async route => {
    const request = route.request()
    writes.push({ method: request.method(), url: request.url(),
      body: request.method() === 'PUT' ? request.postDataJSON() : undefined,
      csrf: request.headers()['x-requested-with'] })
    if (request.method() === 'PUT') {
      if (moveStatus === -1) return route.abort('failed')
      if (moveStatus === -2) await new Promise<void>(resolve => { releaseMove = resolve })
      if (moveStatus !== 200) return json(route, { success: false, message: 'Refused' }, moveStatus)
      const body = request.postDataJSON() as { from: Place; to: Place }
      const index = places.findIndex(place => same(place, body.from))
      if (index < 0) return json(route, { success: false, message: 'Missing' }, 404)
      if (!same(body.from, body.to) && places.some(place => same(place, body.to))) return json(route, { success: false, message: 'Conflict' }, 409)
      places[index] = body.to
    } else {
      const url = new URL(request.url())
      const unitId = url.pathname.split('/').at(-1)
      const section = url.searchParams.get('section')
      const entryKind = url.searchParams.get('entryKind')
      const index = places.findIndex(place => place.unitId === unitId && (place.section ?? 'none') === section && (place.entryKind ?? 'none') === entryKind)
      if (index < 0) return json(route, { success: false, message: 'Missing' }, 404)
      places.splice(index, 1)
    }
    return json(route, { success: true, data: { success: true } })
  })
  await page.goto('/#/phrases')
  await page.locator('.phrase-positions__book').click()
  await page.locator('.el-select-dropdown__item:visible').filter({ hasText: 'Disabled Book（停用）' }).click()
  await expect(page.getByText('共 1 条短语位置')).toBeVisible()
  return { places, writes, setMoveStatus: (status: number) => { moveStatus = status }, setUnitsStatus: (status: number) => { unitsStatus = status }, releaseMove: () => { moveStatus = 200; releaseMove?.() } }
}

test('exact row move and removal preserve other positions and reread counts', async ({ page }) => {
  const state = await setup(page)
  await page.locator('.phrase-positions .el-table').getByRole('button', { name: '调整位置' }).click()
  const dialog = page.getByRole('dialog', { name: '管理单元位置' })
  await expect(dialog).toContainText('Disabled Book')
  await expect(dialog).toContainText('Section A')
  await dialog.getByRole('button', { name: '取消' }).click()
  expect(state.writes).toHaveLength(0)

  await page.locator('.phrase-positions .el-table').getByRole('button', { name: '调整位置' }).click()
  await dialog.locator('.el-form-item').filter({ hasText: '目标类别' }).locator('.el-select').click()
  await page.locator('.el-select-dropdown__item:visible').filter({ hasText: '词条' }).click()
  await dialog.getByRole('button', { name: '保存位置' }).click()
  await expect(page.getByText('共 0 条短语位置')).toBeVisible()
  expect(state.writes[0]).toMatchObject({ method: 'PUT', csrf: 'XMLHttpRequest', body: {
    from: initial[0], to: { unitId: 'unit-a', section: 'A', entryKind: 'word' }
  } })
  expect(state.places).toHaveLength(3)
  expect(state.places).toContainEqual(initial[1])
  expect(state.places).toContainEqual(initial[2])
})

test('conflict, rejection and missing source retain a move draft; units can be retried', async ({ page }) => {
  const state = await setup(page)
  state.setUnitsStatus(503)
  await page.locator('.phrase-positions .el-table').getByRole('button', { name: '调整位置' }).click()
  const dialog = page.getByRole('dialog', { name: '管理单元位置' })
  await expect(dialog).toContainText('Units unavailable')
  state.setUnitsStatus(200)
  await dialog.getByRole('button', { name: '重试单元列表' }).click()
  await expect(dialog.getByRole('combobox', { name: '目标单元' })).toBeEnabled()
  state.setMoveStatus(409)
  await dialog.getByRole('button', { name: '保存位置' }).click()
  await expect(dialog).toContainText('目标位置已存在')
  state.setMoveStatus(400)
  await dialog.getByRole('button', { name: '保存位置' }).click()
  await expect(dialog).toContainText('Refused')
  state.setMoveStatus(404)
  await dialog.getByRole('button', { name: '保存位置' }).click()
  await expect(dialog).toContainText('重新加载')
  expect(state.places).toEqual(initial)
})

test('drawer manages word and unclassified positions, moves units and removes only one place', async ({ page }) => {
  const state = await setup(page)
  await page.locator('.phrase-positions .el-table').getByRole('button', { name: '详情' }).click()
  const drawer = page.getByRole('dialog', { name: '单词详情' })
  await expect(drawer).toContainText('词条')
  await expect(drawer).toContainText('未分类')
  const dialog = page.getByRole('dialog', { name: '管理单元位置' })

  await drawer.getByRole('button', { name: '调整位置：单元 1 B word' }).click()
  await dialog.locator('.el-form-item').filter({ hasText: '目标类别' }).locator('.el-select').click()
  await page.locator('.el-select-dropdown__item:visible').filter({ hasText: '短语' }).click()
  await dialog.getByRole('button', { name: '保存位置' }).click()
  await expect(page.getByText('共 2 条短语位置')).toBeVisible()

  await drawer.getByRole('button', { name: '调整位置：单元 2 未分节 未分类' }).click()
  await dialog.locator('.el-form-item').filter({ hasText: '目标分节' }).locator('.el-select').click()
  await page.locator('.el-select-dropdown__item:visible').filter({ hasText: 'Section A' }).click()
  await dialog.locator('.el-form-item').filter({ hasText: '目标类别' }).locator('.el-select').click()
  await page.locator('.el-select-dropdown__item:visible').filter({ hasText: '短语' }).click()
  await dialog.getByRole('button', { name: '保存位置' }).click()
  await expect(page.getByText('共 3 条短语位置')).toBeVisible()

  await drawer.getByRole('button', { name: '调整位置：单元 1 A phrase' }).click()
  await dialog.locator('.el-form-item').filter({ hasText: '目标单元' }).locator('.el-select').click()
  await page.locator('.el-select-dropdown__item:visible').filter({ hasText: '第 2 单元' }).click()
  await dialog.locator('.el-form-item').filter({ hasText: '目标分节' }).locator('.el-select').click()
  await page.locator('.el-select-dropdown__item:visible').filter({ hasText: 'Section B' }).click()
  await dialog.getByRole('button', { name: '保存位置' }).click()
  await expect(drawer.getByRole('button', { name: '调整位置：单元 2 B phrase' })).toBeVisible()

  await drawer.getByRole('button', { name: '从本单元移除：单元 1 B phrase' }).click()
  await expect(dialog).toContainText('Disabled Book')
  await expect(dialog).toContainText('Section B')
  await expect(dialog).toContainText('take off')
  await dialog.getByRole('button', { name: '取消' }).click()
  expect(state.writes.filter(write => write.method === 'DELETE')).toHaveLength(0)
  await drawer.getByRole('button', { name: '从本单元移除：单元 1 B phrase' }).click()
  await dialog.getByRole('button', { name: '确认只移除此位置' }).click()
  await expect(page.getByText('共 2 条短语位置')).toBeVisible()
  expect(state.writes.at(-1)?.url).toContain('/positions/unit-a?section=B&entryKind=phrase')
  expect(state.places).toEqual([
    { unitId: 'unit-b', section: 'B', entryKind: 'phrase' },
    { unitId: 'unit-b', section: 'A', entryKind: 'phrase' }
  ])
  await expect(drawer).toContainText('leave')
})

test('network outcome is unknown and triggers a fresh read without replay', async ({ page }) => {
  const state = await setup(page)
  state.setMoveStatus(-1)
  await page.locator('.phrase-positions .el-table').getByRole('button', { name: '调整位置' }).click()
  const dialog = page.getByRole('dialog', { name: '管理单元位置' })
  await dialog.getByRole('button', { name: '保存位置' }).click()
  await expect(dialog).toBeHidden()
  await expect(page.getByText('操作结果未知')).toBeVisible()
  await expect(page.getByText('共 1 条短语位置')).toBeVisible()
  expect(state.writes).toHaveLength(1)
  expect(state.places).toEqual(initial)
})

test('unclassified exact removal sends explicit none query values', async ({ page }) => {
  const state = await setup(page)
  await page.locator('.phrase-positions .el-table').getByRole('button', { name: '详情' }).click()
  const drawer = page.getByRole('dialog', { name: '单词详情' })
  await drawer.getByRole('button', { name: '从本单元移除：单元 2 未分节 未分类' }).click()
  await page.getByRole('dialog', { name: '管理单元位置' }).getByRole('button', { name: '确认只移除此位置' }).click()
  await expect.poll(() => state.writes.length).toBe(1)
  await expect(drawer.getByRole('button', { name: '从本单元移除：单元 2 未分节 未分类' })).toHaveCount(0)
  await expect(drawer).toContainText('leave')
  expect(state.writes.at(-1)?.url).toContain('/positions/unit-b?section=none&entryKind=none')
  expect(state.places).toEqual(initial.slice(0, 2))
})

test('an in-flight operation cannot be sent twice and closing invalidates its late answer', async ({ page }) => {
  const state = await setup(page)
  state.setMoveStatus(-2)
  await page.locator('.phrase-positions .el-table').getByRole('button', { name: '调整位置' }).click()
  const dialog = page.getByRole('dialog', { name: '管理单元位置' })
  await dialog.getByRole('button', { name: '保存位置' }).click()
  await expect.poll(() => state.writes.length).toBe(1)
  await expect(dialog.getByRole('button', { name: '保存位置' })).toBeDisabled()
  await dialog.locator('.el-dialog__headerbtn').click()
  state.releaseMove()
  await expect(dialog).toBeHidden()
  expect(state.writes).toHaveLength(1)
})

for (const [status, destination] of [[401, 'login'], [403, 'forbidden']] as const) {
  test(`${status} from a position write keeps the global session redirect`, async ({ page }) => {
    const state = await setup(page)
    state.setMoveStatus(status)
    await page.locator('.phrase-positions .el-table').getByRole('button', { name: '调整位置' }).click()
    await page.getByRole('dialog', { name: '管理单元位置' }).getByRole('button', { name: '保存位置' }).click()
    await expect(page).toHaveURL(new RegExp(`#/${destination}(?:\\?|$)`))
    expect(state.writes).toHaveLength(1)
  })
}
