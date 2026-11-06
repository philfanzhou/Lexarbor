import { expect, test, type Page, type Route } from '@playwright/test'
import AxeBuilder from '@axe-core/playwright'
import { mockAntiforgery } from './support/antiforgery'

const url = '**/admin/enrichment/settings'
const revision = '"1234567890abcdef1234567890abcdef"'
const nextRevision = '"abcdef1234567890abcdef1234567890"'
function state() {
  const voice = () => ({ voice: '', speed: null as number | null, style: '', emotion: '', pitch: 0, volume: 1 })
  const client = () => ({ endpoint: '', model: '', hasApiKey: false, timeoutSeconds: 30 })
  const body = { revision, configurationReady: false, reviewAssistant: { ...client(), autoReviewEnabled: false }, enrichment: {
    fieldsEnabled: false, audioEnabled: false, text: client(),
    tts: { ...client(), provider: 'MiniMaxNative', model: 'speech-2.8-hd', uk: voice(), us: voice() },
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
const tab = (page: Page, title: string) => page.getByRole('navigation', { name: '设置分类' }).getByRole('link', { name: title, exact: true })
const save = (page: Page, title = '审核助手') => page.getByRole('button', { name: `保存${title}设置`, exact: true })
const reload = (page: Page) => page.getByRole('button', { name: '重新读取审核助手', exact: true })
const notice = (page: Page, title = '审核助手') => page.getByRole('status', { name: `${title}配置状态` })
const key = (page: Page, label = '审核 API Key') => page.getByLabel(label, { exact: true })
async function change(page: Page, model = 'review-draft') { await page.getByLabel('审核模型', { exact: true }).fill(model) }

test('settings sections keep independent defaults, policy bounds and optional voice settings', async ({ page }) => {
  await admin(page)
  await page.route(url, route => json(route, state()))
  await page.goto('/#/settings/review')
  await expect(page.getByLabel('启用 AI 自动审核投稿（默认关闭）', { exact: true })).not.toBeChecked()
  await expect(page.getByText('管理员承担此风险', { exact: false })).toBeVisible()
  await expect(save(page)).toBeDisabled()
  await expect(page.getByLabel('审核超时（秒）')).toHaveAttribute('max', '300')
  await expect(page.getByLabel('启用默认字段补齐')).toHaveCount(0)

  await tab(page, '文本补齐').click()
  await expect(page.getByLabel('启用默认字段补齐', { exact: true })).toHaveCount(0)
  await expect(page.getByLabel('启用音频生成')).toHaveCount(0)
  await expect(page.getByLabel('补齐文本超时（秒）')).toHaveAttribute('max', '300')

  await tab(page, '语音生成').click()
  await expect(page.getByLabel('启用音频生成', { exact: true })).toHaveCount(0)
  await expect(page.getByLabel('语音提供方', { exact: true })).toHaveValue('MiniMaxNative')
  await expect(page.getByLabel('语音模型', { exact: true })).toHaveValue('speech-2.8-hd')
  for (const label of ['UK 音色', 'US 音色', 'UK 语速', 'US 语速']) await expect(page.getByLabel(label, { exact: true })).toHaveValue('')
  for (const label of ['UK 语速', 'US 语速']) {
    await expect(page.getByLabel(label)).toHaveAttribute('min', '0.1')
    await expect(page.getByLabel(label)).toHaveAttribute('max', '4')
  }
  await expect(page.getByLabel('语音超时（秒）')).toHaveAttribute('max', '300')
  const matrix = page.getByRole('link', { name: '查看英文语音能力矩阵' })
  await expect(matrix).toBeHidden()
  await page.getByText('语音能力限制', { exact: true }).click()
  await expect(matrix).toBeVisible()
  await expect(matrix).toHaveAttribute('href', /Audio.md#speech-adapters$/)
  await expect(page.locator('details')).toContainText('完整朗读目标的 IPA')
  await expect(page.locator('details')).toContainText('0.5–2')
  await expect(page.locator('details')).toContainText('0.25–4')

  await tab(page, '后台策略').click()
  for (const [label, min, max] of [
    ['并发上限', '1', '8'], ['每步骤失败额度（含首次）', '1', '20'], ['服务连续失败暂停阈值', '1', '20'],
    ['服务暂停初始时间（秒）', '1', '600'], ['服务暂停最长时间（秒）', '1', '3600'], ['限流初始退避（秒）', '1', '120'],
    ['限流最长退避（秒）', '1', '3600'], ['每次发现上限', '1', '1000'], ['扫描间隔（秒）', '1', '3600']
  ] as const) {
    await expect(page.getByLabel(label, { exact: true })).toHaveAttribute('min', min)
    await expect(page.getByLabel(label, { exact: true })).toHaveAttribute('max', max)
  }
  await expect(page.getByLabel('语音模型', { exact: true })).toHaveCount(0)
})

test('text preparation and US-only speech save separately using the current revision', async ({ page }) => {
  await admin(page)
  let current = state(), puts = 0
  await page.route(url, async route => {
    if (route.request().method() === 'GET') return json(route, current)
    puts++
    expect(route.request().headers()['x-signacore-csrf']).toBeTruthy()
    if (puts === 1) {
      expect(route.request().headers()['if-match']).toBe(revision)
      expect(route.request().postDataJSON()).toEqual({ enrichment: {
        text: { endpoint: 'https://planner.test/v1', model: 'plan-model', apiKey: { action: 'replace', value: 'synthetic-plan-key' } }
      } })
      current = { ...current, revision: nextRevision, configurationReady: true, enrichment: { ...current.enrichment,
        text: { ...current.enrichment.text, endpoint: 'https://planner.test/v1', model: 'plan-model', hasApiKey: true }
      } }
    } else {
      expect(route.request().headers()['if-match']).toBe(nextRevision)
      expect(route.request().postDataJSON()).toEqual({ enrichment: { tts: {
        endpoint: 'https://api.minimax.io/v1', us: { voice: 'configured-us-voice', speed: 1.1 },
        apiKey: { action: 'replace', value: 'synthetic-speech-key' }
      } } })
      await new Promise(resolve => setTimeout(resolve, 100))
      current = { ...current, enrichment: { ...current.enrichment, audioEnabled: true,
        tts: { ...current.enrichment.tts, endpoint: 'https://api.minimax.io/v1', hasApiKey: true, us: { ...current.enrichment.tts.us, voice: 'configured-us-voice', speed: 1.1 } }
      } }
    }
    return json(route, current)
  })
  await page.goto('/#/settings/text')
  await expect(page.getByLabel('启用默认字段补齐', { exact: true })).toHaveCount(0)
  await page.getByLabel('补齐文本服务地址', { exact: true }).fill('https://planner.test/v1')
  await page.getByLabel('补齐文本模型', { exact: true }).fill('plan-model')
  await key(page, '补齐文本 API Key').fill('synthetic-plan-key')
  await save(page, '文本补齐').click()
  await expect(notice(page, '文本补齐')).toContainText('已保存并发布')

  await tab(page, '语音生成').click()
  await page.getByLabel('语音服务地址', { exact: true }).fill('https://api.minimax.io/v1')
  await page.getByLabel('US 音色', { exact: true }).fill('configured-us-voice')
  await page.getByLabel('US 语速', { exact: true }).fill('1.1')
  await key(page, '语音 API Key').fill('synthetic-speech-key')
  await save(page, '语音生成').click()
  await expect(save(page, '语音生成')).toBeDisabled()
  await expect(key(page, '语音 API Key')).toHaveValue('')
  await expect(notice(page, '语音生成')).toContainText('已保存并发布')
  expect(puts).toBe(2)
  await expect(page.getByLabel('UK 配置状态', { exact: true })).toContainText('等待：语音服务或该侧音色/语速缺失')
  await expect(page.getByLabel('US 配置状态', { exact: true })).toContainText('本地必填配置齐全')
  await page.reload()
  await expect(page.getByLabel('US 语速', { exact: true })).toHaveValue('1.1')
})

test('credential edits belong to their section and empty locale speed sends null', async ({ page }) => {
  await admin(page)
  const current = state()
  current.enrichment.tts.uk = { ...current.enrichment.tts.uk, voice: 'existing-uk', speed: 1 }
  const payloads: unknown[] = []
  await page.route(url, route => {
    if (route.request().method() === 'PUT') payloads.push(route.request().postDataJSON())
    return json(route, current)
  })
  await page.goto('/#/settings/review')
  await change(page)
  await save(page).click()
  await expect(notice(page)).toContainText('已保存')
  expect(payloads[0]).toEqual({ reviewAssistant: { model: 'review-draft' } })
  await page.getByRole('combobox', { name: '审核 API Key操作', exact: true }).selectOption('clear')
  await save(page).click()
  await expect(notice(page)).toContainText('已保存')
  expect(payloads[1]).toEqual({ reviewAssistant: { apiKey: { action: 'clear' } } })

  await tab(page, '文本补齐').click()
  await page.getByLabel('补齐文本模型').fill('draft-model')
  await page.getByRole('combobox', { name: '补齐文本 API Key操作', exact: true }).selectOption('replace')
  await expect(save(page, '文本补齐')).toBeDisabled()
  await key(page, '补齐文本 API Key').fill('synthetic-replacement')
  await save(page, '文本补齐').click()
  await expect(notice(page, '文本补齐')).toContainText('已保存')
  expect(payloads[2]).toEqual({ enrichment: { text: { model: 'draft-model', apiKey: { action: 'replace', value: 'synthetic-replacement' } } } })
  await expect(key(page, '补齐文本 API Key')).toHaveValue('')

  await tab(page, '语音生成').click()
  await page.getByLabel('UK 语速', { exact: true }).fill('')
  await save(page, '语音生成').click()
  await expect(notice(page, '语音生成')).toContainText('已保存')
  expect(payloads[3]).toEqual({ enrichment: { tts: { uk: { speed: null } } } })
})

for (const status of [400, 409, 412, 413, 428, 503]) {
  test(`safe ${status} retains nonsecret draft, clears keys; conflicts require explicit reload`, async ({ page }) => {
    await admin(page); let puts = 0
    await page.route(url, route => { if (route.request().method() === 'GET') return json(route, state()); puts++; return json(route, { message: 'synthetic-secret-not-for-display' }, status) })
    await page.goto('/#/settings/review'); await change(page); await key(page).fill('synthetic-secret-not-for-display'); await save(page).click()
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
    await page.goto('/#/settings/review'); await change(page); await key(page).fill('synthetic-no-replay'); await save(page).click()
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
  await page.goto('/#/settings/review'); await change(page); await key(page).fill('synthetic-no-replay'); await save(page).click()
  await expect(notice(page)).toContainText('保存结果未知，回读失败'); await expect(save(page)).toHaveCount(0)
  await reload(page).click(); await expect(key(page)).toHaveValue(''); expect(puts).toBe(1)
  await key(page).fill('synthetic-clear-on-read'); await reload(page).click(); await expect(key(page)).toHaveValue('')
})

test('failed/malformed GET means unknown; source locks show their reason and are omitted from saves', async ({ page }) => {
  await admin(page); let reads = 0
  const value = state(); value.reviewAssistant.model = 'deployed-model'
  value.fields['reviewAssistant.model'] = { source: 'environment', editable: false, reason: 'Overridden by deployment configuration.' }
  value.fields['reviewAssistant.apiKey'] = { source: 'commandLine', editable: false, reason: 'Overridden by deployment configuration.' }
  await page.route(url, route => {
    if (route.request().method() === 'PUT') {
      expect(route.request().postDataJSON()).toEqual({ reviewAssistant: { endpoint: 'https://review.test/v1' } })
      return json(route, value)
    }
    reads++; return json(route, reads === 1 ? {} : reads === 2 ? { revision } : value, reads === 1 ? 503 : 200)
  })
  await page.goto('/#/settings/review'); await expect(notice(page)).toContainText('状态未知')
  await reload(page).click(); await expect(notice(page)).toContainText('状态未知')
  await reload(page).click(); await expect(page.getByLabel('审核模型', { exact: true })).toBeDisabled()
  await expect(key(page)).toBeDisabled(); await expect(page.getByText('网页只读：Overridden by deployment configuration.', { exact: false })).toHaveCount(2)
  await page.getByLabel('审核服务地址').fill('https://review.test/v1')
  await save(page).click()
  await expect(notice(page)).toContainText('已保存')
})

for (const other of ['panel', 'administrator'] as const) {
  test(`${other} save makes shared revision stale; explicit reread uses new ETag and never old key`, async ({ page, browser }) => {
    await admin(page); let currentRevision = revision, puts = 0
    await page.route(url, route => {
      const value = { ...state(), revision: currentRevision }
      if (route.request().method() === 'GET') return json(route, value)
      puts++
      if (route.request().headers()['if-match'] !== currentRevision) return json(route, {}, 412)
      expect(route.request().postDataJSON()).toEqual({ reviewAssistant: { model: 'review-draft' } })
      value.reviewAssistant.model = 'review-draft'; return json(route, value)
    })
    await page.goto('/#/settings/review'); await change(page); await key(page).fill('synthetic-stale-key')
    const second = await browser.newPage()
    await admin(second)
    if (other === 'panel') {
      await second.route('**/admin/system/settings/rate-limits', route => {
        if (route.request().method() === 'PUT') currentRevision = nextRevision
        return json(route, {
          revision: currentRevision,
          adminLogin: { enabled: false, permitLimit: 10, windowSeconds: 300, source: 'default', editable: true },
          publicApi: { enabled: currentRevision === nextRevision, permitLimit: 300, windowSeconds: 60, source: 'file', editable: true }
        })
      })
      await second.goto('/#/settings')
      await second.getByLabel('公共 API草稿开关', { exact: true }).check()
      await second.getByRole('button', { name: '保存限流设置', exact: true }).click()
      await expect(second.getByRole('status')).toContainText('保存后已回读')
    } else {
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
      await second.goto('/#/settings/review'); await change(second, 'other-admin-model'); await save(second).click()
      await expect(notice(second)).toContainText('已保存')
    }
    await second.close()
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
    await page.goto('/#/settings/review'); await change(page); await key(page).fill('synthetic-private-draft'); await save(page).click()
    await expect.poll(() => Boolean(pending)).toBe(true)
    await tab(page, '文本补齐').click()
    await expect(page.getByLabel('补齐文本模型')).toHaveValue('')
    await tab(page, '审核助手').click()
    const late = state(); late.reviewAssistant.model = 'late-model'
    await json(pending!, outcome === 'success' ? late : {}, outcome === 'success' ? 200 : Number(outcome)).catch(() => undefined)
    await expect(page.getByLabel('审核模型', { exact: true })).toHaveValue('')
    await expect(key(page)).toHaveValue(''); await expect(page.locator('.session')).toContainText('ci-admin')
    await expect(page).toHaveURL(/#\/settings\/review$/)
  })
}

for (const leave of ['navigate', 'logout'] as const) {
  test(`${leave} discards unsent password drafts; logout clears before server completes`, async ({ page }) => {
    await admin(page); await page.route(url, route => json(route, state()))
    let pendingLogout: Route | undefined
    if (leave === 'logout') await page.route('**/admin/auth/logout', route => { pendingLogout = route })
    await page.goto('/#/settings/review'); await change(page); await key(page).fill('synthetic-unsent-draft')
    if (leave === 'navigate') {
      const dismissed = page.waitForEvent('dialog')
      page.once('dialog', dialog => dialog.dismiss())
      await tab(page, '文本补齐').click()
      expect((await dismissed).message()).toContain('未保存的修改')
      await expect(page).toHaveURL(/#\/settings\/review$/)
      await expect(page.getByLabel('审核模型', { exact: true })).toHaveValue('review-draft')
      await expect(key(page)).toHaveValue('synthetic-unsent-draft')

      page.once('dialog', dialog => dialog.accept())
      await tab(page, '文本补齐').click()
      await expect(key(page)).toHaveCount(0)
      await page.goBack()
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
    await admin(page); await page.route(url, route => json(route, {}, status)); await page.goto('/#/settings/review')
    await expect(page).toHaveURL(status === 401 ? /#\/login/ : /#\/forbidden/)
  })
}

test('narrow keyboard-accessible panel has no credentials in storage, URL, console or supplier requests', async ({ page }) => {
  await admin(page); await page.route(url, route => json(route, state()))
  const logs: string[] = [], requests: string[] = []
  page.on('console', message => logs.push(message.text())); page.on('request', request => requests.push(request.url()))
  await page.setViewportSize({ width: 375, height: 812 }); await page.goto('/#/settings/text')
  await page.getByLabel('补齐文本模型', { exact: true }).focus(); await page.keyboard.insertText('keyboard-planner')
  await expect(page.getByLabel('补齐文本模型', { exact: true })).toHaveValue('keyboard-planner')
  for (const [title, label] of [['文本补齐', '补齐文本 API Key'], ['审核助手', '审核 API Key'], ['语音生成', '语音 API Key']] as const) {
    if (title !== '文本补齐') {
      page.once('dialog', dialog => dialog.accept())
      await tab(page, title).click()
    }
    await key(page, label).fill('synthetic-memory-only')
    await expect(key(page, label)).toHaveAttribute('type', 'password')
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
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
      value.reviewAssistant.timeoutSeconds = 45
      value.fields['reviewAssistant.model'] = { source: 'environment', editable: false, reason: 'Overridden by deployment configuration.' }
    }
    return json(route, value)
  })
  await page.goto('/#/settings/review'); await change(page); await page.getByLabel('审核服务地址', { exact: true }).fill('https://review.test/v1')
  await save(page).click(); await expect(notice(page)).toContainText('保存失败（409）')
  const dismissed = page.waitForEvent('dialog')
  page.once('dialog', dialog => dialog.dismiss())
  await tab(page, '文本补齐').click()
  expect((await dismissed).message()).toContain('未保存的修改')
  await reload(page).click()
  await expect(page.getByLabel('审核模型', { exact: true })).toBeDisabled()
  await expect(page.getByLabel('审核模型', { exact: true })).toHaveValue('locked-model')
  await expect(page.getByLabel('审核超时（秒）')).toHaveValue('45')
  await expect(page.getByLabel('审核服务地址', { exact: true })).toHaveValue('https://review.test/v1')
})

test('invalid numeric input cannot dispatch; no provider or model change fills voice/speed', async ({ page }) => {
  await admin(page); let puts = 0
  await page.route(url, route => { if (route.request().method() === 'PUT') puts++; return json(route, state()) })
  await page.goto('/#/settings/background'); await page.getByLabel('并发上限', { exact: true }).fill('9')
  await save(page, '后台策略').click(); expect(puts).toBe(0)
  expect(await page.getByLabel('并发上限', { exact: true }).evaluate(node => (node as HTMLInputElement).validity.rangeOverflow)).toBe(true)
  page.once('dialog', dialog => dialog.accept())
  await tab(page, '语音生成').click()
  await page.getByLabel('语音提供方', { exact: true }).selectOption('OpenAICompatible')
  await page.getByLabel('语音模型', { exact: true }).fill('gpt-4o-mini-tts')
  for (const label of ['UK 音色', 'US 音色', 'UK 语速', 'US 语速']) await expect(page.getByLabel(label, { exact: true })).toHaveValue('')
})

for (const outcome of ['success', '401', '403'] as const) {
  test(`logout generation invalidates pending ${outcome} before logout itself finishes`, async ({ page }) => {
    await admin(page); let pending: Route | undefined, pendingLogout: Route | undefined
    await page.route('**/admin/auth/logout', route => { pendingLogout = route })
    await page.route(url, route => route.request().method() === 'GET' ? json(route, state()) : (pending = route, undefined))
    await page.goto('/#/settings/review'); await change(page); await key(page).fill('synthetic-logout-race'); await save(page).click()
    await expect.poll(() => Boolean(pending)).toBe(true)
    await page.getByRole('button', { name: '退出登录' }).click(); await expect.poll(() => Boolean(pendingLogout)).toBe(true)
    await expect(key(page)).toHaveCount(0)
    await json(pending!, outcome === 'success' ? state() : {}, outcome === 'success' ? 200 : Number(outcome)).catch(() => undefined)
    await expect(page).toHaveURL(/#\/settings\/review$/)
    await expect(save(page)).toHaveCount(0)
    await json(pendingLogout!, {}); await expect(page).toHaveURL(/#\/login/)
    await page.goto('/?replacement-session=1#/settings/review')
    await expect(page.getByLabel('审核模型', { exact: true })).toHaveValue('')
    await expect(key(page)).toHaveValue('')
  })
}


test('automatic review risk flag saves independently and reports pause until configuration exists', async ({ page }) => {
  await admin(page)
  let current = state(), puts = 0
  await page.route(url, async route => {
    if (route.request().method() === 'GET') return json(route, current)
    puts++
    expect(route.request().headers()['if-match']).toBe(revision)
    expect(route.request().postDataJSON()).toEqual({ reviewAssistant: { autoReviewEnabled: true } })
    current = { ...current, revision: nextRevision, reviewAssistant: { ...current.reviewAssistant, autoReviewEnabled: true } }
    return json(route, current)
  })
  await page.goto('/#/settings/review')
  await expect(page.getByLabel('自动审核配置状态')).toContainText('自动审核关闭')
  await page.getByLabel('启用 AI 自动审核投稿（默认关闭）', { exact: true }).check()
  await save(page).click()
  await expect(page.getByLabel('自动审核配置状态')).toContainText('配置修复后自动恢复，无需人工处理')
  expect(puts).toBe(1)
  await tab(page, '文本补齐').click()
  await expect(page.getByLabel('启用默认字段补齐', { exact: true })).toHaveCount(0)
})

test('draft previews use independent locale styles and credentials without saving, then save the same styles', async ({ page }) => {
  await admin(page)
  const current = state()
  let puts = 0
  const previews: Array<Record<string, unknown>> = []
  await page.route(url, route => {
    if (route.request().method() === 'GET') return json(route, current)
    puts++
    const patch = route.request().postDataJSON().enrichment.tts
    expect(patch.uk).toEqual({ voice: 'uk-draft', speed: 0.9, emotion: 'calm', pitch: -2, volume: 0.8 })
    expect(patch.us).toEqual({ voice: 'us-draft', speed: 1.1, emotion: 'happy' })
    current.enrichment.tts = { ...current.enrichment.tts, ...patch, hasApiKey: true,
      uk: { ...current.enrichment.tts.uk, ...patch.uk }, us: { ...current.enrichment.tts.us, ...patch.us } }
    current.revision = nextRevision
    return json(route, current)
  })
  await page.route('**/admin/enrichment/settings/speech-preview', async route => {
    expect(route.request().headers()['if-match']).toBe(revision)
    expect(route.request().headers()['x-signacore-csrf']).toBeTruthy()
    previews.push(route.request().postDataJSON())
    await route.fulfill({ contentType: 'audio/mpeg', path: '../tests/AudioStorageProbe/Fixtures/tone.mp3' })
  })
  await page.goto('/#/settings/speech')
  await page.getByLabel('语音服务地址', { exact: true }).fill('https://api.minimax.io/v1')
  await key(page, '语音 API Key').fill('synthetic-draft-key')
  await page.getByLabel('UK 音色', { exact: true }).fill('uk-draft')
  await page.getByLabel('UK 语速', { exact: true }).fill('0.9')
  await page.getByLabel('UK 情绪', { exact: true }).selectOption('calm')
  await page.getByLabel('UK 音高', { exact: true }).fill('-2')
  await page.getByLabel('UK 音量', { exact: true }).fill('0.8')
  await page.getByLabel('US 音色', { exact: true }).fill('us-draft')
  await page.getByLabel('US 语速', { exact: true }).fill('1.1')
  await page.getByLabel('US 情绪', { exact: true }).selectOption('happy')
  await page.getByRole('button', { name: '生成 UK 试听', exact: true }).click()
  await expect(page.getByLabel('UK 试听播放器')).toHaveAttribute('src', /^blob:/)
  await page.getByLabel('US 试听样本').selectOption('sentence')
  await page.getByRole('button', { name: '生成 US 试听', exact: true }).click()
  await expect(page.getByLabel('US 试听播放器')).toHaveAttribute('src', /^blob:/)
  expect(puts).toBe(0)
  expect(previews).toEqual([
    { locale: 'uk', sample: 'word', tts: { endpoint: 'https://api.minimax.io/v1', apiKey: { action: 'replace', value: 'synthetic-draft-key' }, uk: { voice: 'uk-draft', speed: 0.9, emotion: 'calm', pitch: -2, volume: 0.8 } } },
    { locale: 'us', sample: 'sentence', tts: { endpoint: 'https://api.minimax.io/v1', apiKey: { action: 'replace', value: 'synthetic-draft-key' }, us: { voice: 'us-draft', speed: 1.1, emotion: 'happy' } } }
  ])
  await expect(key(page, '语音 API Key')).toHaveValue('synthetic-draft-key')
  await save(page, '语音生成').click()
  await expect(notice(page, '语音生成')).toContainText('已保存')
  expect(puts).toBe(1)
  await expect(key(page, '语音 API Key')).toHaveValue('')
  await expect(page.getByText('已有音频不会自动重新生成', { exact: false })).toBeVisible()
})

test('provider style controls preserve independent drafts and unsupported-model explanation', async ({ page }) => {
  await admin(page)
  await page.route(url, route => json(route, state()))
  const previews: unknown[] = []
  await page.route('**/admin/enrichment/settings/speech-preview', route => {
    previews.push(route.request().postDataJSON())
    return route.fulfill({ contentType: 'audio/mpeg', path: '../tests/AudioStorageProbe/Fixtures/tone.mp3' })
  })
  await page.goto('/#/settings/speech')
  await page.getByLabel('UK 情绪', { exact: true }).selectOption('calm')
  await page.getByLabel('语音提供方', { exact: true }).selectOption('OpenAICompatible')
  await page.getByLabel('语音模型', { exact: true }).fill('gpt-4o-mini-tts')
  await expect(page.getByLabel('UK 情绪', { exact: true })).toHaveCount(0)
  await page.getByLabel('UK 风格描述', { exact: true }).fill('清晰、平静，适合教学。')
  await page.getByLabel('US 风格描述', { exact: true }).fill('自然、活泼。')
  await page.getByRole('button', { name: '生成 UK 试听', exact: true }).click()
  await expect(page.getByLabel('UK 试听播放器')).toBeVisible()
  expect(previews[0]).toMatchObject({ locale: 'uk', tts: { uk: { style: '清晰、平静，适合教学。' } } })
  await page.getByLabel('UK 风格描述', { exact: true }).fill('温和。')
  await expect(page.getByText('当前试听对应旧参数', { exact: false })).toBeVisible()
  await page.getByLabel('语音模型', { exact: true }).fill('tts-1')
  await expect(page.getByText('当前模型不支持风格描述', { exact: false })).toBeVisible()
  await page.getByLabel('语音提供方', { exact: true }).selectOption('MiniMaxNative')
  await expect(page.getByLabel('UK 情绪', { exact: true })).toHaveValue('calm')
  await page.getByLabel('语音提供方', { exact: true }).selectOption('OpenAICompatible')
  await expect(page.getByLabel('UK 风格描述', { exact: true })).toHaveValue('温和。')
  await expect(page.getByLabel('US 风格描述', { exact: true })).toHaveValue('自然、活泼。')
})

test('preview cancellation and changed drafts cannot publish a late result; leaving releases audio', async ({ page }) => {
  await admin(page)
  await page.addInitScript(() => {
    const original = URL.revokeObjectURL
    Object.assign(window, { revokedAudio: [] as string[] })
    URL.revokeObjectURL = value => { (window as unknown as { revokedAudio: string[] }).revokedAudio.push(value); original.call(URL, value) }
  })
  await page.route(url, route => json(route, state()))
  let pending: Route | undefined
  await page.route('**/admin/enrichment/settings/speech-preview', route => { pending = route })
  await page.goto('/#/settings/speech')
  await page.getByRole('button', { name: '生成 UK 试听', exact: true }).click()
  await expect.poll(() => Boolean(pending)).toBe(true)
  await page.getByLabel('UK 情绪', { exact: true }).selectOption('calm')
  await expect(page.getByRole('status', { name: 'UK 试听状态' })).toContainText('参数已变化')
  await pending!.fulfill({ contentType: 'audio/mpeg', path: '../tests/AudioStorageProbe/Fixtures/tone.mp3' }).catch(() => undefined)
  await expect(page.getByLabel('UK 试听播放器')).toHaveCount(0)
  pending = undefined
  await page.getByRole('button', { name: '生成 UK 试听', exact: true }).click()
  await expect.poll(() => Boolean(pending)).toBe(true)
  await page.getByRole('button', { name: '取消 UK 试听', exact: true }).click()
  await expect(page.getByRole('status', { name: 'UK 试听状态' })).toContainText('已取消等待')
  await pending!.fulfill({ contentType: 'audio/mpeg', path: '../tests/AudioStorageProbe/Fixtures/tone.mp3' }).catch(() => undefined)
  await page.unroute('**/admin/enrichment/settings/speech-preview')
  await page.route('**/admin/enrichment/settings/speech-preview', route => route.fulfill({ contentType: 'audio/mpeg', path: '../tests/AudioStorageProbe/Fixtures/tone.mp3' }))
  await page.getByRole('button', { name: '生成 UK 试听', exact: true }).click()
  const player = page.getByLabel('UK 试听播放器')
  await expect(player).toBeVisible()
  const objectUrl = await player.getAttribute('src')
  page.once('dialog', dialog => dialog.accept())
  await tab(page, '文本补齐').click()
  expect(await page.evaluate(() => (window as unknown as { revokedAudio: string[] }).revokedAudio)).toContain(objectUrl)
})

for (const status of [400, 412, 429, 502]) {
  test(`preview ${status} is safe, retains drafts and never retries`, async ({ page }) => {
    await admin(page)
    await page.route(url, route => json(route, state()))
    let calls = 0
    await page.route('**/admin/enrichment/settings/speech-preview', route => { calls++; return json(route, { message: 'synthetic-secret-error' }, status) })
    await page.goto('/#/settings/speech')
    await page.getByLabel('UK 情绪', { exact: true }).selectOption('happy')
    await page.getByRole('button', { name: '生成 UK 试听', exact: true }).click()
    await expect(page.getByRole('status', { name: 'UK 试听状态' })).not.toContainText('正在使用草稿')
    await expect(page.getByLabel('UK 情绪', { exact: true })).toHaveValue('happy')
    await expect(page.locator('body')).not.toContainText('synthetic-secret-error')
    await expect(page.getByLabel('UK 试听播放器')).toHaveCount(0)
    expect(calls).toBe(1)
  })
}

test('speech previews decode in Chromium, work on narrow screens, and ignore logout responses', async ({ page }) => {
  await admin(page)
  await page.setViewportSize({ width: 375, height: 812 })
  await page.route(url, route => json(route, state()))
  await page.route('**/admin/enrichment/settings/speech-preview', route => route.fulfill({ contentType: 'audio/mpeg', path: '../tests/AudioStorageProbe/Fixtures/tone.mp3' }))
  await page.goto('/#/settings/speech')
  await page.getByRole('button', { name: '生成 UK 试听', exact: true }).click()
  const player = page.getByLabel('UK 试听播放器')
  await expect(player).toBeVisible()
  await player.evaluate(async node => { await (node as HTMLAudioElement).play() })
  await expect.poll(() => player.evaluate(node => (node as HTMLAudioElement).currentTime)).toBeGreaterThan(0)
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
  const accessibility = await new AxeBuilder({ page }).include('.enrichment-settings').withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze()
  expect(accessibility.violations).toEqual([])
  let pending: Route | undefined
  await page.unroute('**/admin/enrichment/settings/speech-preview')
  await page.route('**/admin/enrichment/settings/speech-preview', route => { pending = route })
  await page.getByRole('button', { name: '生成 US 试听', exact: true }).click()
  await expect.poll(() => Boolean(pending)).toBe(true)
  await page.getByRole('button', { name: '退出登录', exact: true }).click()
  await expect(page).toHaveURL(/#\/login/)
  await pending!.fulfill({ contentType: 'audio/mpeg', path: '../tests/AudioStorageProbe/Fixtures/tone.mp3' }).catch(() => undefined)
  await expect(page.getByLabel('US 试听播放器')).toHaveCount(0)
  await expect(page).toHaveURL(/#\/login/)
})
