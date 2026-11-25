import { expect, test, type Page, type Route } from '@playwright/test'
import {
  catalogFixture, catalogUrl, category, control, entry, json, mockSettings, nextRevision, notice, overridden,
  rateLimitUrl, rateLimitsOf, revision, row, reread, save, settingsNav, startupUrl, switchOf, unknownState
} from './support/settingsCatalog'
import type { Catalog } from './support/settingsCatalog'

const loginSwitch = (page: Page) => switchOf(page, '启用登录限流')
const loginPermits = (page: Page) => control(page, '登录请求次数上限')

// What a committed save leaves behind: the value in effect, read from the file. For a
// restart-only key it is also what the next start reads, so nothing is waiting.
function commit(catalog: Catalog, key: string, value: unknown) {
  const field = entry(catalog, key)
  field.value = value
  if (field.apply === 'restart') field.savedValue = value
  field.source = 'file'
}
// Element Plus renders a table as a header table and a body table; these are its data rows.
const table = (page: Page) => page.locator('.lx-table')
const tableRows = (page: Page) => page.locator('.lx-table .el-table__row')

test('settings opens on the overview, and every category reads the catalog alone', async ({ page }) => {
  const catalog = catalogFixture()
  await mockSettings(page, () => catalog)
  const requests: string[] = []
  page.on('request', request => {
    const path = new URL(request.url()).pathname
    if (path.startsWith('/admin/') && path.includes('settings')) requests.push(`${request.method()} ${path}`)
  })
  await page.goto('/#/books')
  await page.getByRole('link', { name: '系统设置', exact: true }).click()
  await expect(page).toHaveURL(/#\/settings$/)
  await expect(page.getByRole('heading', { name: '系统设置', level: 1 })).toBeVisible()
  const nav = settingsNav(page)
  // The API keys live in the database, not in the catalog; their page still sits beside the categories.
  await expect(nav.getByRole('link')).toHaveText([
    '概览', '全部配置', '登录与身份', '请求限流', '接入 API Key', '网络与代理', '数据保护', '数据库', '音频存储', '审核助手', '视觉推理',
    '语音生成', '后台策略', '词汇学习', '学习服务连接', '投稿功能', '日志', '遥测', '服务与运行信息'
  ])
  await expect(nav.getByRole('link', { name: '接入 API Key', exact: true })).toHaveAttribute('href', '#/settings/api-keys')
  await expect(nav.getByRole('link', { name: '概览', exact: true })).toHaveAttribute('aria-current', 'page')
  await expect(page.getByRole('link', { name: '立即生效：40 项' })).toBeVisible()
  await expect(page.getByRole('link', { name: '重启生效：61 项' })).toBeVisible()
  await expect(page.getByRole('link', { name: '只读：6 项' })).toBeVisible()
  await expect(page.getByRole('link', { name: '待重启：0 项' })).toBeVisible()
  await expect(page.getByRole('link', { name: '音频存储：19 项配置' })).toBeVisible()
  await expect(page.getByRole('status', { name: '待重启的配置' })).toHaveCount(0)
  expect(requests).toEqual(['GET /admin/system/settings/catalog'])

  await category(page, '请求限流').focus()
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/#\/settings\/rate-limits$/)
  await expect(page.getByRole('heading', { name: '请求限流', level: 2 })).toBeVisible()
  await expect(category(page, '请求限流')).toHaveAttribute('aria-current', 'page')
  await expect(page.getByRole('link', { name: '系统设置', exact: true })).toHaveClass(/is-active/)
  await expect(row(page, 'RateLimits:AdminLogin:Enabled')).toBeVisible()
  // The one policy of this release: no second switch, and no key of an earlier one.
  await expect(row(page, 'RateLimits:PublicApi:Enabled')).toHaveCount(0)
  expect(requests).toEqual(['GET /admin/system/settings/catalog', 'GET /admin/system/settings/catalog'])
})

test('the overview counts what is waiting for a restart and names the groups holding it', async ({ page }) => {
  const catalog = catalogFixture()
  entry(catalog, 'Audio:PlaybackTtlSeconds').pendingRestart = true
  entry(catalog, 'Audio:PlaybackTtlSeconds').savedValue = 1200
  entry(catalog, 'Logging:LogLevel:Default').pendingRestart = true
  entry(catalog, 'Logging:LogLevel:Default').savedValue = 'Debug'
  catalog.fileExists = true
  catalog.facts.find(fact => fact.id === 'container')!.value = 'true'
  await mockSettings(page, () => catalog)
  await page.goto('/#/settings')
  await expect(page.getByRole('link', { name: '待重启：2 项' })).toBeVisible()
  const waiting = page.getByRole('status', { name: '待重启的配置' })
  await expect(waiting).toContainText('2 项配置已保存，重启服务后生效')
  await expect(waiting).toContainText('docker restart lexarbor')
  await expect(category(page, '音频存储')).toHaveAttribute('title', '音频存储：1 项已保存，等待重启生效')
  await expect(category(page, '日志')).toHaveAttribute('title', '日志：1 项已保存，等待重启生效')
  await expect(category(page, '词汇学习')).not.toHaveAttribute('title')
  await waiting.getByRole('link', { name: '查看待重启项' }).click()
  await expect(page).toHaveURL(/#\/settings\/all\?filter=pending$/)
  await expect(page.getByRole('link', { name: '播放链接有效期', exact: true })).toBeVisible()
  await expect(page.getByRole('link', { name: '默认日志级别', exact: true })).toBeVisible()
  await expect(page.getByRole('link', { name: '身份提供方地址', exact: true })).toHaveCount(0)
})

test('the overview reports a configuration file that cannot be saved or was edited outside', async ({ page }) => {
  const catalog = catalogFixture()
  await mockSettings(page, () => catalog)
  catalog.storageAvailable = false
  await page.goto('/#/settings')
  await expect(page.getByRole('alert')).toContainText('配置文件当前无法读取或写入')
  // The operator fixes the directory and reads again; an outside edit is reported next.
  catalog.storageAvailable = true
  catalog.externallyModified = true
  await reread(page).click()
  await expect(page.getByRole('alert')).toHaveCount(0)
  await expect(page.getByRole('status', { name: '配置文件被外部修改' })).toContainText('在线保存已暂停，重启后恢复')
})

test('the search on the overview leads to the filtered list', async ({ page }) => {
  await mockSettings(page, catalogFixture())
  await page.goto('/#/settings')
  await page.getByLabel('搜索配置项', { exact: true }).fill('Audio')
  await page.getByRole('button', { name: '搜索', exact: true }).click()
  await expect(page).toHaveURL(/#\/settings\/all\?q=Audio$/)
  await expect(page.getByRole('link', { name: '服务访问地址', exact: true })).toBeVisible()
  await expect(page.getByRole('link', { name: '身份提供方地址', exact: true })).toHaveCount(0)
})

test('every configuration key is listed, searchable, filterable and paged', async ({ page }) => {
  const catalog = catalogFixture()
  entry(catalog, 'Audio:Region').editable = false
  entry(catalog, 'Audio:Region').source = 'environment'
  entry(catalog, 'Audio:Region').reason = overridden
  entry(catalog, 'Audio:PlaybackTtlSeconds').pendingRestart = true
  entry(catalog, 'Audio:PlaybackTtlSeconds').savedValue = 1200
  entry(catalog, 'DataProtection:RootKeyFile').source = 'file'
  await mockSettings(page, () => catalog)
  await page.goto('/#/settings/all')
  await expect(page.getByText('107 个配置项。点击任一项进入所属分组修改。')).toBeVisible()
  await expect(tableRows(page)).toHaveCount(20)
  await expect(page.getByRole('button', { name: '立即生效：40 项' })).toHaveAttribute('aria-pressed', 'false')

  await page.getByRole('button', { name: '待重启：1 项' }).click()
  await expect(page).toHaveURL(/#\/settings\/all\?filter=pending$/)
  await expect(tableRows(page)).toHaveCount(1)
  await expect(table(page)).toContainText('重启后为 1200 秒')

  await page.getByRole('button', { name: '被部署覆盖：1 项' }).click()
  await expect(tableRows(page)).toHaveCount(1)
  await expect(table(page)).toContainText('区域')
  await expect(table(page)).toContainText('环境变量')

  // A model connection is listed under the key of this release, in the reasoning model's category.
  await page.getByRole('button', { name: '全部：107 项' }).click()
  await page.getByLabel('搜索配置项', { exact: true }).fill('Models:Reasoning')
  await expect(tableRows(page)).toHaveCount(4)
  await expect(table(page)).toContainText('Models:Reasoning:Endpoint')
  await expect(table(page)).toContainText('视觉推理')

  await page.getByLabel('搜索配置项', { exact: true }).fill('Speech:Model')
  await expect(tableRows(page)).toHaveCount(1)
  await expect(table(page)).toContainText('语音模型')

  await page.getByLabel('搜索配置项', { exact: true }).fill('')
  await page.getByLabel('按分组筛选').selectOption('logging')
  await expect(page.getByRole('button', { name: '全部：3 项' })).toBeVisible()
  await expect(tableRows(page)).toHaveCount(3)

  await page.getByLabel('按分组筛选').selectOption('')
  await page.getByLabel('每页条数').selectOption('10')
  await expect(tableRows(page)).toHaveCount(10)
  await expect(page.getByText('共 107 项 · 第 1 / 11 页')).toBeVisible()
  await page.getByRole('button', { name: '下一页' }).click()
  await expect(page.getByText('第 2 / 11 页')).toBeVisible()
  await expect(table(page).getByRole('link', { name: '身份提供方地址', exact: true })).toHaveCount(0)
})

test('the list follows its address, also while it is already open', async ({ page }) => {
  const catalog = catalogFixture()
  entry(catalog, 'Audio:PlaybackTtlSeconds').pendingRestart = true
  entry(catalog, 'Audio:PlaybackTtlSeconds').savedValue = 1200
  await mockSettings(page, () => catalog)
  await page.goto('/#/settings/all')
  await expect(tableRows(page)).toHaveCount(20)
  // An address followed while the page is open, as a pasted link is: only the fragment
  // changes, so the page stays and must take the address's view over its own.
  await page.goto('/#/settings/all?filter=pending&q=Audio')
  await expect(page.getByRole('button', { name: '待重启：1 项' })).toHaveAttribute('aria-pressed', 'true')
  await expect(page.getByLabel('搜索配置项', { exact: true })).toHaveValue('Audio')
  await expect(tableRows(page)).toHaveCount(1)
  await expect(table(page)).toContainText('播放链接有效期')
  await page.goto('/#/settings/all')
  await expect(page.getByRole('button', { name: '全部：107 项' })).toHaveAttribute('aria-pressed', 'true')
  await expect(page.getByLabel('搜索配置项', { exact: true })).toHaveValue('')
  await expect(tableRows(page)).toHaveCount(20)
  // The page's own controls still write the address, each time they change the view.
  await page.getByLabel('按分组筛选').selectOption('logging')
  await expect(page).toHaveURL(/group=logging$/)
  await expect(tableRows(page)).toHaveCount(3)
  await page.getByLabel('按分组筛选').selectOption('')
  await expect(page).toHaveURL(/#\/settings\/all$/)
  await expect(tableRows(page)).toHaveCount(20)
})

test('an entry in the list opens its group page pointed at that row', async ({ page }) => {
  await mockSettings(page, catalogFixture())
  await page.goto('/#/settings/all')
  await page.getByRole('link', { name: '受信任的代理地址', exact: true }).click()
  await expect(page).toHaveURL(/#\/settings\/network\?focus=Network:TrustedProxies$/)
  await expect(row(page, 'Network:TrustedProxies')).toHaveClass(/is-focus/)
  await expect(control(page, '受信任的代理地址')).toBeVisible()
})

test('the list becomes cards on a phone, and nothing scrolls sideways', async ({ page }) => {
  await mockSettings(page, catalogFixture())
  await page.setViewportSize({ width: 375, height: 812 })
  await page.goto('/#/settings/all')
  await expect(table(page)).toHaveCount(0)
  const first = page.getByRole('link', { name: '打开 身份提供方地址', exact: true })
  await expect(first).toBeVisible()
  await expect(first).toContainText('IdentityService:Authority')
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  await page.getByLabel('每页条数').selectOption('50')
  await expect(page.locator('.lx-row')).toHaveCount(50)
})

test('a model connection an upgrade left under an earlier section says where the file keeps it', async ({ page }) => {
  const catalog = catalogFixture()
  Object.assign(entry(catalog, 'Models:Reasoning:Endpoint'), { value: 'https://api.example.test/v1', location: 'Enrichment:Text:Endpoint' })
  commit(catalog, 'Models:Reasoning:Model', 'reasoning-1')
  entry(catalog, 'Models:Reasoning:Model').location = 'Enrichment:Text:Model'
  await mockSettings(page, () => catalog)
  await page.goto('/#/settings/text')
  await expect(row(page, 'Models:Reasoning:Endpoint')).toContainText('文件里仍是旧节名 Enrichment:Text:Endpoint')
  await expect(row(page, 'Models:Reasoning:Model')).toContainText('文件里仍是旧节名 Enrichment:Text:Model')
  // A connection already under this release's section has nothing to say about its place.
  await expect(row(page, 'Models:Reasoning:ApiKey')).not.toContainText('旧节名')
})

test('a list of numbers is typed as text, checked in the page and sent as numbers', async ({ page }) => {
  const catalog = catalogFixture()
  await mockSettings(page, () => catalog)
  const bodies: unknown[] = []
  await page.route(startupUrl, route => {
    bodies.push(route.request().postDataJSON())
    commit(catalog, 'VocabularyLearning:Fsrs:LearningStepsMinutes', [5, 30])
    catalog.revision = nextRevision
    return json(route, catalog)
  })
  await page.goto('/#/settings/learning')
  const steps = control(page, '追加的学习步骤')
  await steps.fill('5')
  await steps.press('Enter')
  await steps.fill('30')
  await steps.press('Enter')
  await expect(row(page, 'VocabularyLearning:Fsrs:LearningStepsMinutes')).toContainText('5')
  await steps.fill('soon')
  await steps.press('Enter')
  await expect(row(page, 'VocabularyLearning:Fsrs:LearningStepsMinutes').getByRole('alert')).toHaveText('每项都要填整数。')
  await expect(save(page)).toBeDisabled()
  await page.getByRole('button', { name: '移除 soon', exact: true }).click()
  await save(page).click()
  await expect(notice(page)).toContainText('已保存到配置文件')
  expect(bodies).toEqual([{ 'VocabularyLearning:Fsrs:LearningStepsMinutes': [5, 30] }])

  // A list left with nothing in it removes the key, so the next start reads the default.
  await page.getByRole('button', { name: '移除 5', exact: true }).click()
  await page.getByRole('button', { name: '移除 30', exact: true }).click()
  await save(page).click()
  await expect(notice(page)).toContainText('已保存到配置文件')
  expect(bodies[1]).toEqual({ 'VocabularyLearning:Fsrs:LearningStepsMinutes': null })
})

test('a list of numbers the server reports is edited as text and sent back as numbers', async ({ page }) => {
  const catalog = catalogFixture()
  commit(catalog, 'VocabularyLearning:Fsrs:LearningStepsMinutes', [5, 30])
  await mockSettings(page, () => catalog)
  const bodies: unknown[] = []
  await page.route(startupUrl, route => { bodies.push(route.request().postDataJSON()); return json(route, catalog) })
  await page.goto('/#/settings/learning')
  const steps = row(page, 'VocabularyLearning:Fsrs:LearningStepsMinutes')
  // The numbers the server sent are the tags of the list, each editable as text.
  await expect(steps.locator('.lx-list__item')).toHaveText(['5', '30'])
  // Nothing is edited yet: what the server sent is the draft, and nothing waits to be saved.
  await expect(save(page)).toHaveCount(0)
  await page.getByRole('button', { name: '移除 30', exact: true }).click()
  await save(page).click()
  await expect(notice(page)).toContainText('已保存到配置文件')
  expect(bodies).toEqual([{ 'VocabularyLearning:Fsrs:LearningStepsMinutes': [5] }])
})

test('a decimal keeps its fraction, and a value the options refuse names its own row', async ({ page }) => {
  const catalog = catalogFixture()
  commit(catalog, 'VocabularyLearning:Fsrs:DesiredRetention', 0.9)
  await mockSettings(page, () => catalog)
  await page.route(startupUrl, route => json(route, { message: 'Invalid startup settings.', fields: ['VocabularyLearning:Fsrs:MasteredThresholdDays'] }, 400))
  await page.goto('/#/settings/learning')
  // The options' default is a number with a fraction, not text and not nothing.
  await expect(control(page, '目标记忆保持率')).toHaveValue('0.9')
  await control(page, '掌握门槛').fill('3')
  await control(page, '最长复习间隔').fill('20000')
  await save(page).click()
  await expect(notice(page)).toContainText('保存失败（400）')
  await expect(notice(page)).toContainText('涉及：掌握门槛')
  await expect(row(page, 'VocabularyLearning:Fsrs:MasteredThresholdDays').getByRole('alert')).toBeVisible()
  await expect(row(page, 'VocabularyLearning:Fsrs:MaximumIntervalDays').getByRole('alert')).toHaveCount(0)
  // The refused draft stays for review.
  await expect(control(page, '掌握门槛')).toHaveValue('3')
})

test('a live switch is saved with CSRF and the revision, then confirmed by a fresh read', async ({ page }) => {
  const catalog = catalogFixture()
  commit(catalog, 'RateLimits:AdminLogin:PermitLimit', 7)
  commit(catalog, 'RateLimits:AdminLogin:WindowSeconds', 120)
  await mockSettings(page, () => catalog)
  let puts = 0
  let gets = 0
  await page.route(catalogUrl, route => { gets++; return json(route, catalog) })
  await page.route(rateLimitUrl, async route => {
    if (route.request().method() === 'GET') return json(route, rateLimitsOf(catalog))
    puts++
    expect(route.request().postDataJSON()).toEqual({ adminLoginEnabled: true })
    expect(route.request().headers()['if-match']).toBe(revision)
    expect(route.request().headers()['x-signacore-csrf']).toBeTruthy()
    await new Promise(resolve => setTimeout(resolve, 200))
    commit(catalog, 'RateLimits:AdminLogin:Enabled', true)
    return json(route, rateLimitsOf(catalog))
  })
  await page.goto('/#/settings/rate-limits')
  await expect(control(page, '登录请求次数上限')).toHaveValue('7')
  await expect(control(page, '登录限流窗口')).toHaveValue('120')
  await expect(loginSwitch(page)).not.toBeChecked()
  await loginSwitch(page).focus()
  await page.keyboard.press('Space')
  await expect(loginSwitch(page)).toBeChecked()
  await expect(row(page, 'RateLimits:AdminLogin:Enabled')).toHaveClass(/is-changed/)
  await expect(page.getByRole('status', { name: '未保存的修改' })).toHaveText('1 处未保存，1 项保存后立即生效')
  await save(page).click()
  await expect(save(page)).toHaveCount(0)
  await expect(notice(page)).toHaveText('已保存到配置文件并立即生效，已回读确认。')
  expect(puts).toBe(1)
  expect(gets).toBe(2)
  await expect(loginSwitch(page)).toBeChecked()
  await expect(row(page, 'RateLimits:AdminLogin:Enabled')).toContainText('来源：配置文件')
})

test('a live switch and restart-only numbers go to their own endpoints, in order', async ({ page }) => {
  const catalog = catalogFixture()
  await mockSettings(page, () => catalog)
  const bodies: unknown[] = []
  const matches: (string | undefined)[] = []
  const answer = async (route: Route) => {
    const request = route.request()
    bodies.push([new URL(request.url()).pathname, request.postDataJSON()])
    matches.push(request.headers()['if-match'])
    await new Promise(resolve => setTimeout(resolve, 100))
    if (request.url().includes('rate-limits')) {
      commit(catalog, 'RateLimits:AdminLogin:Enabled', true)
      return json(route, rateLimitsOf(catalog, nextRevision))
    }
    entry(catalog, 'RateLimits:AdminLogin:PermitLimit').savedValue = 500
    entry(catalog, 'RateLimits:AdminLogin:PermitLimit').pendingRestart = true
    catalog.revision = nextRevision
    return json(route, catalog)
  }
  await page.route(rateLimitUrl, answer)
  await page.route(startupUrl, answer)
  await page.goto('/#/settings/rate-limits')
  await loginSwitch(page).check()
  await loginPermits(page).fill('500')
  await expect(page.getByRole('status', { name: '未保存的修改' })).toHaveText('2 处未保存，1 项保存后立即生效，1 项重启后生效')
  await save(page).click()
  await expect(notice(page)).toHaveText('已保存并回读确认：1 项立即生效，1 项将在重启服务后生效。')
  expect(bodies).toEqual([
    ['/admin/system/settings/rate-limits', { adminLoginEnabled: true }],
    ['/admin/system/settings/startup', { 'RateLimits:AdminLogin:PermitLimit': 500 }]
  ])
  expect(matches).toEqual([revision, nextRevision])
  // The switch is in effect now; the number waits for the restart.
  await expect(loginSwitch(page)).toBeChecked()
  await expect(row(page, 'RateLimits:AdminLogin:PermitLimit')).toContainText('待重启')
  await expect(row(page, 'RateLimits:AdminLogin:PermitLimit')).toContainText('当前生效')
  await expect(row(page, 'RateLimits:AdminLogin:PermitLimit')).toContainText('10 次')
  await expect(row(page, 'RateLimits:AdminLogin:PermitLimit')).toContainText('重启后')
  await expect(row(page, 'RateLimits:AdminLogin:PermitLimit')).toContainText('500 次')
})

for (const status of [400, 413, 503]) {
  test(`HTTP ${status} from a live save keeps the draft and the state in effect`, async ({ page }) => {
    await mockSettings(page, catalogFixture())
    let puts = 0
    await page.route(rateLimitUrl, route => {
      if (route.request().method() === 'GET') return json(route, rateLimitsOf(catalogFixture()))
      puts++
      return json(route, { message: 'Settings unavailable.' }, status)
    })
    await page.goto('/#/settings/rate-limits')
    await loginSwitch(page).focus()
    await page.keyboard.press('Space')
    await save(page).click()
    await expect(notice(page)).toContainText(`保存失败（${status}）`)
    await expect(loginSwitch(page)).toBeChecked()
    await expect(save(page)).toBeEnabled()
    expect(puts).toBe(1)
  })
}

test('a stale revision requires a fresh read and keeps the draft for review', async ({ page }) => {
  await mockSettings(page, catalogFixture())
  let reads = 0
  await page.route(catalogUrl, route => {
    reads++
    const catalog = catalogFixture()
    catalog.revision = reads > 1 ? nextRevision : revision
    return json(route, catalog)
  })
  await page.route(rateLimitUrl, route => route.request().method() === 'PUT'
    ? json(route, { message: 'Settings changed.' }, 412)
    : json(route, rateLimitsOf(catalogFixture())))
  await page.goto('/#/settings/rate-limits')
  await loginSwitch(page).focus()
  await page.keyboard.press('Space')
  await save(page).click()
  await expect(notice(page)).toContainText('保存失败（412）')
  await expect(unknownState(page)).toBeVisible()
  await expect(save(page)).toHaveCount(0)
  await reread(page).click()
  await expect(loginSwitch(page)).toBeChecked()
  await expect(save(page)).toBeEnabled()
})

test('failed and malformed reads are unknown until a read succeeds', async ({ page }) => {
  await mockSettings(page, catalogFixture())
  let reads = 0
  await page.route(catalogUrl, route => {
    reads++
    if (reads === 1) return json(route, {}, 503)
    if (reads === 2) return json(route, { revision })
    const catalog = catalogFixture()
    for (const key of ['RateLimits:AdminLogin:Enabled', 'RateLimits:AdminLogin:PermitLimit']) {
      Object.assign(entry(catalog, key), { editable: false, source: 'environment', reason: overridden })
    }
    return json(route, catalog)
  })
  await page.goto('/#/settings/rate-limits')
  await expect(unknownState(page)).toBeVisible()
  await expect(notice(page)).toContainText('状态未知')
  await reread(page).click()
  await expect(notice(page)).toContainText('状态未知')
  await reread(page).click()
  // The deployment owns two of the three keys now: their values are shown, locked, without a control.
  await expect(switchOf(page, '启用登录限流')).toHaveCount(0)
  await expect(control(page, '登录请求次数上限')).toHaveCount(0)
  await expect(row(page, 'RateLimits:AdminLogin:Enabled')).toContainText('关闭')
  await expect(row(page, 'RateLimits:AdminLogin:Enabled')).toContainText('来源：环境变量 · 网页只读')
  await expect(row(page, 'RateLimits:AdminLogin:Enabled')).not.toContainText('默认')
  await expect(control(page, '登录限流窗口')).toBeEnabled()
})

test('a lost PUT response is checked with a read and never resent', async ({ page }) => {
  const catalog = catalogFixture()
  await mockSettings(page, () => catalog)
  let puts = 0
  await page.route(rateLimitUrl, route => {
    if (route.request().method() === 'GET') return json(route, rateLimitsOf(catalog))
    puts++
    commit(catalog, 'RateLimits:AdminLogin:Enabled', true)
    return route.abort('failed')
  })
  await page.goto('/#/settings/rate-limits')
  await loginSwitch(page).focus()
  await page.keyboard.press('Space')
  await save(page).click()
  await expect(notice(page)).toContainText('保存响应未确认')
  await expect(loginSwitch(page)).toBeChecked()
  await expect(save(page)).toHaveCount(0)
  expect(puts).toBe(1)
})

test('a lost save and a failed confirmation stay unknown until an explicit read', async ({ page }) => {
  await mockSettings(page, catalogFixture())
  let reads = 0
  let puts = 0
  await page.route(catalogUrl, route => {
    reads++
    return reads === 2 ? route.abort('failed') : json(route, catalogFixture())
  })
  await page.route(rateLimitUrl, route => {
    if (route.request().method() === 'GET') return json(route, rateLimitsOf(catalogFixture()))
    puts++
    return route.abort('failed')
  })
  await page.goto('/#/settings/rate-limits')
  await loginSwitch(page).focus()
  await page.keyboard.press('Space')
  await save(page).click()
  await expect(notice(page)).toContainText('保存结果未知，回读失败')
  await expect(unknownState(page)).toBeVisible()
  await reread(page).click()
  await expect(loginSwitch(page)).toBeChecked()
  expect(puts).toBe(1)
})

test('a committed save whose confirmation cannot be read is not reported as a failure', async ({ page }) => {
  const catalog = catalogFixture()
  commit(catalog, 'RateLimits:AdminLogin:Enabled', true)
  let reads = 0
  await mockSettings(page, () => catalog)
  await page.route(catalogUrl, route => {
    reads++
    const current = catalogFixture()
    if (reads > 1) return json(route, { message: 'Unavailable.' }, 503)
    commit(current, 'RateLimits:AdminLogin:Enabled', false)
    return json(route, current)
  })
  await page.route(rateLimitUrl, route => json(route, rateLimitsOf(catalog)))
  await page.goto('/#/settings/rate-limits')
  await loginSwitch(page).focus()
  await page.keyboard.press('Space')
  await save(page).click()
  await expect(notice(page)).toContainText('保存结果未知，回读失败')
  await expect(notice(page)).not.toContainText('保存失败')
  await expect(unknownState(page)).toBeVisible()
})

test('leaving with unsaved changes asks first, and discarding restores the values in effect', async ({ page }) => {
  await mockSettings(page, catalogFixture())
  await page.goto('/#/settings/rate-limits')
  await loginSwitch(page).focus()
  await page.keyboard.press('Space')
  const dismissed = page.waitForEvent('dialog')
  page.once('dialog', dialog => dialog.dismiss())
  await category(page, '日志').click()
  expect((await dismissed).message()).toContain('未保存的修改')
  await expect(page).toHaveURL(/#\/settings\/rate-limits$/)
  await expect(loginSwitch(page)).toBeChecked()
  await page.getByRole('button', { name: '放弃修改', exact: true }).click()
  await expect(loginSwitch(page)).not.toBeChecked()
  await expect(save(page)).toHaveCount(0)
  page.once('dialog', dialog => dialog.accept())
  await loginSwitch(page).focus()
  await page.keyboard.press('Space')
  await category(page, '日志').click()
  await expect(page).toHaveURL(/#\/settings\/logging$/)
})

test('narrow screen and keyboard controls fit the viewport', async ({ page }) => {
  await mockSettings(page, catalogFixture())
  await page.setViewportSize({ width: 375, height: 812 })
  await page.goto('/#/settings/rate-limits')
  await expect(page.getByRole('link', { name: '系统设置', exact: true })).toBeVisible()
  await loginSwitch(page).focus()
  await page.keyboard.press('Space')
  await expect(loginSwitch(page)).toBeChecked()
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
})

test('leaving cancels reads; a late 401 cannot clear the session', async ({ page }) => {
  await mockSettings(page, catalogFixture())
  let pending: Route | undefined
  await page.route(catalogUrl, route => { pending = route })
  await page.goto('/#/settings/rate-limits')
  await expect.poll(() => Boolean(pending)).toBe(true)
  await page.getByRole('link', { name: '单条导入', exact: true }).click()
  await expect(page).toHaveURL(/#\/import$/)
  await json(pending!, { message: 'Unauthorized' }, 401).catch(() => undefined)
  await expect(page.locator('.session')).toContainText('ci-admin')
  await expect(page).toHaveURL(/#\/import$/)
})

test('logout clears pending settings and late results cannot restore the page', async ({ page }) => {
  await mockSettings(page, catalogFixture())
  let pending: Route | undefined
  await page.route(catalogUrl, route => { pending = route })
  await page.goto('/#/settings')
  await expect.poll(() => Boolean(pending)).toBe(true)
  await page.getByRole('button', { name: '退出登录' }).click()
  await expect(page).toHaveURL(/#\/login/)
  await json(pending!, catalogFixture()).catch(() => undefined)
  await expect(page.getByRole('link', { name: '立即生效：40 项' })).toHaveCount(0)
  await expect(settingsNav(page)).toHaveCount(0)
})

for (const target of ['/settings', '/settings/speech', '/settings/identity', '/settings/learning', '/settings/api-keys']) {
  test(`unauthorized navigation keeps the exact login return target ${target}`, async ({ page }) => {
    await page.route('**/admin/auth/session', route => json(route, { message: 'Unauthorized' }, 401))
    await page.route(`**/admin/auth/start?returnUrl=${encodeURIComponent(target)}`, route => route.fulfill({ contentType: 'text/html', body: '<p>Sign in</p>' }))
    await page.goto(`/#${target}`)
    await expect(page).toHaveURL(`/#/login?redirect=${target}`)
    await page.getByRole('button', { name: '使用 SignaCore 登录' }).click()
    await expect(page).toHaveURL(`/admin/auth/start?returnUrl=${encodeURIComponent(target)}`)
  })
}

test('a route outside the sign-in allowlist is dropped from the login target', async ({ page }) => {
  await page.route('**/admin/auth/session', route => json(route, { message: 'Unauthorized' }, 401))
  await page.route('**/admin/auth/start', route => route.fulfill({ contentType: 'text/html', body: '<p>Sign in</p>' }))
  await page.goto('/#/settings/unknown')
  await expect(page).toHaveURL('/#/login?redirect=/settings/unknown')
  await page.getByRole('button', { name: '使用 SignaCore 登录' }).click()
  await expect(page).toHaveURL(/\/admin\/auth\/start$/)
})

test('a forbidden session has no settings navigation', async ({ page }) => {
  await page.route('**/admin/auth/session', route => json(route, { message: 'Forbidden' }, 403))
  await page.goto('/#/settings')
  await expect(page).toHaveURL(/#\/forbidden/)
  await expect(page.getByRole('link', { name: '系统设置', exact: true })).toHaveCount(0)
})
