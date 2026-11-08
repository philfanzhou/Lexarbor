import { test, expect, type Page, type Route } from '@playwright/test'
import AxeBuilder from '@axe-core/playwright'
import { mockAntiforgery } from './support/antiforgery'
import type { EnrichmentTask } from '../src/services/enrichmentApi'

function json(route: Route, data: unknown, status = 200) {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify({ success: status === 200, ...(status === 200 ? { data } : { message: 'Synthetic failure.' }) }) })
}
const now = 1793577600000
function task(id = 'task', changes: Partial<EnrichmentTask> = {}): EnrichmentTask {
  return { id, wordId: 'word', word: 'lead', meaningId: 'meaning', meaning: 'guide', locale: 'en-GB', step: 'Audio', applicable: true, missing: true, inheritsDefault: false, state: 'Waiting', waitReason: 'Throttled',
    current: { number: 1, state: 'Waiting', requestCount: 7, consumedFailures: 0, failureBudget: 5, waitReason: 'Throttled', errorCode: 'provider.throttled', nextAttemptAtUnixMs: now + 60000, createdAtUnixMs: now, updatedAtUnixMs: now },
    cleanup: { waiting: 0, running: 0, observed: 0, attempts: 0, nextAttemptAtUnixMs: null, errorCode: null }, ...changes }
}
function failed() { const t = task(); t.state = 'Failed'; Object.assign(t.current, { state: 'Failed', consumedFailures: 5, errorCode: 'enrichment.failed' }); return t }
function paged<T>(items: T[], page = 1, size = 10, totalCount = items.length) { return { items, totalCount, page, size } }
function summary() {
  return { applicableTargets: 5, completedTargets: 1, missingTargets: 4, unqueuedTargets: 2, persistedTasks: 3,
    activities: [
      { step: 'Fields', total: 2, completed: 0, actionRequired: 2, unqueued: 2, missingRequirements: ['text'] },
      { step: 'Expand', total: 1, completed: 0, actionRequired: 0, unqueued: 0, missingRequirements: ['supplement'] },
      { step: 'Audio', total: 2, completed: 1, actionRequired: 0, unqueued: 0, missingRequirements: ['ukSpeech', 'storage'] }
    ],
    targets: [{ state: 'Waiting', waitReason: 'Disabled', count: 2 }, { state: 'Succeeded', waitReason: null, count: 1 }, { state: 'Waiting', waitReason: 'Throttled', count: 2 }],
    services: [{ service: 'text', paused: false, blocked: false, consecutiveFailures: 0, throttleSeconds: 0, nextAttemptAtUnixMs: null }, { service: 'tts', paused: true, blocked: false, consecutiveFailures: 5, throttleSeconds: 8, nextAttemptAtUnixMs: now + 60000 }] }
}
function detail(t: EnrichmentTask, historyPage = 1) {
  return { task: t, rounds: paged([t.current], historyPage),
    calls: paged([{ id: 'call', round: 1, service: 'tts', result: 'Throttled', errorCode: 'provider.throttled', startedAtUnixMs: now, finishedAtUnixMs: now + 1 }], historyPage),
    waits: paged([{ round: 1, reason: 'Throttled', errorCode: 'provider.throttled', recordedAtUnixMs: now, nextAttemptAtUnixMs: now + 60000 }], historyPage),
    cleanup: paged([], historyPage) }
}
async function setup(page: Page, rows = [task()]) {
  await mockAntiforgery(page)
  await page.route('**/admin/auth/session', route => json(route, { username: 'enrichment-admin', roles: ['admin'] }))
  await page.route('**/admin/system/version', route => json(route, { version: 'unknown', revision: null, channel: 'development' }))
  await page.route(/\/admin\/enrichment\/summary(?:\?.*)?$/, route => json(route, summary()))
  await page.route(/\/admin\/enrichment\/tasks(?:\?.*)?$/, route => json(route, paged(rows, 1, 25)))
  await page.route(/\/admin\/enrichment\/tasks\/[^/?]+(?:\?.*)?$/, route => json(route, detail(rows.find(t => t.id === new URL(route.request().url()).pathname.split('/').pop()) ?? task())))
}
async function openDetail(page: Page) {
  await page.goto('/#/enrichment')
  await page.getByRole('button', { name: '查看 lead en-GB 音频生成 详情', exact: true }).click()
  return page.getByRole('region', { name: '任务详情', exact: true })
}

test('新增历史与部分成功、未排队关闭、429请求和额度分开展示且无批次入口', async ({ page }) => {
  await setup(page, [task(), task('uk-success', { meaningId: null, meaning: null, state: 'Succeeded', missing: false, locale: 'en-US' })])
  await page.goto('/#/enrichment')
  const counts = page.getByRole('region', { name: '当前需求与服务' })
  await expect(counts).toContainText('已完成 1 / 5 个目标 · 仍需完善 4')
  await expect(counts).toContainText('2 个等待后台扫描排队')
  await expect(counts.getByRole('article', { name: '音标与词性' })).toContainText('配置未就绪')
  await expect(page.getByLabel('自动刷新状态')).toContainText('新增优先、历史持续推进')
  const table = page.getByRole('table', { name: '补齐任务列表' })
  await expect(table).not.toContainText('已消耗')
  await expect(table).toContainText('服务限流')
  await expect(table).toContainText('已完成')
  await expect(page.getByRole('button', { name: /重试此失败步骤|开始批次/ })).toHaveCount(0)
  await page.getByRole('button', { name: '查看 lead en-GB 音频生成 详情', exact: true }).click()
  const detail = page.getByRole('region', { name: '任务详情', exact: true })
  await expect(detail).toContainText('本轮模型请求7 次')
  await expect(detail).toContainText('已消耗 0 / 5')
})

for (const [reason, text] of [
  ['Disabled', '生成或字段补齐已关闭'], ['ConfigurationMissing', '等待模型、口音或补充词书配置就绪'],
  ['PreparationPending', '等待前置发音准备完成'],
  ['StorageDisabled', '音频存储关闭'], ['ServicePaused', '服务暂停，等待恢复探测'],
  ['Retry', '等待下次自动尝试'], ['LeaseExpired', '执行租约已过期，等待恢复']
]) test(`等待 ${reason} 不显示完成或单条重试`, async ({ page }) => {
  const t = task(); t.waitReason = reason; t.current.waitReason = reason
  await setup(page, [t]); const region = await openDetail(page)
  await expect(region).toContainText(text)
  await expect(region.getByRole('button', { name: /重试此失败步骤/ })).toHaveCount(0)
})

test('服务恢复保留耗尽，能力不支持须修正配置，成功侧无重试', async ({ page }) => {
  const t = failed(); t.current.errorCode = 'provider.capability_unsupported'
  await setup(page, [t])
  const s = summary(); s.services[1].paused = false; s.services[1].consecutiveFailures = 0
  await page.route(/\/admin\/enrichment\/summary(?:\?.*)?$/, route => json(route, s))
  const region = await openDetail(page)
  await expect(region).toContainText('失败额度已耗尽；服务恢复不会清零')
  await expect(region).toContainText('服务或模型不支持所需读音，请修正配置')
  await expect(region.getByRole('button', { name: '仅重试此失败步骤', exact: true })).toBeEnabled()
  await expect(page.getByLabel('tts 服务状态')).toContainText('未暂停')
})

for (const [code, message] of [
  ['text.phonetic_constraint_mismatch', '准备读音与词库音标不符'],
  ['text.template_unexpanded', '朗读模板未完整展开'],
  ['text.pronunciation_control_unsupported', '准备计划无法表达所需读音']
]) test(`准备错误 ${code} 显示具体原因并保留单条重试`, async ({ page }) => {
  const t = failed(); t.current.errorCode = code
  await setup(page, [t]); const region = await openDetail(page)
  await expect(region).toContainText(message)
  await expect(region.getByRole('button', { name: '仅重试此失败步骤', exact: true })).toBeEnabled()
})

test('旧清理失败和新生成成功独立，清空只恢复继承，默认回落不是own完成', async ({ page }) => {
  const t = task('task', { state: 'Succeeded', missing: false })
  t.current.state = 'Succeeded'; t.cleanup = { waiting: 1, running: 0, observed: 0, attempts: 8, nextAttemptAtUnixMs: now + 60000, errorCode: 'audio.invalidation_failed' }
  await setup(page, [t]); const region = await openDetail(page)
  await expect(region).toContainText('已完成')
  await expect(region).toContainText('旧专用音频已停用')
  await expect(region).toContainText('旧文件清理失败，后台将继续处理')
  await expect(region).toContainText('清理尝试 8')
  await expect(region.getByRole('button', { name: /重试此失败步骤/ })).toHaveCount(0)
  Object.assign(t, { state: 'Cancelled', applicable: false, missing: false, inheritsDefault: true })
  await page.getByRole('button', { name: '刷新状态', exact: true }).click()
  await expect(region).toContainText('专用音标已清空，继承默认；无需生成专用音频')
  await expect(region).toContainText('旧文件清理失败')
  Object.assign(t, { state: 'Pending', applicable: true, missing: true, inheritsDefault: false })
  await page.getByRole('button', { name: '刷新状态', exact: true }).click()
  await expect(region).toContainText('默认播放回落不代表专用生成完成')
})

test('单条重试带实际轮次、UUID及CSRF，防双击并重新GET', async ({ page }) => {
  const t = failed(); await setup(page, [t])
  const writes: { body: Record<string, unknown>; csrf: string | undefined }[] = []
  let release!: () => void
  await page.route('**/tasks/task/retry', async route => {
    writes.push({ body: route.request().postDataJSON(), csrf: route.request().headers()['x-signacore-csrf'] })
    await new Promise<void>(resolve => { release = resolve })
    Object.assign(t, { state: 'Pending' }); Object.assign(t.current, { number: 2, state: 'Pending', consumedFailures: 0 })
    return json(route, { targetId: t.id, round: 2, replayed: false })
  })
  const region = await openDetail(page)
  await region.getByRole('button', { name: '仅重试此失败步骤', exact: true }).evaluate(button => { (button as HTMLButtonElement).click(); (button as HTMLButtonElement).click() })
  await expect.poll(() => writes.length).toBe(1)
  expect(writes[0].body.expectedRound).toBe(1)
  expect(writes[0].body.retryKey).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/)
  expect(Object.keys(writes[0].body).sort()).toEqual(['expectedRound', 'retryKey'])
  expect(writes[0].csrf).toBe('synthetic-antiforgery-token')
  release()
  await expect(region).toContainText('当前第 2 轮')
  await expect(region.getByRole('button', { name: /重试此失败步骤/ })).toHaveCount(0)
})

for (const outcome of ['committed', 'unchanged', 'read-failed', 'conflict', 'deleted'] as const) test(`重试结果 ${outcome} 先GET且不自动新key重放`, async ({ page }) => {
  const t = failed(); await setup(page, [t])
  const writes: Record<string, unknown>[] = []; let detailReads = 0
  await page.route(/\/tasks\/task\?/, route => {
    detailReads += 1
    return writes.length && (outcome === 'read-failed' || outcome === 'deleted') ? json(route, null, outcome === 'deleted' ? 404 : 503) : json(route, detail(t))
  })
  await page.route('**/tasks/task/retry', route => {
    writes.push(route.request().postDataJSON())
    if (outcome === 'committed') { t.current.number = 2; t.state = 'Pending'; t.current.state = 'Pending' }
    if (outcome === 'conflict') { t.current.number = 2; return json(route, null, 409) }
    return route.abort('failed')
  })
  const region = await openDetail(page)
  await region.getByRole('button', { name: '仅重试此失败步骤', exact: true }).click()
  await expect.poll(() => detailReads).toBe(2)
  expect(writes).toHaveLength(1)
  if (outcome === 'unchanged') {
    await expect(region.getByRole('button', { name: '使用原请求重试此失败步骤', exact: true })).toBeEnabled()
    await region.getByRole('button', { name: '使用原请求重试此失败步骤', exact: true }).click()
    await expect.poll(() => writes.length).toBe(2)
    expect(writes[1]).toEqual(writes[0])
  } else if (outcome === 'deleted') {
    await expect(region).toContainText('任务不存在或已删除，请选择其他任务')
    await expect(page.getByText('正在核对单条重试结果；不会自动重复提交。')).toHaveCount(0)
  } else if (outcome === 'read-failed') {
    await expect(region).toContainText('任务详情状态未知')
    await expect(region.getByRole('button', { name: /重试此失败步骤/ })).toHaveCount(0)
  } else await expect(region).toContainText('当前第 2 轮')
})

test('任务与四类历史真实分页，切换筛选取消迟到读取', async ({ page }) => {
  await setup(page)
  const requests: URL[] = []; let held: Route | null = null
  await page.route(/\/tasks(?:\?.*)?$/, route => {
    const url = new URL(route.request().url()); requests.push(url)
    if (url.searchParams.get('page') === '2') { held = route; return }
    return json(route, paged([task()], 1, 25, 26))
  })
  await page.route(/\/tasks\/task\?/, route => {
    const p = Number(new URL(route.request().url()).searchParams.get('historyPage'))
    const d = detail(task(), p); d.rounds.totalCount = 11; d.calls.totalCount = 11; d.waits.totalCount = 11; d.cleanup.totalCount = 11
    if (p === 2) { d.calls.items[0].id = 'second'; d.calls.items[0].result = 'TerminalFailure' }
    return json(route, d)
  })
  const region = await openDetail(page)
  await region.getByText('技术详情与执行历史', { exact: true }).click()
  await region.getByRole('button', { name: '下一页历史' }).click()
  await expect(region).toContainText('本轮停止')
  await expect(region.getByRole('navigation', { name: '历史分页' })).toContainText('第 2 页')
  await page.getByRole('button', { name: '下一页任务' }).click()
  await expect.poll(() => held !== null).toBe(true)
  await page.getByLabel('口音', { exact: true }).selectOption('en-US')
  await page.getByRole('button', { name: '应用筛选' }).click()
  await expect(page.getByRole('navigation', { name: '任务分页' })).toContainText('第 1 / 2 页')
  await held?.fulfill({ contentType: 'application/json', body: JSON.stringify({ success: true, data: paged([task('late', { word: 'LATE-OLD-PAGE' })], 2, 25, 26) }) }).catch(() => undefined)
  await expect(page.getByRole('table')).not.toContainText('LATE-OLD-PAGE')
  expect(requests.at(-1)?.searchParams.get('locale')).toBe('en-US')
  expect(requests.at(-1)?.searchParams.get('size')).toBe('25')
})

test('空列表与GET错误保持未知，连续失败有界，隐藏暂停并在返回时刷新', async ({ page }) => {
  await page.clock.install()
  await setup(page, [])
  let reads = 0, broken = false
  await page.route(/\/tasks(?:\?.*)?$/, route => { reads += 1; return broken ? json(route, null, 503) : json(route, paged([], 1, 25)) })
  await page.goto('/#/enrichment')
  await expect(page.getByText('仍有 2 个目标等待扫描排队，请查看上方配置提示。')).toBeVisible()
  await page.evaluate(() => { Object.defineProperty(document, 'hidden', { configurable: true, value: true }); document.dispatchEvent(new Event('visibilitychange')) })
  await page.clock.fastForward(60000)
  expect(reads).toBe(1)
  await expect(page.getByLabel('自动刷新状态')).toContainText('页面隐藏时暂停刷新')
  broken = true
  await page.evaluate(() => { Object.defineProperty(document, 'hidden', { configurable: true, value: false }); document.dispatchEvent(new Event('visibilitychange')) })
  await expect.poll(() => reads).toBe(2)
  await expect(page.getByRole('alert')).toContainText('任务列表状态未知')
  await page.clock.fastForward(10000); await expect.poll(() => reads).toBe(3)
  await page.clock.fastForward(20000); await expect.poll(() => reads).toBe(4)
  await expect(page.getByLabel('自动刷新状态')).toContainText('自动刷新已暂停')
  await page.clock.fastForward(60000); expect(reads).toBe(4)
  broken = false
  await page.getByRole('button', { name: '刷新状态', exact: true }).click()
  await expect.poll(() => reads).toBe(5)
})

for (const status of [200, 401, 403]) test(`离开和登出后迟到 ${status} 不污染新会话`, async ({ page }) => {
  await setup(page)
  let held: Route | null = null, live = true, reads = 0
  await page.route('**/admin/auth/session', route => json(route, { username: live ? 'old-admin' : 'new-admin', roles: ['admin'] }))
  await page.route('**/admin/auth/logout', route => { live = false; return json(route, {}) })
  await page.route(/\/tasks(?:\?.*)?$/, route => { reads += 1; if (reads === 1) { held = route; return }; return json(route, paged([task('new', { word: 'NEW-SESSION' })], 1, 25)) })
  await page.goto('/#/enrichment'); await expect.poll(() => held !== null).toBe(true)
  await page.getByRole('button', { name: '退出登录' }).click()
  await expect(page).toHaveURL(/#\/login$/)
  await page.route('**/admin/auth/start*', route => route.fulfill({ contentType: 'text/html', body: '<script>location.replace("/#/enrichment")</script>' }))
  await page.getByRole('button', { name: '使用 SignaCore 登录' }).click()
  await expect(page.getByRole('table')).toContainText('NEW-SESSION')
  await held?.fulfill({ status, contentType: 'application/json', body: JSON.stringify(status === 200 ? { success: true, data: paged([task('old', { word: 'OLD-SESSION' })], 1, 25) } : { success: false, message: 'Expired old session' }) }).catch(() => undefined)
  await expect(page.locator('.session')).toContainText('new-admin')
  await expect(page).toHaveURL(/#\/enrichment$/)
  await expect(page.getByRole('table')).not.toContainText('OLD-SESSION')
})

test('安全DTO文本渲染、浏览器无秘密存储日志及供应商请求，键盘和窄屏可用', async ({ page }) => {
  const logs: string[] = [], external: string[] = []
  page.on('console', message => logs.push(message.text()))
  page.on('request', request => { if (new URL(request.url()).origin !== 'http://127.0.0.1:4173') external.push(request.url()) })
  await setup(page, [task('task', { word: '<img src=x onerror=alert(1)>' })])
  await page.route(/\/tasks(?:\?.*)?$/, route => json(route, { ...paged([{ ...task('task', { word: '<img src=x onerror=alert(1)>' }), apiKey: 'synthetic-private-marker' }], 1, 25), apiKey: 'synthetic-private-marker', planJson: 'synthetic-private-marker' }))
  await page.setViewportSize({ width: 768, height: 900 })
  await page.goto('/#/enrichment')
  await expect(page.getByRole('table')).toBeVisible()
  await expect(page.getByRole('table')).toContainText('<img src=x onerror=alert(1)>')
  await expect(page.getByRole('table').locator('img')).toHaveCount(0)
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze()).violations
  expect(violations).toEqual([])
  const storage = await page.evaluate(() => ({ local: localStorage.length, session: sessionStorage.length }))
  expect(storage).toEqual({ local: 0, session: 0 })
  expect(logs.join('\n')).not.toContain('synthetic-private-marker')
  await expect(page.locator('body')).not.toContainText('synthetic-private-marker')
  expect(external).toEqual([])
  const speechSettings = page.getByRole('link', { name: '检查英式语音配置' })
  await speechSettings.focus()
  await expect(speechSettings).toBeFocused()
  await expect(speechSettings).toHaveAttribute('href', '#/settings/speech')
  await expect(page.getByRole('link', { name: '配置文本模型' })).toHaveAttribute('href', '#/settings/text')
  await expect(page.getByRole('link', { name: '配置音频存储' })).toHaveAttribute('href', '#/settings/audio-storage')
  await expect(page.getByRole('link', { name: '后台策略' })).toHaveAttribute('href', '#/settings/background')
})

test('处理中与旧成功后的新需求保留投影，不提供成功或取消任务重试', async ({ page }) => {
  const running = task('running', { word: 'running', state: 'Running' }); running.current.state = 'Running'
  const changed = task('changed', { word: 'changed', state: 'Pending' }); changed.current.state = 'Succeeded'
  const cancelled = task('cancelled', { word: 'cancelled', state: 'Cancelled', applicable: false }); cancelled.current.state = 'Succeeded'
  await setup(page, [running, changed, cancelled]); await page.goto('/#/enrichment')
  await expect(page.getByRole('row').filter({ hasText: 'running' })).toContainText('处理中')
  await expect(page.getByRole('row').filter({ hasText: 'changed' })).toContainText('待处理')
  await expect(page.getByRole('row').filter({ hasText: 'cancelled' })).toContainText('当前不再需要此目标；历史保留')
  await page.getByRole('button', { name: '查看 changed en-GB 音频生成 详情' }).click()
  const region = page.getByRole('region', { name: '任务详情', exact: true })
  await expect(region).toContainText('持久状态 已完成')
  await expect(region).toContainText('专用音频仍缺失：待处理')
  await expect(region.getByRole('button', { name: /重试此失败步骤/ })).toHaveCount(0)
})

test('切换详情目标取消旧history响应，轮询不重入，离开后迟到401不清会话', async ({ page }) => {
  await page.clock.install(); await setup(page, [task(), task('other', { word: 'other' })])
  let held: Route | null = null, calls = 0
  await page.route(/\/tasks\/task\?/, route => { held = route; calls += 1 })
  await page.goto('/#/enrichment')
  await page.getByRole('button', { name: '查看 lead en-GB 音频生成 详情' }).click()
  await expect.poll(() => held !== null).toBe(true)
  await page.clock.fastForward(20000)
  expect(calls).toBe(1)
  await page.getByRole('button', { name: '查看 other en-GB 音频生成 详情' }).click()
  await expect(page.getByRole('region', { name: '任务详情', exact: true })).toContainText('other')
  await held?.fulfill({ status: 401, contentType: 'application/json', body: JSON.stringify({ success: false, message: 'old read expired' }) }).catch(() => undefined)
  await expect(page.locator('.session')).toContainText('enrichment-admin')
  await expect(page).toHaveURL(/#\/enrichment$/)
  await page.getByRole('link', { name: '投稿审核', exact: true }).click()
  await expect(page).toHaveURL(/#\/submissions$/)
  await page.clock.fastForward(60000)
  expect(calls).toBe(1)
})

test('从状态到真实词条编辑入口再按释义返回，使用word/meaning过滤且关闭抽屉', async ({ page }) => {
  await setup(page)
  const queries: URL[] = []
  await page.route(/\/tasks(?:\?.*)?$/, route => { queries.push(new URL(route.request().url())); return json(route, paged([task()], 1, 25)) })
  await page.route('**/admin/vocabulary/word', route => json(route, { id: 'word', word: 'lead', phoneticUk: 'li:d', phoneticUs: 'li:d', books: [{ id: 'book', bookName: 'Fixture', status: true }], meanings: [{ id: 'meaning', vocabularyId: 'word', bookId: 'book', meaning: 'guide', phoneticUk: null, phoneticUs: null, pronunciation: { uk: { phonetic: 'li:d', source: 'default' }, us: { phonetic: 'li:d', source: 'default' } }, expectedUk: { epoch: 1, sourceRevision: 'a'.repeat(64), ownAssetId: null }, expectedUs: { epoch: 1, sourceRevision: 'b'.repeat(64), ownAssetId: null }, units: [] }] }))
  await page.route('**/admin/vocabulary/word/audio*', route => json(route, { enabled: false, items: [] }))
  const region = await openDetail(page)
  await region.getByRole('button', { name: '查看词条并编辑数据' }).click()
  await expect(page.getByRole('button', { name: '编辑', exact: true })).toBeVisible()
  await page.getByRole('link', { name: '查看本释义音频更新状态' }).click()
  await expect(page).toHaveURL(/#\/enrichment\?wordId=word&meaningId=meaning$/)
  await expect(page.locator('.word-detail-drawer')).not.toBeVisible()
  expect(queries.at(-1)?.searchParams.get('wordId')).toBe('word')
  expect(queries.at(-1)?.searchParams.get('meaningId')).toBe('meaning')
})

test('重试期间路由目标更换取消旧写入，迟到失败不能覆盖新目标', async ({ page }) => {
  await setup(page, [failed()]); let held: Route | null = null
  await page.route('**/tasks/task/retry', route => { held = route })
  const region = await openDetail(page)
  await region.getByRole('button', { name: '仅重试此失败步骤', exact: true }).click()
  await expect.poll(() => held !== null).toBe(true)
  await page.evaluate(() => { location.hash = '/enrichment?wordId=new-target' })
  await expect(page.getByLabel('词条 ID', { exact: true })).toHaveValue('new-target')
  await expect(page.getByRole('region', { name: '任务详情', exact: true })).toHaveCount(0)
  await held?.fulfill({ status: 401, contentType: 'application/json', body: JSON.stringify({ success: false, message: 'old action expired' }) }).catch(() => undefined)
  await expect(page.locator('.session')).toContainText('enrichment-admin')
  await expect(page.getByLabel('单条重试结果')).toHaveCount(0)
  await expect(page.getByRole('button', { name: '刷新状态', exact: true })).toBeEnabled()
})


test('词汇扩展使用同一进度页，显示不限口音并可筛选详情', async ({ page }) => {
  const row = task('expansion', { locale: null, step: 'Expand', state: 'Succeeded', missing: false, waitReason: null })
  row.current = { ...row.current, state: 'Succeeded', requestCount: 1, consumedFailures: 0, waitReason: null, errorCode: null }
  await setup(page, [row])
  await page.goto('/#/enrichment')
  const table = page.getByRole('table', { name: '补齐任务列表' })
  await expect(table).toContainText('不限口音')
  await expect(table).toContainText('词形与派生词')
  await page.getByLabel('步骤', { exact: true }).selectOption('Expand')
  await page.getByRole('button', { name: '查看 lead 不限口音 词形与派生词扩展 详情', exact: true }).click()
  await expect(page.getByRole('region', { name: '任务详情', exact: true })).toContainText('词形与派生词扩展')
})


test('按词搜索、任务入口与需要处理筛选保留完整范围进度', async ({ page }) => {
  await setup(page)
  const lists: URL[] = [], summaries: URL[] = []
  await page.route(/\/tasks(?:\?.*)?$/, route => { lists.push(new URL(route.request().url())); return json(route, paged([task()], 1, 25)) })
  await page.route(/\/summary(?:\?.*)?$/, route => { summaries.push(new URL(route.request().url())); return json(route, summary()) })
  await page.goto('/#/enrichment')
  await page.getByLabel('搜索单词或释义', { exact: true }).fill('  lead  ')
  await page.getByRole('button', { name: '应用筛选', exact: true }).click()
  await expect.poll(() => lists.at(-1)?.searchParams.get('search')).toBe('lead')
  await page.getByRole('article', { name: '单词读音' }).getByRole('button', { name: '查看相关任务' }).click()
  await expect.poll(() => lists.at(-1)?.searchParams.get('step')).toBe('Audio')
  await page.getByRole('button', { name: '需要处理', exact: true }).click()
  await expect.poll(() => lists.at(-1)?.searchParams.get('attentionOnly')).toBe('true')
  expect(lists.at(-1)?.searchParams.get('sort')).toBe('attention')
  expect(summaries.at(-1)?.searchParams.get('search')).toBe('lead')
  expect(summaries.at(-1)?.searchParams.has('attentionOnly')).toBe(false)
  expect(summaries.at(-1)?.searchParams.has('step')).toBe(false)
  await expect(page.getByRole('article', { name: '音标与词性' })).toBeVisible()
  await page.getByRole('button', { name: '重置筛选', exact: true }).click()
  await expect.poll(() => lists.at(-1)?.searchParams.has('search')).toBe(false)
  await expect(page.getByLabel('步骤', { exact: true })).toHaveValue('')
})

test('技术历史默认收起，旧轮次被替代可读，窄屏详情可关闭并通过无障碍检查', async ({ page }) => {
  await setup(page, [failed()])
  await page.route(/\/tasks\/task\?/, route => {
    const d = detail(failed()); d.rounds.items.push({ ...d.task.current, state: 'Superseded', number: 0 })
    return json(route, d)
  })
  await page.setViewportSize({ width: 390, height: 844 })
  const region = await openDetail(page)
  await expect(region.getByRole('heading', { name: '任务详情', exact: true })).toBeFocused()
  await expect(region.getByText('本轮已停止，需要处理', { exact: true })).toBeVisible()
  await expect(region.getByRole('heading', { name: /轮次历史/ })).not.toBeVisible()
  await region.getByText('技术详情与执行历史', { exact: true }).click()
  await expect(region.getByText(/已被新一轮替代/)).toBeVisible()
  expect((await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze()).violations).toEqual([])
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  await region.getByRole('button', { name: '关闭任务详情' }).click()
  await expect(region).toHaveCount(0)
  await expect(page.getByRole('button', { name: '查看 lead en-GB 音频生成 详情', exact: true })).toBeFocused()
})


test('文本请求与任务参与次数分别展示，旧服务响应仍可读取', async ({ page }) => {
  await setup(page)
  await page.route(/\/admin\/enrichment\/summary(?:\?.*)?$/, route => json(route, {
    ...summary(), textRequests: { requests: 2, participations: 9, completed: 8, unknown: 1 }
  }))
  await page.goto('/#/enrichment')
  const stats = page.getByLabel('文本请求统计')
  await expect(stats).toContainText('全站文本请求 2 次，处理 9 项任务，已保存 8 项有效结果')
  await expect(stats).toContainText('1 次请求正在处理或结果尚未确认')
  await expect(stats).toContainText('包含补齐、词变和自动审核')
})

test('自动恢复从排队到处理再到耗尽，只有停止后才显示手动重试', async ({ page }) => {
  const t = failed(); t.waitReason = null; t.current.waitReason = null; t.current.errorCode = 'batch.timeout'; t.automaticRecoveryAvailable = true
  await setup(page, [t]); const region = await openDetail(page)
  await expect(page.getByRole('table', { name: '补齐任务列表' }).locator('.badge')).toHaveText('等待自动恢复')
  await expect(region).toContainText('后台会自动重新处理这项技术性失败')
  await expect(region.getByRole('button', { name: /重试此失败步骤/ })).toHaveCount(0)
  t.automaticRecoveryAvailable = false; t.state = 'Waiting'; t.waitReason = 'Retry'
  Object.assign(t.current, { state: 'Waiting', automaticRecovery: true, forceSingle: true, consumedFailures: 2, number: 2 })
  await page.getByRole('button', { name: '刷新状态', exact: true }).click()
  await expect(region).toContainText('自动恢复：等待下次尝试')
  await expect(region).toContainText('已消耗 2/5 次失败额度')
  await expect(region).toContainText('此任务会单独请求 AI')
  await expect(region.getByRole('button', { name: /重试此失败步骤/ })).toHaveCount(0)
  t.state = 'Failed'; t.waitReason = null; t.current.state = 'Failed'; t.current.consumedFailures = 5
  await page.getByRole('button', { name: '刷新状态', exact: true }).click()
  await expect(region).toContainText('自动恢复已停止，待复核')
  await expect(region).toContainText('不会因重启再次开启')
  await expect(region.getByRole('button', { name: '仅重试此失败步骤', exact: true })).toBeEnabled()
})

test('已确认的短语字段不显示为未完成或需要重试', async ({ page }) => {
  const t = task('phrase', { step: 'Fields', applicable: false, missing: false, state: 'Cancelled', waitReason: null })
  Object.assign(t.current, { fieldsNotRequired: true, state: 'Succeeded', waitReason: null, errorCode: null })
  await setup(page, [t]); await page.goto('/#/enrichment')
  const table = page.getByRole('table', { name: '补齐任务列表' })
  await expect(table.locator('.badge')).toHaveText('无需补全')
  await page.getByRole('button', { name: '查看 lead en-GB 默认字段 详情', exact: true }).click()
  const region = page.getByRole('region', { name: '任务详情', exact: true })
  await expect(region).toContainText('短语无需补全')
  await expect(region).toContainText('读音生成单独继续')
  await expect(region.getByRole('button', { name: /重试此失败步骤/ })).toHaveCount(0)
})
