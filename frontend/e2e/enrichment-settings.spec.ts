import { expect, test, type Page, type Route } from '@playwright/test'
import AxeBuilder from '@axe-core/playwright'
import {
  applied, audioUrl, catalogFixture, catalogUrl, category, clear, control, entry, enrichmentUrl, json, mockSettings, modelOf,
  nextRevision, notice, overridden, previewUrl, rateLimitUrl, rateLimitsOf, replace, revision, reveal, row, reread, save,
  secretsEverywhere, startupUrl, switchOf, unknownState
} from './support/settingsCatalog'
import type { Catalog } from './support/settingsCatalog'

const secretInputs = (page: Page) => page.locator('input[type="password"]')
// The review assistant shares the reasoning model's connection, which has its own category.
const model = (page: Page) => control(page, '推理模型')
const modelKey = (page: Page) => control(page, '推理 API Key')
const modelAddress = (page: Page) => control(page, '推理服务地址')
const reviewStatus = (page: Page) => page.getByLabel('自动审核配置状态', { exact: true })
const tone = '../tests/AudioStorageProbe/Fixtures/tone.mp3'

// What a committed save leaves behind: the value in effect, read from the file.
function stored(catalog: Catalog, key: string, value: unknown) {
  const field = entry(catalog, key)
  if (field.kind === 'secret') field.hasValue = value !== ''
  else field.value = value
  field.source = 'file'
}
async function typeKey(page: Page, label: string, value: string) {
  await (replace(page, label).or(reveal(page, label))).click()
  await control(page, label).fill(value)
}
// Leaving a category with a draft asks first; this answers.
function answerPrompt(page: Page, accept: boolean) {
  page.once('dialog', dialog => accept ? dialog.accept() : dialog.dismiss())
}

test('each category shows its own fields, their bounds and the model it shares', async ({ page }) => {
  await mockSettings(page, catalogFixture())
  await page.goto('/#/settings/review')
  await expect(switchOf(page, '启用 AI 自动审核投稿')).not.toBeChecked()
  await expect(page.getByText('管理员承担此风险', { exact: false })).toBeVisible()
  await expect(reviewStatus(page)).toContainText('自动审核关闭')
  // The advice comes from the reasoning model, which is configured in its own category.
  await expect(model(page)).toHaveCount(0)
  await expect(modelKey(page)).toHaveCount(0)

  await category(page, '视觉推理').click()
  await expect(control(page, '推理请求超时')).toHaveAttribute('max', '300')
  await expect(model(page)).toHaveValue('')
  await expect(modelAddress(page)).toHaveValue('')
  await expect(row(page, 'Models:Reasoning:Endpoint')).toContainText('Models:Reasoning:Endpoint')
  // The work these models start can be followed from the category that starts it.
  await expect(page.getByRole('link', { name: '查看自治进度、服务等待与异常恢复' })).toHaveAttribute('href', '#/enrichment')

  await category(page, '语音生成').click()
  await expect(control(page, '语音模型')).toHaveValue('speech-2.8-hd')
  for (const label of ['英式音色', '美式音色', '英式语速', '美式语速']) await expect(control(page, label)).toHaveValue('')
  for (const label of ['英式语速', '美式语速']) {
    await expect(control(page, label)).toHaveAttribute('min', '0.1')
    await expect(control(page, label)).toHaveAttribute('max', '4')
  }
  await expect(control(page, '语音请求超时')).toHaveAttribute('max', '300')
  // With no address the server reads the MiniMax way, which takes emotion, pitch and volume;
  // a style description belongs to the other protocol.
  await expect(control(page, '英式情绪')).toBeVisible()
  await expect(control(page, '英式风格描述')).toHaveCount(0)
  // No protocol is chosen here: the server names it from the address.
  await expect(control(page, '语音提供方')).toHaveCount(0)
  const matrix = page.getByRole('link', { name: '查看英文语音能力矩阵' })
  await expect(matrix).toBeHidden()
  await page.getByText('语音能力限制', { exact: true }).click()
  await expect(matrix).toBeVisible()
  await expect(matrix).toHaveAttribute('href', /Audio.md#speech-adapters$/)
  const limits = page.locator('.lx-details')
  await expect(limits).toContainText('完整朗读目标的 IPA')
  await expect(limits).toContainText('0.5–2')
  await expect(limits).toContainText('0.25–4')

  await category(page, '后台策略').click()
  for (const [label, min, max] of [
    ['文本请求并发', '1', '2'], ['任务执行并发', '1', '8'], ['每步失败额度', '1', '20'], ['服务暂停阈值', '1', '20'],
    ['初始暂停时间', '1', '600'], ['最长暂停时间', '1', '3600'], ['初始退避时间', '1', '120'],
    ['最长退避时间', '1', '3600'], ['发现批大小', '1', '1000'], ['扫描间隔', '1', '3600']
  ] as const) {
    await expect(control(page, label)).toHaveAttribute('min', min)
    await expect(control(page, label)).toHaveAttribute('max', max)
  }
  await expect(control(page, '语音模型')).toHaveCount(0)
})

test('the reasoning model and US-only speech save separately with the current revision', async ({ page }) => {
  const catalog = catalogFixture()
  await mockSettings(page, () => catalog)
  let puts = 0
  await page.route(catalogUrl, route => { if (route.request().method() === 'GET') return json(route, catalog) })
  await page.route(enrichmentUrl, async route => {
    if (route.request().method() === 'GET') return json(route, modelOf(catalog))
    puts++
    expect(route.request().headers()['x-signacore-csrf']).toBeTruthy()
    if (puts === 1) {
      expect(route.request().headers()['if-match']).toBe(revision)
      expect(route.request().postDataJSON()).toEqual({ enrichment: {
        text: { endpoint: 'https://planner.test/v1', model: 'plan-model', apiKey: { action: 'replace', value: 'synthetic-plan-key' } }
      } })
      stored(catalog, 'Models:Reasoning:Endpoint', 'https://planner.test/v1')
      stored(catalog, 'Models:Reasoning:Model', 'plan-model')
      stored(catalog, 'Models:Reasoning:ApiKey', 'synthetic-plan-key')
    } else {
      expect(route.request().headers()['if-match']).toBe(revision)
      expect(route.request().postDataJSON()).toEqual({ enrichment: { tts: {
        endpoint: 'https://api.minimax.io/v1', us: { voice: 'configured-us-voice', speed: 1.1 },
        apiKey: { action: 'replace', value: 'synthetic-speech-key' }
      } } })
      stored(catalog, 'Models:Speech:Endpoint', 'https://api.minimax.io/v1')
      stored(catalog, 'Models:Speech:ApiKey', 'synthetic-speech-key')
      stored(catalog, 'Models:Speech:Us:Voice', 'configured-us-voice')
      stored(catalog, 'Models:Speech:Us:Speed', 1.1)
    }
    catalog.facts.find(fact => fact.id === 'enrichmentConfigurationReady')!.value = 'ready'
    return json(route, modelOf(catalog))
  })
  await page.goto('/#/settings/text')
  await modelAddress(page).fill('https://planner.test/v1')
  await model(page).fill('plan-model')
  await typeKey(page, '推理 API Key', 'synthetic-plan-key')
  await save(page).click()
  await expect(notice(page)).toContainText('已保存')

  await category(page, '语音生成').click()
  await control(page, '语音服务地址').fill('https://api.minimax.io/v1')
  await control(page, '美式音色').fill('configured-us-voice')
  await control(page, '美式语速').fill('1.1')
  await typeKey(page, '语音 API Key', 'synthetic-speech-key')
  await save(page).click()
  await expect(save(page)).toHaveCount(0)
  await expect(control(page, '语音 API Key')).toHaveCount(0)
  await expect(notice(page)).toContainText('已保存')
  expect(puts).toBe(2)
  await expect(page.getByLabel('UK 配置状态', { exact: true })).toContainText('等待：语音服务或英式音色、语速缺失')
  await expect(page.getByLabel('US 配置状态', { exact: true })).toContainText('本地必填配置齐全')
  await page.reload()
  await expect(control(page, '美式语速')).toHaveValue('1.1')
})

test('credential edits belong to their category, and an emptied locale speed is sent as null', async ({ page }) => {
  const catalog = catalogFixture()
  stored(catalog, 'Models:Reasoning:ApiKey', 'existing-reasoning-key')
  stored(catalog, 'Models:Speech:Uk:Speed', 1)
  await mockSettings(page, () => catalog)
  const payloads: unknown[] = []
  await page.route(enrichmentUrl, route => {
    const body = route.request().postDataJSON() as Record<string, unknown>
    payloads.push(body)
    // A committed value is what the next read answers.
    applied(catalog, body)
    return json(route, modelOf(catalog))
  })
  await page.goto('/#/settings/text')
  await model(page).fill('reasoning-draft')
  await save(page).click()
  await expect(notice(page)).toContainText('已保存')
  expect(payloads[0]).toEqual({ enrichment: { text: { model: 'reasoning-draft' } } })
  await clear(page, '推理 API Key').click()
  await save(page).click()
  await expect(notice(page)).toContainText('已保存')
  expect(payloads[1]).toEqual({ enrichment: { text: { apiKey: { action: 'clear' } } } })

  answerPrompt(page, false)
  await category(page, '语音生成').click()
  await control(page, '语音模型').fill('draft-speech-model')
  await typeKey(page, '语音 API Key', 'synthetic-replacement')
  await save(page).click()
  await expect(notice(page)).toContainText('已保存')
  expect(payloads[2]).toEqual({ enrichment: { tts: { model: 'draft-speech-model', apiKey: { action: 'replace', value: 'synthetic-replacement' } } } })
  await expect(control(page, '语音 API Key')).toHaveCount(0)

  await control(page, '英式语速').fill('')
  await save(page).click()
  await expect(notice(page)).toContainText('已保存')
  expect(payloads[3]).toEqual({ enrichment: { tts: { uk: { speed: null } } } })
})

for (const status of [400, 409, 412, 413, 428, 503]) {
  test(`HTTP ${status} keeps the non-secret draft, clears keys, and a conflict needs an explicit reload`, async ({ page }) => {
    await mockSettings(page, catalogFixture())
    let puts = 0
    await page.route(enrichmentUrl, route => {
      if (route.request().method() === 'GET') return json(route, modelOf(catalogFixture()))
      puts++
      return json(route, { message: 'synthetic-secret-not-for-display' }, status)
    })
    await page.goto('/#/settings/text')
    await model(page).fill('reasoning-draft')
    await reveal(page, '推理 API Key').click()
    await modelKey(page).fill('synthetic-secret-not-for-display')
    await save(page).click()
    await expect(notice(page)).toContainText(`保存失败（${status}）`)
    await expect(notice(page)).toContainText('密钥输入已清空')
    await expect(page.locator('body')).not.toContainText('synthetic-secret-not-for-display')
    if ([409, 412, 428].includes(status)) {
      await expect(unknownState(page)).toBeVisible()
      await reread(page).click()
    }
    await expect(model(page)).toHaveValue('reasoning-draft')
    await expect(secretInputs(page)).toHaveCount(0)
    expect(puts).toBe(1)
  })
}

for (const failure of ['lost', 'malformed'] as const) {
  test(`a ${failure} PUT response is recovered by a read only, and the non-secret draft stays`, async ({ page }) => {
    const catalog = catalogFixture()
    await mockSettings(page, () => catalog)
    let reads = 0
    let puts = 0
    await page.route(catalogUrl, route => { reads++; return json(route, catalog) })
    await page.route(enrichmentUrl, route => {
      if (route.request().method() === 'GET') return json(route, modelOf(catalog))
      puts++
      if (failure === 'lost') {
        stored(catalog, 'Models:Reasoning:Model', 'server-model')
        return route.abort('failed')
      }
      // A 2xx without the revision the next request needs: no answer the page can use.
      return json(route, {})
    })
    await page.goto('/#/settings/text')
    await model(page).fill('reasoning-draft')
    await reveal(page, '推理 API Key').click()
    await modelKey(page).fill('synthetic-no-replay')
    await save(page).click()
    await expect(notice(page)).toContainText('保存响应未确认')
    expect(puts).toBe(1)
    expect(reads).toBe(2)
    // The server never stored the draft: it is still the administrator's to review and save.
    await expect(model(page)).toHaveValue('reasoning-draft')
    await expect(save(page)).toBeEnabled()
    await expect(secretInputs(page)).toHaveCount(0)
  })
}

test('a lost save and a failed read stay unknown; a manual read clears secrets and never replays the PUT', async ({ page }) => {
  await mockSettings(page, catalogFixture())
  let reads = 0
  let puts = 0
  await page.route(catalogUrl, route => {
    reads++
    return reads === 2 ? json(route, {}, 503) : json(route, catalogFixture())
  })
  await page.route(enrichmentUrl, route => {
    if (route.request().method() === 'GET') return json(route, modelOf(catalogFixture()))
    puts++
    return route.abort('failed')
  })
  await page.goto('/#/settings/text')
  await model(page).fill('reasoning-draft')
  await reveal(page, '推理 API Key').click()
  await modelKey(page).fill('synthetic-no-replay')
  await save(page).click()
  await expect(notice(page)).toContainText('保存结果未知，回读失败')
  await expect(save(page)).toHaveCount(0)
  await reread(page).click()
  await expect(secretInputs(page)).toHaveCount(0)
  expect(puts).toBe(1)
  await reveal(page, '推理 API Key').click()
  await modelKey(page).fill('synthetic-clear-on-read')
  await reread(page).click()
  await expect(secretInputs(page)).toHaveCount(0)
})

test('failed and malformed reads stay unknown; deployment-owned fields show why and stay out of saves', async ({ page }) => {
  await mockSettings(page, catalogFixture())
  let reads = 0
  let puts = 0
  await page.route(catalogUrl, route => {
    reads++
    if (reads === 1) return json(route, {}, 503)
    if (reads === 2) return json(route, { revision })
    const catalog = catalogFixture()
    entry(catalog, 'Models:Reasoning:Model').value = 'deployed-model'
    Object.assign(entry(catalog, 'Models:Reasoning:Model'), { editable: false, source: 'environment', reason: overridden })
    Object.assign(entry(catalog, 'Models:Reasoning:ApiKey'), { editable: false, source: 'commandLine', reason: overridden })
    return json(route, catalog)
  })
  await page.route(enrichmentUrl, route => {
    if (route.request().method() === 'GET') return json(route, modelOf(catalogFixture()))
    puts++
    expect(route.request().postDataJSON()).toEqual({ enrichment: { text: { endpoint: 'https://reasoning.test/v1' } } })
    return json(route, modelOf(catalogFixture()))
  })
  await page.goto('/#/settings/text')
  await expect(notice(page)).toContainText('状态未知')
  await reread(page).click()
  await expect(notice(page)).toContainText('状态未知')
  await reread(page).click()
  await expect(model(page)).toHaveCount(0)
  await expect(row(page, 'Models:Reasoning:Model')).toContainText('deployed-model')
  await expect(row(page, 'Models:Reasoning:Model')).toContainText('来源：环境变量 · 网页只读')
  await expect(replace(page, '推理 API Key')).toHaveCount(0)
  await expect(row(page, 'Models:Reasoning:ApiKey')).toContainText('来源：启动参数 · 网页只读')
  await modelAddress(page).fill('https://reasoning.test/v1')
  await save(page).click()
  await expect(notice(page)).toContainText('已保存')
  expect(puts).toBe(1)
})

for (const other of ['category', 'administrator'] as const) {
  test(`a save elsewhere (${other}) makes the shared revision stale; an explicit reread saves again without the old key`, async ({ page, browser }) => {
    let current = revision
    const catalogOf = (): Catalog => ({ ...catalogFixture(), revision: current })
    await mockSettings(page, () => catalogOf())
    let puts = 0
    await page.route(enrichmentUrl, route => {
      if (route.request().method() === 'GET') return json(route, modelOf(catalogOf()))
      puts++
      if (route.request().headers()['if-match'] !== current) return json(route, {}, 412)
      expect(route.request().postDataJSON()).toEqual({ enrichment: { text: { model: 'reasoning-draft' } } })
      return json(route, modelOf(catalogOf(), current))
    })
    await page.goto('/#/settings/text')
    await model(page).fill('reasoning-draft')
    await reveal(page, '推理 API Key').click()
    await modelKey(page).fill('synthetic-stale-key')
    const second = await browser.newPage()
    await mockSettings(second, () => catalogOf(), 'second-admin')
    if (other === 'category') {
      await second.route(rateLimitUrl, route => {
        if (route.request().method() === 'PUT') current = nextRevision
        return json(route, rateLimitsOf(catalogOf(), current))
      })
      await second.goto('/#/settings/rate-limits')
      await switchOf(second, '启用登录限流').check()
      await save(second).click()
      await expect(notice(second)).toContainText('已保存到配置文件并立即生效')
    } else {
      await second.route(enrichmentUrl, route => {
        if (route.request().method() === 'GET') return json(route, modelOf(catalogOf()))
        expect(route.request().headers()['if-match']).toBe(revision)
        current = nextRevision
        return json(route, modelOf(catalogOf(), current))
      })
      await second.goto('/#/settings/text')
      await control(second, '推理模型').fill('other-admin-model')
      await save(second).click()
      await expect(notice(second)).toContainText('已保存')
    }
    await second.close()
    await save(page).click()
    await expect(notice(page)).toContainText('保存失败（412）')
    expect(puts).toBe(1)
    await reread(page).click()
    await expect(model(page)).toHaveValue('reasoning-draft')
    await expect(secretInputs(page)).toHaveCount(0)
    expect(puts).toBe(1)
    await save(page).click()
    await expect(notice(page)).toContainText('已保存')
    expect(puts).toBe(2)
  })
}

for (const outcome of ['success', '401', '403'] as const) {
  test(`a late ${outcome} PUT cannot populate or redirect a category opened after navigation`, async ({ page }) => {
    await mockSettings(page, catalogFixture())
    let pending: Route | undefined
    await page.route(enrichmentUrl, route => { pending = route })
    await page.goto('/#/settings/text')
    await model(page).fill('reasoning-draft')
    await reveal(page, '推理 API Key').click()
    await modelKey(page).fill('synthetic-private-draft')
    await save(page).click()
    await expect.poll(() => Boolean(pending)).toBe(true)
    await category(page, '审核助手').click()
    await expect(switchOf(page, '启用 AI 自动审核投稿')).not.toBeChecked()
    await category(page, '视觉推理').click()
    await json(pending!, outcome === 'success' ? modelOf(catalogFixture()) : {}, outcome === 'success' ? 200 : Number(outcome)).catch(() => undefined)
    await expect(model(page)).toHaveValue('')
    await expect(secretInputs(page)).toHaveCount(0)
    await expect(page.locator('.session')).toContainText('ci-admin')
    await expect(page).toHaveURL(/#\/settings\/text$/)
  })
}

for (const leave of ['navigate', 'logout'] as const) {
  test(`${leave} discards unsent password drafts; signing out clears them before the server answers`, async ({ page }) => {
    await mockSettings(page, catalogFixture())
    let pendingLogout: Route | undefined
    if (leave === 'logout') await page.route('**/admin/auth/logout', route => { pendingLogout = route })
    await page.goto('/#/settings/text')
    await model(page).fill('reasoning-draft')
    await reveal(page, '推理 API Key').click()
    await modelKey(page).fill('synthetic-unsent-draft')
    if (leave === 'navigate') {
      const dismissed = page.waitForEvent('dialog')
      answerPrompt(page, false)
      await category(page, '审核助手').click()
      expect((await dismissed).message()).toContain('未保存的修改')
      await expect(page).toHaveURL(/#\/settings\/text$/)
      await expect(model(page)).toHaveValue('reasoning-draft')
      await expect(modelKey(page)).toHaveValue('synthetic-unsent-draft')

      const accepted = page.waitForEvent('dialog')
      answerPrompt(page, true)
      await category(page, '审核助手').click()
      await accepted
      await expect(secretInputs(page)).toHaveCount(0)
      await page.goBack()
      await expect(secretInputs(page)).toHaveCount(0)
      await expect(model(page)).toHaveValue('')
    } else {
      await page.getByRole('button', { name: '退出登录' }).click()
      await expect.poll(() => Boolean(pendingLogout)).toBe(true)
      await expect(secretInputs(page)).toHaveCount(0)
      await json(pendingLogout!, {})
      await expect(page).toHaveURL(/#\/login/)
    }
  })
}

for (const status of [401, 403]) {
  test(`HTTP ${status} keeps the shared authentication navigation`, async ({ page }) => {
    await mockSettings(page, catalogFixture())
    await page.route(catalogUrl, route => json(route, {}, status))
    await page.goto('/#/settings/review')
    await expect(page).toHaveURL(status === 401 ? /#\/login/ : /#\/forbidden/)
  })
}

test('a phone-sized category is keyboard-operable, and a credential reaches no storage, URL, log or supplier', async ({ page }) => {
  await mockSettings(page, catalogFixture())
  const logs: string[] = []
  const requests: string[] = []
  page.on('console', message => logs.push(message.text()))
  page.on('request', request => requests.push(request.url()))
  await page.setViewportSize({ width: 375, height: 812 })
  await page.goto('/#/settings/text')
  await model(page).focus()
  await page.keyboard.insertText('keyboard-reasoning')
  await expect(model(page)).toHaveValue('keyboard-reasoning')
  for (const [title, label] of [['视觉推理', '推理 API Key'], ['语音生成', '语音 API Key']] as const) {
    if (title !== '视觉推理') {
      answerPrompt(page, true)
      await category(page, title).click()
    }
    await reveal(page, label).click()
    await control(page, label).fill('synthetic-memory-only')
    await expect(control(page, label)).toHaveAttribute('type', 'password')
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  }
  const everywhere = await secretsEverywhere(page)
  expect(everywhere.local).toEqual({})
  expect(everywhere.session).toEqual({})
  expect(everywhere.url + logs.join()).not.toContain('synthetic-memory-only')
  expect(requests.every(value => new URL(value).hostname === '127.0.0.1')).toBe(true)
  const results = await new AxeBuilder({ page }).include('.lx-page').withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze()
  expect(results.violations).toEqual([])
})

test('a deployment lock that appeared after a conflict is adopted, and the other drafts survive', async ({ page }) => {
  await mockSettings(page, catalogFixture())
  let reads = 0
  await page.route(catalogUrl, route => {
    reads++
    const catalog = catalogFixture()
    if (reads > 1) {
      entry(catalog, 'Models:Reasoning:Model').value = 'locked-model'
      entry(catalog, 'Models:Reasoning:TimeoutSeconds').value = 45
      Object.assign(entry(catalog, 'Models:Reasoning:Model'), { editable: false, source: 'environment', reason: overridden })
    }
    return json(route, catalog)
  })
  await page.route(enrichmentUrl, route => route.request().method() === 'PUT'
    ? json(route, {}, 409)
    : json(route, modelOf(catalogFixture())))
  await page.goto('/#/settings/text')
  await model(page).fill('reasoning-draft')
  await modelAddress(page).fill('https://reasoning.test/v1')
  await save(page).click()
  await expect(notice(page)).toContainText('保存失败（409）')
  const dismissed = page.waitForEvent('dialog')
  answerPrompt(page, false)
  await category(page, '审核助手').click()
  expect((await dismissed).message()).toContain('未保存的修改')
  await reread(page).click()
  await expect(model(page)).toHaveCount(0)
  await expect(row(page, 'Models:Reasoning:Model')).toContainText('locked-model')
  await expect(control(page, '推理请求超时')).toHaveValue('45')
  await expect(modelAddress(page)).toHaveValue('https://reasoning.test/v1')
})

test('an out-of-range number never leaves the browser; the speech model never fills a voice or speed', async ({ page }) => {
  await mockSettings(page, catalogFixture())
  let puts = 0
  await page.route(enrichmentUrl, route => {
    if (route.request().method() === 'PUT') puts++
    return json(route, modelOf(catalogFixture()))
  })
  await page.goto('/#/settings/background')
  await control(page, '任务执行并发').fill('9')
  await expect(row(page, 'Enrichment:Concurrency').getByRole('alert')).toHaveText('取值范围为 1 到 8。')
  await save(page).click({ timeout: 1000 }).catch(() => undefined)
  expect(puts).toBe(0)
  answerPrompt(page, true)
  await category(page, '语音生成').click()
  await control(page, '语音服务地址').fill('https://api.openai.com/v1')
  await control(page, '语音模型').fill('gpt-4o-mini-tts')
  for (const label of ['英式音色', '美式音色', '英式语速', '美式语速']) await expect(control(page, label)).toHaveValue('')
})

for (const outcome of ['success', '401', '403'] as const) {
  test(`signing out invalidates a pending ${outcome} before the sign-out itself finishes`, async ({ page }) => {
    await mockSettings(page, catalogFixture())
    let pending: Route | undefined
    let pendingLogout: Route | undefined
    await page.route('**/admin/auth/logout', route => { pendingLogout = route })
    await page.route(enrichmentUrl, route => { pending = route })
    await page.goto('/#/settings/text')
    await model(page).fill('reasoning-draft')
    await reveal(page, '推理 API Key').click()
    await modelKey(page).fill('synthetic-logout-race')
    await save(page).click()
    await expect.poll(() => Boolean(pending)).toBe(true)
    await page.getByRole('button', { name: '退出登录' }).click()
    await expect.poll(() => Boolean(pendingLogout)).toBe(true)
    await expect(secretInputs(page)).toHaveCount(0)
    await json(pending!, outcome === 'success' ? modelOf(catalogFixture()) : {}, outcome === 'success' ? 200 : Number(outcome)).catch(() => undefined)
    await expect(page).toHaveURL(/#\/settings\/text$/)
    await expect(save(page)).toHaveCount(0)
    await json(pendingLogout!, {})
    await expect(page).toHaveURL(/#\/login/)
    await page.goto('/?replacement-session=1#/settings/text')
    await expect(model(page)).toHaveValue('')
    await expect(secretInputs(page)).toHaveCount(0)
  })
}

test('the automatic-review switch saves on its own and reports why automatic review is paused', async ({ page }) => {
  const catalog = catalogFixture()
  await mockSettings(page, () => catalog)
  let puts = 0
  await page.route(enrichmentUrl, async route => {
    puts++
    expect(route.request().headers()['if-match']).toBe(revision)
    expect(route.request().postDataJSON()).toEqual({ reviewAssistant: { autoReviewEnabled: true } })
    entry(catalog, 'MeaningSubmissions:Ai:AutoReviewEnabled').value = true
    return json(route, modelOf(catalog))
  })
  await page.goto('/#/settings/review')
  await expect(reviewStatus(page)).toContainText('自动审核关闭')
  await switchOf(page, '启用 AI 自动审核投稿').check()
  await save(page).click()
  // On, with no reasoning model behind it yet: paused until the connection is configured.
  await expect(reviewStatus(page)).toContainText('推理模型的服务地址或模型缺失')
  await expect(reviewStatus(page)).toContainText('配置修复后自动恢复，无需人工处理')
  expect(puts).toBe(1)
})

test('draft previews use each locale\'s own draft and the credential without saving, then save the same styles', async ({ page }) => {
  const catalog = catalogFixture()
  await mockSettings(page, () => catalog)
  let puts = 0
  const previews: Array<Record<string, unknown>> = []
  await page.route(enrichmentUrl, route => {
    if (route.request().method() === 'GET') return json(route, modelOf(catalog))
    const patch = route.request().postDataJSON().enrichment.tts
    expect(patch.uk).toEqual({ voice: 'uk-draft', speed: 0.9, emotion: 'calm', pitch: -2, volume: 0.8 })
    expect(patch.us).toEqual({ voice: 'us-draft', speed: 1.1, emotion: 'happy' })
    stored(catalog, 'Models:Speech:Endpoint', patch.endpoint)
    stored(catalog, 'Models:Speech:ApiKey', patch.apiKey.value)
    for (const side of ['uk', 'us'] as const) for (const [name, value] of Object.entries(patch[side])) {
      stored(catalog, `Models:Speech:${side === 'uk' ? 'Uk' : 'Us'}:${name[0].toUpperCase()}${name.slice(1)}`, value)
    }
    puts++
    return json(route, modelOf(catalog))
  })
  await page.route(previewUrl, route => {
    expect(route.request().headers()['if-match']).toBe(revision)
    expect(route.request().headers()['x-signacore-csrf']).toBeTruthy()
    previews.push(route.request().postDataJSON())
    return route.fulfill({ contentType: 'audio/mpeg', path: tone })
  })
  await page.goto('/#/settings/speech')
  await control(page, '语音服务地址').fill('https://api.minimax.io/v1')
  await typeKey(page, '语音 API Key', 'synthetic-draft-key')
  await control(page, '英式音色').fill('uk-draft')
  await control(page, '英式语速').fill('0.9')
  await control(page, '英式情绪').selectOption('calm')
  await control(page, '英式音高').fill('-2')
  await control(page, '英式音量').fill('0.8')
  await control(page, '美式音色').fill('us-draft')
  await control(page, '美式语速').fill('1.1')
  await control(page, '美式情绪').selectOption('happy')
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
  // A preview keeps the typed credential for the explicit save that may follow it.
  await expect(control(page, '语音 API Key')).toHaveValue('synthetic-draft-key')
  await save(page).click()
  await expect(notice(page)).toContainText('已保存')
  expect(puts).toBe(1)
  await expect(control(page, '语音 API Key')).toHaveCount(0)
  await expect(page.getByText('已有音频不会自动重新生成', { exact: false })).toBeVisible()
})

test('the address decides which speech controls show, each keeps its drafts, and an address the server cannot use is named', async ({ page }) => {
  await mockSettings(page, catalogFixture())
  const previews: unknown[] = []
  await page.route(previewUrl, route => {
    previews.push(route.request().postDataJSON())
    return route.fulfill({ contentType: 'audio/mpeg', path: tone })
  })
  await page.goto('/#/settings/speech')
  const address = control(page, '语音服务地址')
  const unsupported = page.getByText('当前语音服务地址不是受支持的官方地址', { exact: false })
  // With no address the server reads the MiniMax way.
  await expect(control(page, '英式情绪')).toBeVisible()
  await expect(control(page, '英式风格描述')).toHaveCount(0)
  await expect(unsupported).toHaveCount(0)
  await control(page, '英式情绪').selectOption('calm')
  await address.fill('https://api.openai.com/v1')
  await expect(control(page, '英式情绪')).toHaveCount(0)
  await expect(unsupported).toHaveCount(0)
  await control(page, '英式风格描述').fill('清晰、平静，适合教学。')
  await control(page, '美式风格描述').fill('自然、活泼。')
  await page.getByRole('button', { name: '生成 UK 试听', exact: true }).click()
  await expect(page.getByLabel('UK 试听播放器')).toBeVisible()
  expect(previews[0]).toMatchObject({ locale: 'uk', tts: { endpoint: 'https://api.openai.com/v1', uk: { style: '清晰、平静，适合教学。' } } })
  await control(page, '英式风格描述').fill('温和。')
  await expect(page.getByText('当前试听对应旧参数', { exact: false })).toBeVisible()
  // Neither official base: said here, and generation would stop safely on the server.
  await address.fill('https://speech.example.test/v1')
  await expect(unsupported).toBeVisible()
  // Changing the address back and forth keeps what was typed for each protocol.
  await address.fill('')
  await expect(unsupported).toHaveCount(0)
  await expect(control(page, '英式情绪')).toHaveValue('calm')
  await address.fill('https://api.openai.com/v1')
  await expect(control(page, '英式风格描述')).toHaveValue('温和。')
  await expect(control(page, '美式风格描述')).toHaveValue('自然、活泼。')
})

test('a canceled or outdated preview cannot publish a late result; leaving releases the audio', async ({ page }) => {
  await mockSettings(page, catalogFixture())
  await page.addInitScript(() => {
    const original = URL.revokeObjectURL
    Object.assign(window, { revokedAudio: [] as string[] })
    URL.revokeObjectURL = value => { (window as unknown as { revokedAudio: string[] }).revokedAudio.push(value); original.call(URL, value) }
  })
  let pending: Route | undefined
  await page.route(previewUrl, route => { pending = route })
  await page.goto('/#/settings/speech')
  await page.getByRole('button', { name: '生成 UK 试听', exact: true }).click()
  await expect.poll(() => Boolean(pending)).toBe(true)
  await control(page, '英式情绪').selectOption('calm')
  await expect(page.getByRole('status', { name: 'UK 试听状态' })).toContainText('参数已变化')
  await pending!.fulfill({ contentType: 'audio/mpeg', path: tone }).catch(() => undefined)
  await expect(page.getByLabel('UK 试听播放器')).toHaveCount(0)
  pending = undefined
  await page.getByRole('button', { name: '生成 UK 试听', exact: true }).click()
  await expect.poll(() => Boolean(pending)).toBe(true)
  await page.getByRole('button', { name: '取消 UK 试听', exact: true }).click()
  await expect(page.getByRole('status', { name: 'UK 试听状态' })).toContainText('已取消等待')
  await pending!.fulfill({ contentType: 'audio/mpeg', path: tone }).catch(() => undefined)
  await page.unroute(previewUrl)
  await page.route(previewUrl, route => route.fulfill({ contentType: 'audio/mpeg', path: tone }))
  await page.getByRole('button', { name: '生成 UK 试听', exact: true }).click()
  const player = page.getByLabel('UK 试听播放器')
  await expect(player).toBeVisible()
  const objectUrl = await player.getAttribute('src')
  answerPrompt(page, true)
  await category(page, '审核助手').click()
  expect(await page.evaluate(() => (window as unknown as { revokedAudio: string[] }).revokedAudio)).toContain(objectUrl)
})

for (const status of [400, 412, 429, 502]) {
  test(`a preview answered with ${status} is safe, keeps the drafts and is never retried`, async ({ page }) => {
    await mockSettings(page, catalogFixture())
    let calls = 0
    await page.route(previewUrl, route => { calls++; return json(route, { message: 'synthetic-secret-error' }, status) })
    await page.goto('/#/settings/speech')
    await control(page, '英式情绪').selectOption('happy')
    await page.getByRole('button', { name: '生成 UK 试听', exact: true }).click()
    await expect(page.getByRole('status', { name: 'UK 试听状态' })).not.toContainText('正在使用草稿')
    await expect(control(page, '英式情绪')).toHaveValue('happy')
    await expect(page.locator('body')).not.toContainText('synthetic-secret-error')
    await expect(page.getByLabel('UK 试听播放器')).toHaveCount(0)
    expect(calls).toBe(1)
  })
}

test('previews decode in Chromium, fit a phone-sized screen and ignore a sign-out underneath them', async ({ page }) => {
  await mockSettings(page, catalogFixture())
  await page.setViewportSize({ width: 375, height: 812 })
  await page.route(previewUrl, route => route.fulfill({ contentType: 'audio/mpeg', path: tone }))
  await page.goto('/#/settings/speech')
  await page.getByRole('button', { name: '生成 UK 试听', exact: true }).click()
  const player = page.getByLabel('UK 试听播放器')
  await expect(player).toBeVisible()
  await player.evaluate(async node => { await (node as HTMLAudioElement).play() })
  await expect.poll(() => player.evaluate(node => (node as HTMLAudioElement).currentTime)).toBeGreaterThan(0)
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
  const accessibility = await new AxeBuilder({ page }).include('.lx-page').withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze()
  expect(accessibility.violations).toEqual([])
  let pending: Route | undefined
  await page.unroute(previewUrl)
  await page.route(previewUrl, route => { pending = route })
  await page.getByRole('button', { name: '生成 US 试听', exact: true }).click()
  await expect.poll(() => Boolean(pending)).toBe(true)
  await page.getByRole('button', { name: '退出登录' }).click()
  await expect(page).toHaveURL(/#\/login/)
  await pending!.fulfill({ contentType: 'audio/mpeg', path: tone }).catch(() => undefined)
  await expect(page.getByLabel('US 试听播放器')).toHaveCount(0)
  await expect(page).toHaveURL(/#\/login/)
})

test('a catalog without one additive entry still shows and saves the rest', async ({ page }) => {
  const catalog = catalogFixture()
  catalog.fields = catalog.fields.filter(field => field.key !== 'Enrichment:TextConcurrency')
  await mockSettings(page, () => catalog)
  let puts = 0
  await page.route(enrichmentUrl, route => {
    if (route.request().method() === 'PUT') puts++
    return json(route, modelOf(catalogFixture()))
  })
  await page.goto('/#/settings/background')
  await expect(control(page, '文本请求并发')).toHaveCount(0)
  await expect(control(page, '任务执行并发')).toBeEnabled()
  await control(page, '任务执行并发').fill('2')
  await save(page).click()
  await expect(notice(page)).toContainText('已保存')
  expect(puts).toBe(1)
})

test('a restart-only value is saved for the next start without touching what runs now', async ({ page }) => {
  const catalog = catalogFixture()
  await mockSettings(page, () => catalog)
  let puts = 0
  await page.route(startupUrl, async route => {
    puts++
    expect(route.request().postDataJSON()).toEqual({ 'Logging:LogLevel:Default': 'Debug' })
    expect(route.request().headers()['if-match']).toBe(revision)
    await new Promise(resolve => setTimeout(resolve, 100))
    Object.assign(entry(catalog, 'Logging:LogLevel:Default'), { savedValue: 'Debug', pendingRestart: true, source: 'file' })
    return json(route, catalog)
  })
  await page.route(audioUrl, route => json(route, {}, 503))
  await page.goto('/#/settings/logging')
  await expect(control(page, '默认日志级别')).toHaveValue('Information')
  await control(page, '默认日志级别').selectOption('Debug')
  await expect(page.getByRole('status', { name: '未保存的修改' })).toHaveText('1 处未保存，1 项重启后生效')
  await save(page).click()
  await expect(notice(page)).toHaveText('已保存到配置文件，1 项将在重启服务后生效。')
  expect(puts).toBe(1)
  // The control edits what the next start reads; the value in effect is still the old one.
  await expect(control(page, '默认日志级别')).toHaveValue('Debug')
  const pending = row(page, 'Logging:LogLevel:Default').getByLabel('重启后的值')
  await expect(pending).toContainText('Information')
  await expect(pending).toContainText('Debug')
})
