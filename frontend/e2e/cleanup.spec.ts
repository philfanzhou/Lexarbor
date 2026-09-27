import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page, type Route } from '@playwright/test'

const admin = {
  username: 'ci-admin',
  roles: ['admin']
}

const bookA = {
  id: '11111111-1111-1111-1111-111111111111',
  bookName: 'CI Book A',
  description: 'Cleanup test fixture',
  publisher: 'Lexarbor',
  educationLevel: 'Secondary',
  grade: 'Grade 7',
  category: 'English',
  displayOrder: 1,
  status: true,
  iconUrl: ''
}

const legacyBook = {
  ...bookA,
  id: '44444444-4444-4444-4444-444444444444',
  bookName: 'Starter English 300',
  displayOrder: 4,
  status: false
}

const refA = { id: bookA.id, bookName: bookA.bookName, status: bookA.status }

const meaningsA = [
  { id: 'meaning-a1', vocabularyId: 'word-apple', bookId: bookA.id, partOfSpeech: 'n.', meaning: '苹果', example: 'An apple a day.' },
  { id: 'meaning-a2', vocabularyId: 'word-apple', bookId: bookA.id, partOfSpeech: 'n.', meaning: '苹果树', example: null }
]

const apple = { id: 'word-apple', word: 'apple', phoneticUk: '/ˈæp.əl/', phoneticUs: '/ˈæp.əl/', books: [refA] }
const cherry = { id: 'word-cherry', word: 'cherry', phoneticUk: '/ˈtʃer.i/', phoneticUs: null, books: [refA] }
const banana = { id: 'word-banana', word: 'banana', phoneticUk: null, phoneticUs: null, books: [refA] }
const shared = { id: 'word-shared', word: 'shared', phoneticUk: null, phoneticUs: null, books: [refA] }

const appleDetail = { ...apple, meanings: meaningsA }
const cherryDetail = {
  ...cherry,
  meanings: [{ id: 'meaning-c1', vocabularyId: 'word-cherry', bookId: bookA.id, partOfSpeech: 'n.', meaning: '樱桃', example: null }]
}

const contentA = {
  book: bookA,
  wordCount: 4,
  meaningCount: 5,
  items: [
    appleDetail,
    cherryDetail,
    { ...banana, meanings: [{ id: 'meaning-d1', vocabularyId: 'word-banana', bookId: bookA.id, partOfSpeech: 'n.', meaning: '香蕉', example: null }] },
    { ...shared, meanings: [{ id: 'meaning-e1', vocabularyId: 'word-shared', bookId: bookA.id, partOfSpeech: 'n.', meaning: '共享词', example: null }] }
  ],
  totalCount: 4,
  totalPage: 1
}

const wcagTags = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']

function json(route: Route, data: unknown, status = 200) {
  return route.fulfill({
    status,
    contentType: 'application/json',
    body: JSON.stringify(data)
  })
}

/** The server's envelope: `data` on success, a top-level `message` on failure. */
function envelope(route: Route, data: unknown, status = 200) {
  if (status < 400) {
    return json(route, { success: true, data }, status)
  }

  const message = (data as { message?: string } | null)?.message ?? 'Request failed.'
  return json(route, { success: false, message }, status)
}

interface Recorded {
  url: URL
  body: unknown
}

/** Records the cleanup previews and answers them from a queue of fixtures. */
function useCleanupPreview(page: Page, answer: () => { data: unknown; status: number }) {
  const requests: Recorded[] = []
  void page.route(/\/cleanup\/preview$/, (route) => {
    requests.push({ url: new URL(route.request().url()), body: route.request().postDataJSON() })
    const next = answer()
    return envelope(route, next.data, next.status)
  })
  return requests
}

function useCleanupCommit(page: Page, answer: () => { data: unknown; status: number }) {
  const requests: Recorded[] = []
  void page.route(/\/admin\/vocabulary-books\/[^/]+\/cleanup$/, (route) => {
    requests.push({ url: new URL(route.request().url()), body: route.request().postDataJSON() })
    const next = answer()
    return envelope(route, next.data, next.status)
  })
  return requests
}

/** Holds every commit until the test answers it, in arrival order. */
function useDeferredCommit(page: Page) {
  const pending: Array<(data: unknown, status: number) => void> = []
  const requests: Recorded[] = []
  void page.route(/\/admin\/vocabulary-books\/[^/]+\/cleanup$/, (route) => {
    requests.push({ url: new URL(route.request().url()), body: route.request().postDataJSON() })
    return new Promise<void>((resolve) => {
      pending.push((data, status) => {
        envelope(route, data, status).catch(() => undefined)
        resolve()
      })
    })
  })
  return {
    requests,
    answer(data: unknown = {}, status = 200) {
      pending.shift()?.(data, status)
    }
  }
}

async function mockSession(page: Page) {
  await page.route('**/admin/auth/session', (route) =>
    json(route, { success: true, data: admin }))
}

async function mockBooksList(page: Page, books = [bookA, legacyBook]) {
  await page.route(/\/admin\/vocabulary-books(\?.*)?$/, (route) => {
    if (route.request().method() !== 'GET') {
      return route.fallback()
    }

    return json(route, { success: true, data: { items: books, totalCount: books.length, totalPage: 1 } })
  })
  await page.route('**/admin/vocabulary-books/categories', (route) =>
    json(route, { success: true, data: { items: ['English'] } }))
  await page.route('**/admin/vocabulary-books/education-levels', (route) =>
    json(route, { success: true, data: { items: ['Secondary'] } }))
}

async function openBooks(page: Page, books = [bookA, legacyBook]) {
  await mockSession(page)
  await mockBooksList(page, books)
  await page.goto('/#/books')
  await expect(page.locator('.session')).toContainText(admin.username)
}

async function openBookWords(page: Page, book = bookA) {
  await mockSession(page)
  await mockBooksList(page)
  await page.route(/\/admin\/vocabulary-books\/[^/]+\/content(\?.*)?$/, (route) =>
    json(route, { success: true, data: contentA }))
  await page.goto(`/#/books/${book.id}/words`)
  await expect(page.locator('.el-table')).toContainText('apple')
}

async function openDetailFromLibrary(page: Page) {
  await mockSession(page)
  await mockBooksList(page)
  await page.route(/\/admin\/vocabulary(\?.*)?$/, (route) =>
    json(route, { success: true, data: { items: [apple, cherry], totalCount: 2, totalPage: 1 } }))
  await page.route(/\/admin\/vocabulary\/[^/]+$/, (route) => {
    if (route.request().method() !== 'GET') {
      return route.fallback()
    }

    const wordId = new URL(route.request().url()).pathname.split('/').at(-1)
    return json(route, { success: true, data: wordId === cherry.id ? cherryDetail : appleDetail })
  })
  await page.goto('/#/vocabulary')
}

const dialog = (page: Page) => page.locator('.cleanup-dialog')

test('clears a book through preview and confirmation, and cancelling commits nothing', async ({ page }) => {
  await openBooks(page)
  const previewData = { bookId: bookA.id, bookName: bookA.bookName, action: 'clear', affectedWordCount: 4, meaningCount: 5, orphanWordCount: 2 }
  const previews = useCleanupPreview(page, () => ({ data: previewData, status: 200 }))
  const commits = useCleanupCommit(page, () => ({
    data: { bookId: bookA.id, action: 'clear', affectedWordCount: 4, deletedMeaningCount: 5, deletedWordCount: 2, deletedBook: false },
    status: 200
  }))

  await page.locator('.el-table__row', { hasText: bookA.bookName }).getByRole('button', { name: '清空内容' }).click()
  await expect(dialog(page)).toBeVisible()
  await expect(dialog(page)).toContainText(`目标教材：${bookA.bookName}`)
  await expect(dialog(page)).toContainText('清理操作：清空教材内容')
  await expect(dialog(page)).toContainText('涉及去重单词 4 个')
  await expect(dialog(page)).toContainText('涉及释义 5 条')
  await expect(dialog(page)).toContainText('预计 2 个单词将因此失去全部教材引用并被删除')
  await expect(dialog(page)).toContainText('计数为估计值')
  await expect(dialog(page)).toContainText('其他教材（包括停用教材）中的释义与共享单词会保留')
  expect(previews).toHaveLength(1)
  expect(previews[0].body).toEqual({ action: 'clear' })
  expect(previews[0].url.pathname).toBe(`/admin/vocabulary-books/${bookA.id}/cleanup/preview`)

  // Cancelling ends the preview only: no commit is ever sent.
  await dialog(page).getByRole('button', { name: '取消' }).click()
  await expect(dialog(page)).not.toBeVisible()
  expect(commits).toHaveLength(0)

  await page.locator('.el-table__row', { hasText: bookA.bookName }).getByRole('button', { name: '清空内容' }).click()
  await expect(dialog(page)).toContainText('涉及释义 5 条')
  await dialog(page).getByRole('button', { name: '确认清理' }).click()

  expect(commits).toHaveLength(1)
  expect(commits[0].body).toEqual({ action: 'clear' })
  await expect(page.locator('.el-message--success')).toContainText('删除释义 5 条、单词 2 个，教材已保留')
})

test('deleting a book requires its exact name and sends it with the commit', async ({ page }) => {
  let listReads = 0
  await mockSession(page)
  await mockBooksList(page)
  await page.route(/\/admin\/vocabulary-books(\?.*)?$/, (route) => {
    if (route.request().method() !== 'GET') {
      return route.fallback()
    }

    listReads += 1
    return json(route, { success: true, data: { items: listReads > 1 ? [] : [legacyBook], totalCount: listReads > 1 ? 0 : 1, totalPage: 1 } })
  })
  const previewData = { bookId: legacyBook.id, bookName: legacyBook.bookName, action: 'delete', affectedWordCount: 300, meaningCount: 420, orphanWordCount: 300 }
  const previews = useCleanupPreview(page, () => ({ data: previewData, status: 200 }))
  const commits = useCleanupCommit(page, () => ({
    data: { bookId: legacyBook.id, action: 'delete', affectedWordCount: 300, deletedMeaningCount: 420, deletedWordCount: 300, deletedBook: true },
    status: 200
  }))
  await page.goto('/#/books')

  await page.locator('.el-table__row', { hasText: legacyBook.bookName }).getByRole('button', { name: '删除教材及内容' }).click()
  await expect(dialog(page)).toContainText('清理操作：删除教材及其内容')

  const confirmButton = dialog(page).getByRole('button', { name: '确认清理' })
  await expect(confirmButton).toBeDisabled()

  await dialog(page).getByRole('textbox').fill('Starter English 30')
  await expect(confirmButton).toBeDisabled()

  await dialog(page).getByRole('textbox').fill(legacyBook.bookName)
  await expect(confirmButton).toBeEnabled()
  await confirmButton.click()

  expect(previews[0].body).toEqual({ action: 'delete' })
  expect(commits).toHaveLength(1)
  expect(commits[0].body).toEqual({ action: 'delete', confirmedBookName: legacyBook.bookName })
  await expect(page.locator('.el-message--success')).toContainText('已删除教材「Starter English 300」及内容：删除释义 420 条、单词 300 个')
  // The list re-queries and the deleted legacy book is gone.
  await expect(page.locator('.el-table__row', { hasText: legacyBook.bookName })).toHaveCount(0)
})

test('a rename between preview and commit is refused and asks for a fresh preview', async ({ page }) => {
  await openBooks(page)
  const previewData = { bookId: bookA.id, bookName: bookA.bookName, action: 'delete', affectedWordCount: 4, meaningCount: 5, orphanWordCount: 2 }
  const previews = useCleanupPreview(page, () => ({ data: previewData, status: 200 }))
  const commits = useCleanupCommit(page, () => ({
    data: { message: 'The confirmed book name does not match.' },
    status: 409
  }))

  await page.locator('.el-table__row', { hasText: bookA.bookName }).getByRole('button', { name: '删除教材及内容' }).click()
  await dialog(page).getByRole('textbox').fill(bookA.bookName)
  await dialog(page).getByRole('button', { name: '确认清理' }).click()

  const error = dialog(page).locator('.el-alert--error').last()
  await expect(error).toContainText('The confirmed book name does not match.')
  await expect(error).toContainText('重新预览')
  expect(commits).toHaveLength(1)

  await error.getByRole('button', { name: '重新预览' }).click()
  await expect(previews).toHaveLength(2)
})

test('removes the current page selection without extending it and drops the selection on paging', async ({ page }) => {
  await openBookWords(page)
  const previewData = { bookId: bookA.id, bookName: bookA.bookName, action: 'removeWords', affectedWordCount: 2, meaningCount: 3, orphanWordCount: 1 }
  const previews = useCleanupPreview(page, () => ({ data: previewData, status: 200 }))
  const commits = useCleanupCommit(page, () => ({
    data: { bookId: bookA.id, action: 'removeWords', affectedWordCount: 2, deletedMeaningCount: 3, deletedWordCount: 1, deletedBook: false },
    status: 200
  }))

  const removeButton = page.getByRole('button', { name: '从本教材移除' })
  await expect(removeButton).toBeDisabled()

  await page.locator('.el-table__row', { hasText: 'apple' }).locator('.el-checkbox').click()
  await page.locator('.el-table__row', { hasText: 'cherry' }).locator('.el-checkbox').click()
  await expect(removeButton).toContainText('从本教材移除（2）')

  await removeButton.click()
  await expect(dialog(page)).toContainText('清理操作：从本教材移除所选单词')
  expect(previews).toHaveLength(1)
  expect((previews[0].body as { wordIds: string[] }).wordIds.sort()).toEqual(['word-apple', 'word-cherry'])

  await dialog(page).getByRole('button', { name: '确认清理' }).click()
  expect(commits).toHaveLength(1)
  expect((commits[0].body as { wordIds: string[] }).wordIds.sort()).toEqual(['word-apple', 'word-cherry'])
  await expect(page.locator('.el-message--success')).toContainText('删除释义 3 条')
  // The reload starts unselected again.
  await expect(removeButton).toBeDisabled()
})

test('removes one word from its row', async ({ page }) => {
  await openBookWords(page)
  const previews = useCleanupPreview(page, () => ({
    data: { bookId: bookA.id, bookName: bookA.bookName, action: 'removeWords', affectedWordCount: 1, meaningCount: 1, orphanWordCount: 0 },
    status: 200
  }))
  const commits = useCleanupCommit(page, () => ({
    data: { bookId: bookA.id, action: 'removeWords', affectedWordCount: 1, deletedMeaningCount: 1, deletedWordCount: 0, deletedBook: false },
    status: 200
  }))

  await page.locator('.el-table__row', { hasText: 'banana' }).getByRole('button', { name: '移除' }).click()
  await expect(dialog(page)).toContainText('清理操作：从本教材移除所选单词')
  expect(previews[0].body).toEqual({ action: 'removeWords', wordIds: ['word-banana'] })

  await dialog(page).getByRole('button', { name: '确认清理' }).click()
  expect(commits[0].body).toEqual({ action: 'removeWords', wordIds: ['word-banana'] })
  await expect(page.locator('.el-message--success')).toContainText('0 个单词失去全部教材引用一并删除')
})

test('a page emptied by a removal falls back to the last valid page', async ({ page }) => {
  await mockSession(page)
  await mockBooksList(page)
  const requests: URL[] = []
  let emptied = false
  await page.route(/\/admin\/vocabulary-books\/[^/]+\/content(\?.*)?$/, (route) => {
    requests.push(new URL(route.request().url()))
    const pageNumber = Number(new URL(route.request().url()).searchParams.get('page') ?? '1')
    if (!emptied) {
      return json(route, {
        success: true,
        data: {
          ...contentA,
          items: pageNumber === 2 ? [{ ...banana, meanings: contentA.items[2].meanings }] : contentA.items.slice(0, 3),
          totalCount: 40,
          totalPage: 2
        }
      })
    }

    // The removal shrank the book to a single page again.
    return json(route, { success: true, data: { ...contentA, items: contentA.items.slice(0, 3), totalCount: 20, totalPage: 1 } })
  })
  await useCleanupPreview(page, () => ({
    data: { bookId: bookA.id, bookName: bookA.bookName, action: 'removeWords', affectedWordCount: 1, meaningCount: 1, orphanWordCount: 1 },
    status: 200
  }))
  await useCleanupCommit(page, () => ({
    data: { bookId: bookA.id, action: 'removeWords', affectedWordCount: 1, deletedMeaningCount: 1, deletedWordCount: 1, deletedBook: false },
    status: 200
  }))
  await page.goto(`/#/books/${bookA.id}/words`)

  await page.locator('.el-pagination').getByText('2', { exact: true }).click()
  await expect.poll(() => requests.at(-1)?.searchParams.get('page')).toBe('2')

  emptied = true
  await page.locator('.el-table__row', { hasText: 'banana' }).getByRole('button', { name: '移除' }).click()
  await dialog(page).getByRole('button', { name: '确认清理' }).click()

  // Page two no longer exists; the list falls back to page one and re-queries.
  await expect.poll(() => requests.at(-1)?.searchParams.get('page')).toBe('1')
  await expect(page.locator('.el-table')).toContainText('apple')
})

test('a removed book returns to the books list', async ({ page }) => {
  await openBookWords(page)
  await useCleanupPreview(page, () => ({
    data: { bookId: bookA.id, bookName: bookA.bookName, action: 'removeWords', affectedWordCount: 1, meaningCount: 1, orphanWordCount: 1 },
    status: 200
  }))
  await useCleanupCommit(page, () => ({
    data: { bookId: bookA.id, action: 'removeWords', affectedWordCount: 1, deletedMeaningCount: 1, deletedWordCount: 1, deletedBook: true },
    status: 200
  }))

  await page.locator('.el-table__row', { hasText: 'banana' }).getByRole('button', { name: '移除' }).click()
  await dialog(page).getByRole('button', { name: '确认清理' }).click()

  await expect(page).toHaveURL(/#\/books$/)
  await expect(page.locator('.page-header h1')).toHaveText('教材管理')
})

test('deletes one meaning from the drawer with its own three ids', async ({ page }) => {
  await openDetailFromLibrary(page)
  const previews = useCleanupPreview(page, () => ({
    data: { bookId: bookA.id, bookName: bookA.bookName, action: 'removeMeaning', affectedWordCount: 1, meaningCount: 1, orphanWordCount: 0 },
    status: 200
  }))
  const commits = useCleanupCommit(page, () => ({
    data: { bookId: bookA.id, action: 'removeMeaning', affectedWordCount: 1, deletedMeaningCount: 1, deletedWordCount: 0, deletedBook: false },
    status: 200
  }))
  const detailReads: string[] = []
  let detailData: unknown = appleDetail
  await page.route(/\/admin\/vocabulary\/[^/]+$/, (route) => {
    if (route.request().method() !== 'GET') {
      return route.fallback()
    }

    detailReads.push(new URL(route.request().url()).pathname)
    return json(route, { success: true, data: detailData })
  })
  await page.goto('/#/vocabulary')

  await page.locator('.el-table__row', { hasText: 'apple' }).getByRole('button', { name: '详情' }).click()
  const drawer = page.locator('.el-drawer')
  await expect(drawer).toContainText('苹果树')

  await drawer.locator('.word-detail__meaning', { hasText: '苹果树' }).getByRole('button', { name: '删除' }).click()
  await expect(dialog(page)).toContainText('清理操作：删除这条释义')
  expect(previews).toHaveLength(1)
  expect(previews[0].url.pathname).toBe(`/admin/vocabulary-books/${bookA.id}/cleanup/preview`)
  expect(previews[0].body).toEqual({ action: 'removeMeaning', wordId: 'word-apple', meaningId: 'meaning-a2' })

  // The server state changes with the commit; the re-read must serve it.
  detailData = { ...appleDetail, meanings: [meaningsA[0]] }
  await dialog(page).getByRole('button', { name: '确认清理' }).click()
  expect(commits).toHaveLength(1)
  expect(commits[0].body).toEqual({ action: 'removeMeaning', wordId: 'word-apple', meaningId: 'meaning-a2' })

  await expect(drawer).not.toContainText('苹果树')
  await expect(drawer).toContainText('苹果')
  expect(detailReads).toHaveLength(2)
})

test('deleting the last meaning closes the drawer of the deleted word', async ({ page }) => {
  await openDetailFromLibrary(page)
  await useCleanupPreview(page, () => ({
    data: { bookId: bookA.id, bookName: bookA.bookName, action: 'removeMeaning', affectedWordCount: 1, meaningCount: 1, orphanWordCount: 1 },
    status: 200
  }))
  let cherryGone = false
  await useCleanupCommit(page, () => {
    cherryGone = true
    return {
      data: { bookId: bookA.id, action: 'removeMeaning', affectedWordCount: 1, deletedMeaningCount: 1, deletedWordCount: 1, deletedBook: false },
      status: 200
    }
  })
  await page.route(/\/admin\/vocabulary\/[^/]+$/, (route) => {
    if (route.request().method() !== 'GET') {
      return route.fallback()
    }

    const wordId = new URL(route.request().url()).pathname.split('/').at(-1)
    if (wordId === cherry.id && cherryGone) {
      return json(route, { success: false, message: 'No such word.' }, 404)
    }

    return json(route, { success: true, data: wordId === cherry.id ? cherryDetail : appleDetail })
  })
  await page.goto('/#/vocabulary')

  await page.locator('.el-table__row', { hasText: 'cherry' }).getByRole('button', { name: '详情' }).click()
  const drawer = page.locator('.el-drawer')
  await expect(drawer).toContainText('樱桃')

  await drawer.locator('.word-detail__meaning', { hasText: '樱桃' }).getByRole('button', { name: '删除' }).click()
  await dialog(page).getByRole('button', { name: '确认清理' }).click()

  await expect(drawer).not.toBeVisible()
})

test('an aborted commit warns and re-queries without replaying', async ({ page }) => {
  await mockSession(page)
  await mockBooksList(page)
  let contentReads = 0
  await page.route(/\/admin\/vocabulary-books\/[^/]+\/content(\?.*)?$/, (route) => {
    contentReads += 1
    return json(route, { success: true, data: contentA })
  })
  await useCleanupPreview(page, () => ({
    data: { bookId: bookA.id, bookName: bookA.bookName, action: 'removeWords', affectedWordCount: 1, meaningCount: 1, orphanWordCount: 0 },
    status: 200
  }))
  let commitReads = 0
  await page.route(/\/admin\/vocabulary-books\/[^/]+\/cleanup$/, async (route) => {
    commitReads += 1
    await route.abort('failed')
  })
  await page.goto(`/#/books/${bookA.id}/words`)
  await expect(page.locator('.el-table')).toContainText('banana')
  expect(contentReads).toBe(1)

  await page.locator('.el-table__row', { hasText: 'banana' }).getByRole('button', { name: '移除' }).click()
  await dialog(page).getByRole('button', { name: '确认清理' }).click()

  await expect(page.locator('.el-message--warning')).toContainText('清理提交结果未知')
  await expect.poll(() => contentReads).toBe(2)
  await expect(dialog(page)).not.toBeVisible()
  await page.waitForTimeout(300)
  expect(commitReads).toBe(1)
})

test('a double click commits exactly once', async ({ page }) => {
  await openBookWords(page)
  await useCleanupPreview(page, () => ({
    data: { bookId: bookA.id, bookName: bookA.bookName, action: 'removeWords', affectedWordCount: 1, meaningCount: 1, orphanWordCount: 0 },
    status: 200
  }))
  const commit = useDeferredCommit(page)
  await page.goto(`/#/books/${bookA.id}/words`)

  await page.locator('.el-table__row', { hasText: 'banana' }).getByRole('button', { name: '移除' }).click()
  const confirm = dialog(page).getByRole('button', { name: '确认清理' })
  await confirm.click()
  await confirm.click({ force: true })
  expect(commit.requests).toHaveLength(1)

  commit.answer({ bookId: bookA.id, action: 'removeWords', affectedWordCount: 1, deletedMeaningCount: 1, deletedWordCount: 0, deletedBook: false })
  await expect(page.locator('.el-message--success')).toContainText('已移除')
})

test('preview refusals are distinct: gone, stale, and busy', async ({ page }) => {
  await openBooks(page)
  let mode: 'gone' | 'stale' | 'busy' = 'gone'
  await useCleanupPreview(page, () => {
    if (mode === 'gone') {
      return { data: { message: 'No such book.' }, status: 404 }
    }
    if (mode === 'stale') {
      return { data: { message: 'The selection no longer belongs to the book.' }, status: 409 }
    }

    return { data: { message: 'The store is busy.' }, status: 503 }
  })

  const clearButton = page.locator('.el-table__row', { hasText: bookA.bookName }).getByRole('button', { name: '清空内容' })
  await clearButton.click()
  await expect(dialog(page).locator('.el-alert--error')).toContainText('目标教材不存在或已被删除。')
  await dialog(page).getByRole('button', { name: '取消' }).click()

  mode = 'stale'
  await clearButton.click()
  const staleError = dialog(page).locator('.el-alert--error')
  await expect(staleError).toContainText('The selection no longer belongs to the book.')
  await expect(staleError).toContainText('重新预览')

  mode = 'busy'
  await staleError.getByRole('button', { name: '重新预览' }).click()
  await expect(dialog(page).locator('.el-alert--error')).toContainText('服务忙，暂时无法取得预览')
})

for (const width of [1440, 768]) {
  test.describe(`at ${width} px`, () => {
    test.use({ viewport: { width, height: 900 } })

    test('the cleanup dialog for a delete is accessible with a long book name', async ({ page }) => {
      const longBook = { ...legacyBook, bookName: 'Starter English 300 With A Very Long Name For Wrapping Checks' }
      await openBooks(page, [longBook])
      await useCleanupPreview(page, () => ({
        data: { bookId: longBook.id, bookName: longBook.bookName, action: 'delete', affectedWordCount: 3, meaningCount: 4, orphanWordCount: 3 },
        status: 200
      }))
      await useCleanupCommit(page, () => ({
        data: { bookId: longBook.id, action: 'delete', affectedWordCount: 3, deletedMeaningCount: 4, deletedWordCount: 3, deletedBook: true },
        status: 200
      }))

      await page.locator('.el-table__row', { hasText: longBook.bookName }).getByRole('button', { name: '删除教材及内容' }).click()
      await dialog(page).getByRole('textbox').fill(longBook.bookName)
      await expect(dialog(page).getByRole('button', { name: '确认清理' })).toBeEnabled()
      await page.waitForTimeout(400)

      const results = await new AxeBuilder({ page }).withTags(wcagTags).analyze()
      expect(results.violations).toEqual([])
      const overflow = await page.evaluate(() =>
        document.documentElement.scrollWidth - document.documentElement.clientWidth)
      expect(overflow).toBeLessThanOrEqual(0)
    })

    test('the book word list with a selection is accessible', async ({ page }) => {
      await openBookWords(page)
      // All rows selected: the indeterminate header checkbox that Element Plus
      // renders for a partial selection carries an aria-checked attribute its
      // label role does not support, which is an upstream markup quirk.
      await page.locator('.el-table__header .el-checkbox').click()
      await expect(page.getByRole('button', { name: '从本教材移除' })).toContainText('（4）')
      await page.waitForTimeout(400)

      const results = await new AxeBuilder({ page }).withTags(wcagTags).analyze()
      expect(results.violations).toEqual([])
      const overflow = await page.evaluate(() =>
        document.documentElement.scrollWidth - document.documentElement.clientWidth)
      expect(overflow).toBeLessThanOrEqual(0)
    })

    test('the meaning-delete dialog inside the drawer flow is accessible', async ({ page }) => {
      await openDetailFromLibrary(page)
      await useCleanupPreview(page, () => ({
        data: { bookId: bookA.id, bookName: bookA.bookName, action: 'removeMeaning', affectedWordCount: 1, meaningCount: 1, orphanWordCount: 0 },
        status: 200
      }))
      await useCleanupCommit(page, () => ({
        data: { bookId: bookA.id, action: 'removeMeaning', affectedWordCount: 1, deletedMeaningCount: 1, deletedWordCount: 0, deletedBook: false },
        status: 200
      }))
      await page.goto('/#/vocabulary')
      await page.locator('.el-table__row', { hasText: 'apple' }).getByRole('button', { name: '详情' }).click()
      await page.locator('.el-drawer').locator('.word-detail__meaning', { hasText: '苹果树' }).getByRole('button', { name: '删除' }).click()
      await expect(dialog(page)).toContainText('删除这条释义')
      await page.waitForTimeout(400)

      const results = await new AxeBuilder({ page }).withTags(wcagTags).analyze()
      expect(results.violations).toEqual([])
    })
  })
}
