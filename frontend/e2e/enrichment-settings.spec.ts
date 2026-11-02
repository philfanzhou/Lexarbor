import { expect, test, type Page, type Route } from '@playwright/test'
import AxeBuilder from '@axe-core/playwright'
import { mockAntiforgery } from './support/antiforgery'

const url = '**/admin/enrichment/settings'
const revision = '"1234567890abcdef1234567890abcdef"'
const nextRevision = '"abcdef1234567890abcdef1234567890"'
function state() {
  const client = () => ({ endpoint: '', model: '', hasApiKey: false, timeoutSeconds: 30 })
  const body = { revision, configurationReady: false, reviewAssistant: client(), enrichment: {
    fieldsEnabled: false, audioEnabled: false, text: client(),
    tts: { ...client(), provider: 'MiniMaxNative', model: 'speech-2.8-hd', uk: { voice: '', speed: null as number | null }, us: { voice: '', speed: null as number | null } },
    concurrency: 1, failureBudget: 5, serviceFailureThreshold: 5, servicePauseSeconds: 60, maxServicePauseSeconds: 600,
    throttleInitialSeconds: 8, throttleMaxSeconds: 120, discoveryBatchSize: 20, scanIntervalSeconds: 30
  }, fields: {} as Record<string, { source: string; editable: boolean; reason: string | null }> }
  function fields(value: object, prefix = '') {
    for (const [key, item] of Object.entries(value)) {
      const name = prefix ? `${prefix}.${key}` : key
      if (typeof item === 'object' && item !== null) fields(item, name)
      else body.fields[name.replace(/hasApiKey$/, 'apiKey')] = { source: 'default', editable: true, reason: null }
    }
  }
  fields({ reviewAssistant: body.reviewAssistant, enrichment: body.enrichment })
  return body
}
function json(route: Route, value: unknown, status = 200) { return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(value) }) }
async function admin(page: Page) {
  await mockAntiforgery(page)
  await page.route('**/admin/auth/session', route => json(route, { username: 'ci-admin', roles: ['admin'] }))
  await page.route('**/admin/system/version', route => json(route, { version: '1.0.0', revision: null, channel: 'release' }))
  await page.route('**/admin/auth/logout', route => json(route, {}))
  await page.route('**/api/vocabulary-books/all', route => json(route, { success: true, data: { books: [] } }))
  await page.route('**/admin/system/settings/audio-storage', route => json(route, {}, 503))
  await page.route('**/admin/system/settings/rate-limits', route => json(route, { revision,
    adminLogin: { enabled: false, permitLimit: 10, windowSeconds: 300, source: 'default', editable: true },
    publicApi: { enabled: false, permitLimit: 300, windowSeconds: 60, source: 'default', editable: true } }))
}
const save = (page: Page) => page.getByRole('button', { name: '保存审核与补齐设置', exact: true })
const reload = (page: Page) => page.getByRole('button', { name: '重新读取审核与补齐', exact: true })
const notice = (page: Page) => page.getByRole('status', { name: '审核与补齐配置状态' })
const key = (page: Page, label = '审核 API Key') => page.getByLabel(label, { exact: true })
async function change(page: Page, model = 'review-draft') { await page.getByLabel('审核模型', { exact: true }).fill(model) }

// This mock represents the actual nested DTO. It never substitutes guessed backend routes.
test('default-off values, all policy bounds and no voice/speed defaults; independent advice/enrichment', async ({ page }) => {
  await admin(page); await page.route(url, route => json(route, state())); await page.goto('/#/settings')
  await expect(page.getByLabel('启用默认字段补齐', { exact: true })).not.toBeChecked()
  await expect(page.getByLabel('启用音频生成', { exact: true })).not.toBeChecked()
  await expect(page.getByLabel('语音提供方', { exact: true })).toHaveValue('MiniMaxNative')
  await expect(page.getByLabel('语音模型', { exact: true })).toHaveValue('speech-2.8-hd')
  for (const label of ['UK 音色', 'US 音色', 'UK 语速', 'US 语速']) await expect(page.getByLabel(label, { exact: true })).toHaveValue('')
  for (const [label, min, max] of [['审核超时（秒）', '1', '300'], ['补齐文本超时（秒）', '1', '300'], ['语音超时（秒）', '1', '300'], ['并发上限', '1', '8'], ['每步骤失败额度（含首次）', '1', '20'], ['服务连续失败暂停阈值', '1', '20'], ['服务暂停初始时间（秒）', '1', '600'], ['服务暂停最长时间（秒）', '1', '3600'], ['限流初始退避（秒）', '1', '120'], ['限流最长退避（秒）', '1', '3600'], ['每次发现上限', '1', '1000'], ['扫描间隔（秒）', '1', '3600'], ['UK 语速', '0.1', '4'], ['US 语速', '0.1', '4']]) {
    await expect(page.getByLabel(label!, { exact: true })).toHaveAttribute('min', min!)
    await expect(page.getByLabel(label!, { exact: true })).toHaveAttribute('max', max!)
  }
  await expect(page.getByLabel('语音能力限制')).toContainText('完整朗读目标的 IPA')
  await expect(page.getByLabel('语音能力限制')).toContainText('0.5–2')
  await expect(page.getByLabel('语音能力限制')).toContainText('0.25–4')
  await expect(page.getByRole('link', { name: '查看英文语音能力矩阵' })).toHaveAttribute('href', /Audio.md#speech-adapters$/)
  await expect(page.getByText('本页不启用自动审核', { exact: false })).toBeVisible()
  await expect(save(page)).toBeDisabled()
})

test('audio-only and US-only setup can save; nested changed fields, CSRF, If-Match and one PUT', async ({ page }) => {
  await admin(page)
  let current = state(), puts = 0
  await page.route(url, async route => {
    if (route.request().method() === 'GET') return json(route, current)
    puts++
    expect(route.request().headers()['if-match']).toBe(revision)
    expect(route.request().headers()['x-signacore-csrf']).toBeTruthy()
    expect(route.request().postDataJSON()).toEqual({ reviewAssistant: { apiKey: { action: 'keep' } }, enrichment: { audioEnabled: true,
      text: { endpoint: 'https://planner.test/v1', model: 'plan-model', apiKey: { action: 'replace', value: 'synthetic-plan-key' } },
      tts: { endpoint: 'https://api.minimax.io/v1', us: { voice: 'configured-us-voice', speed: 1.1 }, apiKey: { action: 'replace', value: 'synthetic-speech-key' } } } })
    await new Promise(resolve => setTimeout(resolve, 100))
    current = { ...current, revision: nextRevision, configurationReady: true, enrichment: { ...current.enrichment, audioEnabled: true,
      text: { ...current.enrichment.text, endpoint: 'https://planner.test/v1', model: 'plan-model', hasApiKey: true },
      tts: { ...current.enrichment.tts, endpoint: 'https://api.minimax.io/v1', hasApiKey: true, us: { voice: 'configured-us-voice', speed: 1.1 } } } }
    return json(route, current)
  })
  await page.goto('/#/settings')
  await page.getByLabel('启用音频生成', { exact: true }).check()
  await page.getByLabel('补齐文本服务地址', { exact: true }).fill('https://planner.test/v1')
  await page.getByLabel('补齐文本模型', { exact: true }).fill('plan-model')
  await key(page, '补齐文本 API Key').fill('synthetic-plan-key')
  await page.getByLabel('语音服务地址', { exact: true }).fill('https://api.minimax.io/v1')
  await page.getByLabel('US 音色', { exact: true }).fill('configured-us-voice')
  await page.getByLabel('US 语速', { exact: true }).fill('1.1')
  await key(page, '语音 API Key').fill('synthetic-speech-key')
  await save(page).click(); await expect(save(page)).toBeDisabled(); await expect(key(page, '语音 API Key')).toHaveValue('')
  await expect(notice(page)).toContainText('已保存并发布')
  expect(puts).toBe(1)
  await expect(page.getByLabel('UK 配置状态', { exact: true })).toContainText('等待：语音服务或该侧音色/语速缺失')
  await expect(page.getByLabel('US 配置状态', { exact: true })).toContainText('本地必填配置齐全')
  await expect(page.getByLabel('启用默认字段补齐', { exact: true })).not.toBeChecked()
  await page.reload(); await expect(page.getByLabel('US 语速', { exact: true })).toHaveValue('1.1')
})

test('keep/replace/clear are independent; blank replacement blocks save and empty locale speed sends null', async ({ page }) => {
  await admin(page)
  const current = state(); current.enrichment.tts.uk = { voice: 'existing-uk', speed: 1 }
  const payloads: unknown[] = []
  await page.route(url, route => { if (route.request().method() === 'PUT') payloads.push(route.request().postDataJSON()); return json(route, current) })
  await page.goto('/#/settings'); await change(page); await save(page).click(); await expect(notice(page)).toContainText('已保存')
  expect(payloads[0]).toEqual({ reviewAssistant: { model: 'review-draft', apiKey: { action: 'keep' } }, enrichment: { text: { apiKey: { action: 'keep' } }, tts: { apiKey: { action: 'keep' } } } })
  await page.getByRole('combobox', { name: '审核 API Key操作', exact: true }).selectOption('clear')
  await page.getByRole('combobox', { name: '补齐文本 API Key操作', exact: true }).selectOption('replace')
  await expect(save(page)).toBeDisabled()
  await key(page, '补齐文本 API Key').fill('synthetic-replacement')
  await page.getByLabel('UK 语速', { exact: true }).fill('')
  await save(page).click(); await expect(notice(page)).toContainText('已保存')
  expect(payloads[1]).toEqual({ reviewAssistant: { apiKey: { action: 'clear' } }, enrichment: { text: { apiKey: { action: 'replace', value: 'synthetic-replacement' } }, tts: { uk: { speed: null }, apiKey: { action: 'keep' } } } })
  for (const label of ['审核 API Key', '补齐文本 API Key', '语音 API Key']) await expect(key(page, label)).toHaveValue('')
})

for (const status of [400, 409, 412, 413, 428, 503]) {
  test(`safe ${status} retains nonsecret draft, clears keys; conflicts require explicit reload`, async ({ page }) => {
    await admin(page); let puts = 0
    await page.route(url, route => { if (route.request().method() === 'GET') return json(route, state()); puts++; return json(route, { message: 'synthetic-secret-not-for-display' }, status) })
    await page.goto('/#/settings'); await change(page); await key(page).fill('synthetic-secret-not-for-display'); await save(page).click()
    await expect(notice(page)).toContainText(`保存失败（${status}）`)
    await expect(page.locator('body')).not.toContainText('synthetic-secret-not-for-display')
    if ([409, 412, 428].includes(status)) {
      await expect(save(page)).toHaveCount(0); await reload(page).click()
    }
    await expect(page.getByLabel('审核模型', { exact: true })).toHaveValue('review-draft')
    await expect(key(page)).toHaveValue(''); expect(puts).toBe(1)
  })
}

for (const failure of ['lost', 'malformed'] as const) {
  test(`${failure} PUT response recovers by GET only and retains nonsecret draft`, async ({ page }) => {
    await admin(page); let reads = 0, puts = 0
    await page.route(url, route => {
      if (route.request().method() === 'GET') { reads++; const value = state(); value.reviewAssistant.model = reads === 1 ? '' : 'server-model'; return json(route, value) }
      puts++; return failure === 'lost' ? route.abort('failed') : json(route, { revision: nextRevision })
    })
    await page.goto('/#/settings'); await change(page); await key(page).fill('synthetic-no-replay'); await save(page).click()
    await expect(notice(page)).toContainText('保存响应未确认，已回读')
    expect(puts).toBe(1); expect(reads).toBe(2)
    await expect(page.getByText('当前生效：server-model', { exact: false })).toBeVisible()
    await expect(page.getByLabel('审核模型', { exact: true })).toHaveValue('review-draft')
    await expect(key(page)).toHaveValue('')
  })
}

test('lost save and failed read stay unknown; manual read clears secret and never replays PUT', async ({ page }) => {
  await admin(page); let reads = 0, puts = 0
  await page.route(url, route => {
    if (route.request().method() === 'PUT') { puts++; return route.abort('failed') }
    reads++; return reads === 2 ? json(route, {}, 503) : json(route, state())
  })
  await page.goto('/#/settings'); await change(page); await key(page).fill('synthetic-no-replay'); await save(page).click()
  await expect(notice(page)).toContainText('保存结果未知，回读失败'); await expect(save(page)).toHaveCount(0)
  await reload(page).click(); await expect(key(page)).toHaveValue(''); expect(puts).toBe(1)
  await key(page).fill('synthetic-clear-on-read'); await reload(page).click(); await expect(key(page)).toHaveValue('')
})

test('failed/malformed GET means unknown; source locks show reason, discard stale locked draft and omit locked values', async ({ page }) => {
  await admin(page); let reads = 0
  const value = state(); value.reviewAssistant.model = 'deployed-model'
  value.fields['reviewAssistant.model'] = { source: 'environment', editable: false, reason: 'Overridden by deployment configuration.' }
  value.fields['reviewAssistant.apiKey'] = { source: 'commandLine', editable: false, reason: 'Overridden by deployment configuration.' }
  await page.route(url, route => {
    if (route.request().method() === 'PUT') {
      expect(route.request().postDataJSON()).toEqual({ enrichment: { concurrency: 2, text: { apiKey: { action: 'keep' } }, tts: { apiKey: { action: 'keep' } } } })
      return json(route, value)
    }
    reads++; return json(route, reads === 1 ? {} : reads === 2 ? { revision } : value, reads === 1 ? 503 : 200)
  })
  await page.goto('/#/settings'); await expect(notice(page)).toContainText('状态未知')
  await reload(page).click(); await expect(notice(page)).toContainText('状态未知')
  await reload(page).click(); await expect(page.getByLabel('审核模型', { exact: true })).toBeDisabled()
  await expect(key(page)).toBeDisabled(); await expect(page.getByText('网页只读：Overridden by deployment configuration.', { exact: false })).toHaveCount(2)
  await page.getByLabel('并发上限', { exact: true }).fill('2'); await save(page).click(); await expect(notice(page)).toContainText('已保存')
})

for (const other of ['panel', 'administrator'] as const) {
  test(`${other} save makes shared revision stale; explicit reread uses new ETag and never old key`, async ({ page, browser }) => {
    await admin(page); let currentRevision = revision, puts = 0
    await page.route('**/admin/system/settings/rate-limits', route => {
      if (route.request().method() === 'PUT') currentRevision = nextRevision
      return json(route, { revision: currentRevision, adminLogin: { enabled: false, permitLimit: 10, windowSeconds: 300, source: 'default', editable: true }, publicApi: { enabled: true, permitLimit: 300, windowSeconds: 60, source: 'file', editable: true } })
    })
    await page.route(url, route => {
      const value = { ...state(), revision: currentRevision }
      if (route.request().method() === 'GET') return json(route, value)
      puts++
      if (route.request().headers()['if-match'] !== currentRevision) return json(route, {}, 412)
      expect(route.request().postDataJSON()).toEqual({ reviewAssistant: { model: 'review-draft', apiKey: { action: 'keep' } }, enrichment: { text: { apiKey: { action: 'keep' } }, tts: { apiKey: { action: 'keep' } } } })
      value.reviewAssistant.model = 'review-draft'; return json(route, value)
    })
    await page.goto('/#/settings'); await change(page); await key(page).fill('synthetic-stale-key')
    if (other === 'panel') {
      await page.getByLabel('公共 API草稿开关', { exact: true }).uncheck()
      await page.getByRole('button', { name: '保存限流设置', exact: true }).click()
      await expect(page.getByRole('status').first()).toContainText('保存后已回读')
    } else {
      const second = await browser.newPage()
      await admin(second)
      await second.route('**/admin/auth/session', route => json(route, { username: 'second-admin', roles: ['admin'] }))
      await second.route(url, route => {
        const value = state()
        if (route.request().method() === 'PUT') {
          expect(route.request().headers()['if-match']).toBe(revision)
          currentRevision = nextRevision
          value.reviewAssistant.model = 'other-admin-model'
        }
        return json(route, { ...value, revision: currentRevision })
      })
      await second.goto('/#/settings'); await change(second, 'other-admin-model'); await save(second).click()
      await expect(notice(second)).toContainText('已保存')
      await second.close()
    }
    await save(page).click(); await expect(notice(page)).toContainText('保存失败（412）'); expect(puts).toBe(1)
    await reload(page).click(); await expect(page.getByLabel('审核模型', { exact: true })).toHaveValue('review-draft')
    await expect(key(page)).toHaveValue(''); expect(puts).toBe(1)
    await save(page).click(); await expect(notice(page)).toContainText('已保存'); expect(puts).toBe(2)
  })
}

for (const outcome of ['success', '401', '403'] as const) {
  test(`late ${outcome} PUT cannot populate or redirect replacement panel after navigation`, async ({ page }) => {
    await admin(page); let pending: Route | undefined
    await page.route(url, route => route.request().method() === 'GET' ? json(route, state()) : (pending = route, undefined))
    await page.goto('/#/settings'); await change(page); await key(page).fill('synthetic-private-draft'); await save(page).click()
    await expect.poll(() => Boolean(pending)).toBe(true)
    await page.getByRole('link', { name: '单条导入', exact: true }).click()
    await page.getByRole('link', { name: '系统设置', exact: true }).click()
    const late = state(); late.reviewAssistant.model = 'late-model'
    await json(pending!, outcome === 'success' ? late : {}, outcome === 'success' ? 200 : Number(outcome)).catch(() => undefined)
    await expect(page.getByLabel('审核模型', { exact: true })).toHaveValue('')
    await expect(key(page)).toHaveValue(''); await expect(page.locator('.session')).toContainText('ci-admin')
    await expect(page).toHaveURL(/#\/settings$/)
  })
}

for (const leave of ['navigate', 'logout'] as const) {
  test(`${leave} discards unsent password drafts; logout clears before server completes`, async ({ page }) => {
    await admin(page); await page.route(url, route => json(route, state()))
    let pendingLogout: Route | undefined
    if (leave === 'logout') await page.route('**/admin/auth/logout', route => { pendingLogout = route })
    await page.goto('/#/settings'); await change(page); await key(page).fill('synthetic-unsent-draft')
    if (leave === 'navigate') {
      await page.getByRole('link', { name: '单条导入', exact: true }).click(); await page.getByRole('link', { name: '系统设置', exact: true }).click()
      await expect(key(page)).toHaveValue(''); await expect(page.getByLabel('审核模型', { exact: true })).toHaveValue('')
    } else {
      await page.getByRole('button', { name: '退出登录' }).click()
      await expect.poll(() => Boolean(pendingLogout)).toBe(true)
      await expect(key(page)).toHaveCount(0)
      await json(pendingLogout!, {}); await expect(page).toHaveURL(/#\/login/)
    }
  })
}

for (const status of [401, 403]) {
  test(`current ${status} keeps existing authentication behavior`, async ({ page }) => {
    await admin(page); await page.route(url, route => json(route, {}, status)); await page.goto('/#/settings')
    await expect(page).toHaveURL(status === 401 ? /#\/login/ : /#\/forbidden/)
  })
}

test('narrow keyboard-accessible panel has no credentials in storage, URL, console or supplier requests', async ({ page }) => {
  await admin(page); await page.route(url, route => json(route, state()))
  const logs: string[] = [], requests: string[] = []
  page.on('console', message => logs.push(message.text())); page.on('request', request => requests.push(request.url()))
  await page.setViewportSize({ width: 375, height: 812 }); await page.goto('/#/settings')
  await page.getByLabel('启用默认字段补齐', { exact: true }).focus(); await page.keyboard.press('Space')
  await expect(page.getByLabel('启用默认字段补齐', { exact: true })).toBeChecked()
  for (const label of ['审核 API Key', '补齐文本 API Key', '语音 API Key']) {
    await key(page, label).fill('synthetic-memory-only'); await expect(key(page, label)).toHaveAttribute('type', 'password')
  }
  expect(await page.evaluate(() => ({ local: { ...localStorage }, session: { ...sessionStorage } }))).toEqual({ local: {}, session: {} })
  expect(page.url() + logs.join()).not.toContain('synthetic-memory-only')
  expect(requests.every(value => new URL(value).hostname === '127.0.0.1')).toBe(true)
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  expect((await new AxeBuilder({ page }).include('.enrichment-settings').withTags(['wcag2a', 'wcag2aa']).analyze()).violations).toEqual([])
})


test('new deployment lock after conflict adopts locked actual value and preserves other drafts', async ({ page }) => {
  await admin(page); let reads = 0
  await page.route(url, route => {
    if (route.request().method() === 'PUT') return json(route, {}, 409)
    const value = state(); reads++
    if (reads > 1) {
      value.reviewAssistant.model = 'locked-model'
      value.fields['reviewAssistant.model'] = { source: 'environment', editable: false, reason: 'Overridden by deployment configuration.' }
    }
    return json(route, value)
  })
  await page.goto('/#/settings'); await change(page); await page.getByLabel('补齐文本模型', { exact: true }).fill('retained-plan')
  await save(page).click(); await expect(notice(page)).toContainText('保存失败（409）'); await reload(page).click()
  await expect(page.getByLabel('审核模型', { exact: true })).toBeDisabled()
  await expect(page.getByLabel('审核模型', { exact: true })).toHaveValue('locked-model')
  await expect(page.getByLabel('补齐文本模型', { exact: true })).toHaveValue('retained-plan')
})

test('invalid numeric input cannot dispatch; no provider or model change fills voice/speed', async ({ page }) => {
  await admin(page); let puts = 0
  await page.route(url, route => { if (route.request().method() === 'PUT') puts++; return json(route, state()) })
  await page.goto('/#/settings'); await page.getByLabel('并发上限', { exact: true }).fill('9')
  await save(page).click(); expect(puts).toBe(0)
  expect(await page.getByLabel('并发上限', { exact: true }).evaluate(node => (node as HTMLInputElement).validity.rangeOverflow)).toBe(true)
  await page.getByLabel('语音提供方', { exact: true }).selectOption('OpenAICompatible')
  await page.getByLabel('语音模型', { exact: true }).fill('gpt-4o-mini-tts')
  for (const label of ['UK 音色', 'US 音色', 'UK 语速', 'US 语速']) await expect(page.getByLabel(label, { exact: true })).toHaveValue('')
})

for (const outcome of ['success', '401', '403'] as const) {
  test(`logout generation invalidates pending ${outcome} before logout itself finishes`, async ({ page }) => {
    await admin(page); let pending: Route | undefined, pendingLogout: Route | undefined
    await page.route('**/admin/auth/logout', route => { pendingLogout = route })
    await page.route(url, route => route.request().method() === 'GET' ? json(route, state()) : (pending = route, undefined))
    await page.goto('/#/settings'); await change(page); await key(page).fill('synthetic-logout-race'); await save(page).click()
    await expect.poll(() => Boolean(pending)).toBe(true)
    await page.getByRole('button', { name: '退出登录' }).click(); await expect.poll(() => Boolean(pendingLogout)).toBe(true)
    await expect(key(page)).toHaveCount(0)
    await json(pending!, outcome === 'success' ? state() : {}, outcome === 'success' ? 200 : Number(outcome)).catch(() => undefined)
    await expect(page).toHaveURL(/#\/settings$/)
    await expect(save(page)).toHaveCount(0)
    await json(pendingLogout!, {}); await expect(page).toHaveURL(/#\/login/)
    await page.goto('/?replacement-session=1#/settings')
    await expect(page.getByLabel('审核模型', { exact: true })).toHaveValue('')
    await expect(key(page)).toHaveValue('')
  })
}
