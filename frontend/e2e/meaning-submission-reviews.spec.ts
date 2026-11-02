import { expect, test, type Page, type Route } from '@playwright/test'
import AxeBuilder from '@axe-core/playwright'
import { mockAntiforgery } from './support/antiforgery'
import type { MeaningSubmissionReview } from '../src/services/meaningSubmissionApi'

const json = (route: Route, data: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify({ success: status < 400, data }) })
function record(id = 'record-1', overrides: Partial<MeaningSubmissionReview> = {}): MeaningSubmissionReview {
  return { recordId: id, submissionId: 'submission-1', source: 'student-client', reviewerKind: 'ai',
    original: { word: 'Word', partOfSpeech: 'n.', meaning: '原释义', example: 'Original example' },
    ai: { verdict: 'revise', partOfSpeech: 'v.', meaning: '原释义', phoneticUk: '/ai-uk/', phoneticUs: '/ai-us/', comment: '仅修改词性与音标', model: 'captured-actual-model', reviewedAt: '2026-11-02T00:00:00Z' },
    applied: true, checked: false, checkedAt: null, resultWordId: 'word-1', ...overrides }
}
const paged = (items: MeaningSubmissionReview[], totalCount = items.length, page = 1) => ({ items, totalCount, page, size: 20 })
async function setup(page: Page) {
  await page.route('**/admin/auth/session', route => json(route, { username: 'admin', roles: ['admin'] }))
  await page.route('**/admin/auth/method', route => json(route, { mode: 'hosted', providerDisplayName: 'SignaCore' }))
  await mockAntiforgery(page)
  await page.route('**/admin/system/version', route => json(route, { version: '1', revision: null, channel: 'test' }))
  await page.route(/\/admin\/meaning-submissions\?/, route => json(route, { configured: true, assistantEnabled: true, items: [], totalCount: 0, totalPage: 0 }))
}
async function open(page: Page) {
  await page.goto('/#/submissions')
  await page.getByText('AI 审核记录', { exact: true }).click()
}

test('原文与POS/音标修订逐项对照，核对带CSRF只提交一次并保留现有纠错入口', async ({ page }) => {
  await setup(page)
  let checked = false
  const posts: string[] = [], filters: URLSearchParams[] = []
  await page.route(/\/admin\/meaning-submission-reviews\?/, route => {
    const params = new URL(route.request().url()).searchParams; filters.push(params)
    return json(route, paged([record('record-1', { checked, checkedAt: checked ? '2026-11-02T01:00:00Z' : null })]))
  })
  await page.route('**/admin/meaning-submission-reviews/record-1/checked', async route => {
    posts.push(route.request().headers()['x-signacore-csrf'] ?? ''); checked = true
    await json(route, { recordId: 'record-1', checkedAt: '2026-11-02T01:00:00Z' })
  })
  await open(page)
  await page.getByLabel('AI 结果', { exact: true }).selectOption('revise')
  await page.getByLabel('核对状态', { exact: true }).selectOption('false')
  await expect.poll(() => filters.at(-1)?.get('checked')).toBe('false')
  expect(filters.at(-1)?.get('verdict')).toBe('revise'); expect(filters.at(-1)?.get('applied')).toBe('true')
  await page.getByRole('button', { name: '对照记录' }).click()
  const detail = page.getByRole('region', { name: '审核记录对照' })
  for (const text of ['提交原文', '词性：n.', '释义：原释义', 'Original example', '词性：v.', '/ai-uk/', '/ai-us/', '仅修改词性与音标', 'captured-actual-model', '实际应用：已应用']) await expect(detail).toContainText(text)
  await expect(detail.getByRole('button', { name: '查看词条并更正' })).toBeVisible()
  await expect(detail.getByRole('link', { name: '在词库查找并更正' })).toHaveAttribute('href', '#/vocabulary')
  // Keep selection visible after a successful check by clearing the unchecked filter.
  await page.getByLabel('核对状态', { exact: true }).selectOption('')
  await page.getByRole('button', { name: '对照记录' }).click()
  await page.getByRole('button', { name: '标记已核对' }).dblclick()
  await expect.poll(() => posts.length).toBe(1)
  expect(posts[0]).toBeTruthy()
  await expect(detail).toContainText('已核对（')
  await expect(page.getByRole('button', { name: '标记已核对' })).toBeDisabled()
})

for (const verdict of ['accept', 'reject'] as const) test(`筛选${verdict}历史，未应用结论不冒称实际通过或驳回`, async ({ page }) => {
  await setup(page)
  const row = record('late', { applied: false, resultWordId: null, ai: { ...record().ai, verdict } })
  const queries: URLSearchParams[] = []
  await page.route(/\/admin\/meaning-submission-reviews\?/, route => { queries.push(new URL(route.request().url()).searchParams); return json(route, paged([row])) })
  await open(page)
  await page.getByLabel('AI 结果', { exact: true }).selectOption(verdict)
  await page.getByLabel('实际应用', { exact: true }).selectOption('false')
  await expect.poll(() => queries.at(-1)?.get('applied')).toBe('false')
  expect(queries.at(-1)?.get('verdict')).toBeNull()
  await expect(page.getByLabel('AI 结果', { exact: true })).toBeDisabled()
  await expect(page.getByLabel('AI 结果', { exact: true })).toHaveValue('')
  await page.getByRole('button', { name: '对照记录' }).click()
  const detail = page.getByRole('region', { name: '审核记录对照' })
  await expect(detail).toContainText('未应用的 AI 结论')
  await expect(detail).toContainText('未改变词库')
  await expect(detail.getByRole('button', { name: '查看词条并更正' })).toHaveCount(0)
})

test('丢核对响应先按recordId读取确认，确认失败保持未知且不盲重发', async ({ page }) => {
  await setup(page)
  let posted = 0, confirmReads = 0, failConfirm = true
  await page.route(/\/admin\/meaning-submission-reviews\?/, route => {
    const params = new URL(route.request().url()).searchParams
    if (params.has('recordId')) {
      expect(params.get('recordId')).toBe('record-1'); confirmReads++
      if (failConfirm) return json(route, null, 503)
    }
    return json(route, paged([record('record-1', { checked: posted > 0, checkedAt: posted ? '2026-11-02T01:00:00Z' : null })]))
  })
  await page.route('**/admin/meaning-submission-reviews/record-1/checked', route => { posted++; return route.abort('failed') })
  await open(page); await page.getByRole('button', { name: '对照记录' }).click()
  await page.getByRole('button', { name: '标记已核对' }).click()
  await expect(page.getByRole('alert')).toContainText('核对结果未知')
  await expect(page.getByRole('button', { name: '标记已核对' })).toBeDisabled()
  await page.getByRole('button', { name: '重试读取记录' }).click()
  await expect.poll(() => confirmReads).toBe(2); expect(posted).toBe(1)
  failConfirm = false
  await page.getByRole('button', { name: '重试读取记录' }).click()
  await expect(page.getByText('已回读确认核对成功', { exact: false })).toBeVisible()
  await expect(page.getByRole('region', { name: '审核记录对照' })).toContainText('已核对（')
  expect(posted).toBe(1)
})

test('明确拒绝和404不伪造已核对，保留原记录供刷新', async ({ page }) => {
  await setup(page)
  await page.route(/\/admin\/meaning-submission-reviews\?/, route => json(route, paged([record()])))
  await page.route('**/admin/meaning-submission-reviews/record-1/checked', route => json(route, null, 404))
  await open(page); await page.getByRole('button', { name: '对照记录' }).click()
  await page.getByRole('button', { name: '标记已核对' }).click()
  await expect(page.getByText('审核记录不存在。', { exact: true })).toBeVisible()
  await expect(page.getByRole('region', { name: '审核记录对照' })).toContainText('核对状态：未核对')
})

test('分页空页回退、失败重试与快速切换丢弃旧列表和详情', async ({ page }) => {
  await setup(page)
  let held: Route | undefined, fail = false, shrink = false
  await page.route(/\/admin\/meaning-submission-reviews\?/, route => {
    const params = new URL(route.request().url()).searchParams
    if (params.get('page') === '2' && !shrink) { held = route; return }
    if (fail) return json(route, null, 503)
    return json(route, paged([record(params.get('verdict') || 'record-1')], shrink ? 1 : 21))
  })
  await open(page); await page.getByRole('button', { name: '对照记录' }).click()
  await page.getByRole('button', { name: '下一页记录' }).click()
  await expect.poll(() => !!held).toBe(true)
  await page.getByLabel('AI 结果', { exact: true }).selectOption('reject')
  await expect(page.getByRole('region', { name: '审核记录对照' })).toHaveCount(0)
  await held?.fulfill({ contentType: 'application/json', body: JSON.stringify({ success: true, data: paged([record('OLD-PAGE')], 21, 2) }) }).catch(() => undefined)
  await expect(page.locator('body')).not.toContainText('OLD-PAGE')
  fail = true; await page.getByRole('button', { name: '刷新审核记录' }).click()
  await expect(page.getByRole('alert')).toContainText('审核记录加载失败')
  fail = false; shrink = true; await page.getByRole('button', { name: '重试读取记录' }).click()
  await expect(page.getByText('共 1 条审核记录', { exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: '下一页记录' })).toBeDisabled()
})

for (const status of [200, 401, 403]) test(`登出后记录迟到${status}不覆盖或清掉新会话`, async ({ page }) => {
  await setup(page)
  let held: Route | undefined, old = true
  await page.route('**/admin/auth/session', route => json(route, { username: old ? 'old-admin' : 'new-admin', roles: ['admin'] }))
  await page.route('**/admin/auth/logout', route => { old = false; return json(route, {}) })
  await page.route(/\/admin\/meaning-submission-reviews\?/, route => { if (old) { held = route; return }; return json(route, paged([record('NEW-SESSION')])) })
  await open(page); await expect.poll(() => !!held).toBe(true)
  await page.getByRole('button', { name: '退出登录' }).click()
  await expect(page).toHaveURL(/#\/login$/)
  await page.route('**/admin/auth/start*', route => route.fulfill({ contentType: 'text/html', body: '<script>location.replace("/#/submissions")</script>' }))
  await page.getByRole('button', { name: '使用 SignaCore 登录' }).click()
  await page.getByText('AI 审核记录', { exact: true }).click()
  await expect(page.getByText('共 1 条审核记录', { exact: true })).toBeVisible()
  await held?.fulfill({ status, contentType: 'application/json', body: JSON.stringify({ success: status === 200, data: paged([record('OLD-SESSION')]) }) }).catch(() => undefined)
  await expect(page.locator('.session')).toContainText('new-admin')
  await expect(page).toHaveURL(/#\/submissions$/)
  await page.getByRole('button', { name: '对照记录' }).click()
  await expect(page.getByRole('region', { name: '审核记录对照' })).toContainText('NEW-SESSION')
})

test('恶意文本按文本显示，额外供应商字段不显示、不存储、不外调，窄屏与键盘可用', async ({ page }) => {
  await setup(page)
  const logs: string[] = [], external: string[] = []
  page.on('console', msg => logs.push(msg.text()))
  page.on('request', request => { if (new URL(request.url()).origin !== 'http://127.0.0.1:4173') external.push(request.url()) })
  const hostile = '<img src=x onerror=alert(1)>'
  const row = record('hostile', { original: { ...record().original, word: hostile }, ai: { ...record().ai, comment: hostile } })
  await page.route(/\/admin\/meaning-submission-reviews\?/, route => json(route, { ...paged([{ ...row, rawResponse: 'synthetic-private-marker' }]), apiKey: 'synthetic-private-marker' }))
  await page.setViewportSize({ width: 768, height: 900 })
  await open(page); await page.getByRole('button', { name: '对照记录' }).click()
  const detail = page.getByRole('region', { name: '审核记录对照' })
  await expect(detail).toContainText(hostile); await expect(detail.locator('img')).toHaveCount(0)
  await expect(page.locator('body')).not.toContainText('synthetic-private-marker')
  expect(logs.join('\n')).not.toContain('synthetic-private-marker')
  expect(await page.evaluate(() => ({ local: localStorage.length, session: sessionStorage.length }))).toEqual({ local: 0, session: 0 })
  expect(external).toEqual([])
  await page.getByRole('radio', { name: 'AI 审核记录', exact: true }).evaluate(el => Promise.all(el.parentElement!.getAnimations({ subtree: true }).map(animation => animation.finished)))
  expect((await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze()).violations).toEqual([])
  await page.getByRole('button', { name: '标记已核对' }).focus()
  await expect(page.getByRole('button', { name: '标记已核对' })).toBeFocused()
})

test('切换记录时取消核对，迟到401不清会话，刷新先确认原记录', async ({ page }) => {
  await setup(page)
  let held: Route | undefined, posts = 0, confirmations = 0
  await page.route(/\/admin\/meaning-submission-reviews\?/, route => {
    const id = new URL(route.request().url()).searchParams.get('recordId')
    if (id) { confirmations++; return json(route, paged([record(id, { checked: true, checkedAt: '2026-11-02T01:00:00Z' })])) }
    return json(route, paged([record('first'), record('second')]))
  })
  await page.route('**/admin/meaning-submission-reviews/first/checked', route => { posts++; held = route })
  await open(page)
  await page.getByRole('button', { name: '对照记录' }).nth(0).click()
  await page.getByRole('button', { name: '标记已核对' }).click()
  await expect.poll(() => !!held).toBe(true)
  await page.getByRole('button', { name: '对照记录' }).nth(1).click()
  await held?.fulfill({ status: 401, contentType: 'application/json', body: JSON.stringify({ success: false, message: 'Old request' }) }).catch(() => undefined)
  await expect(page).toHaveURL(/#\/submissions$/)
  await expect(page.getByRole('region', { name: '审核记录对照' })).toContainText('second')
  await expect(page.getByRole('button', { name: '标记已核对' })).toBeDisabled()
  await page.getByRole('button', { name: '刷新审核记录' }).click()
  await expect.poll(() => confirmations).toBe(1)
  await expect(page.getByRole('button', { name: '标记已核对' })).toBeEnabled()
  expect(posts).toBe(1)
})

test('实际空页回退和空列表、非法DTO只显示固定错误', async ({ page }) => {
  await setup(page)
  let empty = false, invalid = false
  const pages: string[] = []
  await page.route(/\/admin\/meaning-submission-reviews\?/, route => {
    const current = new URL(route.request().url()).searchParams.get('page')!; pages.push(current)
    if (invalid) return json(route, { ...paged([record()]), items: [{ rawResponse: 'synthetic-private-marker' }] })
    if (current === '2' || empty) return json(route, paged([], empty ? 0 : 1, Number(current)))
    return json(route, paged([record()], pages.length === 1 ? 21 : 1))
  })
  await open(page); await page.getByRole('button', { name: '下一页记录' }).click()
  await expect.poll(() => pages.join(',')).toBe('1,2,1')
  await expect(page.getByRole('navigation', { name: '审核记录分页' })).toContainText('第 1 页')
  empty = true; await page.getByRole('button', { name: '刷新审核记录' }).click()
  await expect(page.getByText('没有符合筛选条件的审核记录。', { exact: true })).toBeVisible()
  invalid = true; await page.getByRole('button', { name: '刷新审核记录' }).click()
  await expect(page.getByRole('alert')).toContainText('审核记录加载失败')
  await expect(page.locator('body')).not.toContainText('synthetic-private-marker')
})
