import { expect, test, type Page, type Route } from '@playwright/test'
import AxeBuilder from '@axe-core/playwright'
import {
  audioUrl, catalogFixture, catalogUrl, category, clear, control, entry, json, mockSettings, nextRevision, notice,
  overridden, replace, revision, row, reread, reveal, save, secretsEverywhere, startupUrl, unknownState
} from './support/settingsCatalog'
import type { Catalog } from './support/settingsCatalog'

const region = (page: Page) => control(page, '区域')
const storageSwitch = (page: Page) => page.getByRole('switch', { name: '启用音频存储', exact: true })
const accessKey = (page: Page) => control(page, 'Access Key')
const secretKey = (page: Page) => control(page, 'Secret Key')
const secretInputs = (page: Page) => page.locator('input[type="password"]')

// The live module's own answer for whatever the catalog holds now.
function audioOf(catalog: Catalog, at = revision) {
  const value = (name: string) => entry(catalog, `Audio:${name}`).value
  const state = (name: string) => {
    const field = entry(catalog, `Audio:${name}`)
    return { source: field.source, editable: field.editable, reason: field.reason }
  }
  return {
    revision: at, enabled: value('Enabled'), internalEndpoint: value('InternalEndpoint'), publicEndpoint: value('PublicEndpoint'),
    region: value('Region'), bucket: value('Bucket'), keyPrefix: value('KeyPrefix'),
    hasAccessKey: entry(catalog, 'Audio:AccessKey').hasValue, hasSecretKey: entry(catalog, 'Audio:SecretKey').hasValue,
    fields: Object.fromEntries(['enabled', 'internalEndpoint', 'publicEndpoint', 'region', 'bucket', 'keyPrefix', 'accessKey', 'secretKey']
      .map(name => [name, state(name[0].toUpperCase() + name.slice(1))]))
  }
}
// What a committed save leaves behind: the value in effect, read from the file.
function commit(catalog: Catalog, key: string, value: unknown) {
  const field = entry(catalog, key)
  if (field.kind === 'secret') field.hasValue = value !== ''
  else field.value = value
  if (field.apply === 'restart') field.savedValue = value
  field.source = 'file'
}
async function typeSecret(page: Page, label: 'Access Key' | 'Secret Key', value: string) {
  await (replace(page, label).or(reveal(page, label))).click()
  await control(page, label).fill(value)
}

test('a first setup sends the changed fields with CSRF and the revision, once', async ({ page }) => {
  const catalog = catalogFixture()
  await mockSettings(page, () => catalog)
  let puts = 0
  await page.route(audioUrl, async route => {
    puts++
    const request = route.request()
    expect(request.postDataJSON()).toEqual({
      enabled: true, internalEndpoint: 'http://storage.test/', publicEndpoint: 'https://audio.test/',
      accessKey: { action: 'replace', value: 'synthetic-new-access' }, secretKey: { action: 'replace', value: 'synthetic-new-secret' }
    })
    expect(request.headers()['if-match']).toBe(revision)
    expect(request.headers()['x-signacore-csrf']).toBeTruthy()
    await new Promise(resolve => setTimeout(resolve, 150))
    for (const [key, value] of [
      ['Audio:Enabled', true], ['Audio:InternalEndpoint', 'http://storage.test/'], ['Audio:PublicEndpoint', 'https://audio.test/'],
      ['Audio:AccessKey', 'synthetic-new-access'], ['Audio:SecretKey', 'synthetic-new-secret']
    ] as const) commit(catalog, key, value)
    return json(route, audioOf(catalog))
  })
  await page.goto('/#/settings/audio-storage')
  await expect(storageSwitch(page)).not.toBeChecked()
  await control(page, '服务访问地址').fill('http://storage.test/')
  await control(page, '浏览器播放地址').fill('https://audio.test/')
  await typeSecret(page, 'Access Key', 'synthetic-new-access')
  await typeSecret(page, 'Secret Key', 'synthetic-new-secret')
  await storageSwitch(page).check()
  await save(page).click()
  await expect(notice(page)).toContainText('已保存到配置文件并立即生效')
  await expect(notice(page)).toContainText('密钥输入已清空')
  await expect(secretInputs(page)).toHaveCount(0)
  expect(puts).toBe(1)
  await page.reload()
  await expect(storageSwitch(page)).toBeChecked()
  await expect(page.getByText('已设置（不显示原值）')).toHaveCount(2)
})

test('a blank credential keeps the stored one; replacing and clearing are each explicit', async ({ page }) => {
  const catalog = catalogFixture()
  commit(catalog, 'Audio:AccessKey', 'synthetic-access')
  commit(catalog, 'Audio:SecretKey', 'synthetic-secret')
  await mockSettings(page, () => catalog)
  const payloads: unknown[] = []
  await page.route(audioUrl, route => {
    const payload = route.request().postDataJSON() as { region?: string }
    payloads.push(payload)
    // A committed value is what the next read answers.
    if (payload.region) commit(catalog, 'Audio:Region', payload.region)
    return json(route, audioOf(catalog))
  })
  await page.goto('/#/settings/audio-storage')
  // Opening a replacement and leaving it empty is not a change: there is nothing to save.
  await replace(page, 'Access Key').click()
  await expect(accessKey(page)).toBeVisible()
  await page.getByRole('button', { name: '取消', exact: true }).click()
  await expect(secretInputs(page)).toHaveCount(0)
  await expect(save(page)).toHaveCount(0)

  await region(page).fill('test-region')
  await save(page).click()
  await expect(notice(page)).toContainText('已保存')
  expect(payloads[0]).toEqual({ region: 'test-region', accessKey: { action: 'keep' }, secretKey: { action: 'keep' } })

  await clear(page, 'Secret Key').click()
  await expect(row(page, 'Audio:SecretKey')).toContainText('保存后将清除')
  await replace(page, 'Access Key').click()
  await accessKey(page).fill('synthetic-replacement')
  await save(page).click()
  await expect(notice(page)).toContainText('已保存')
  expect(payloads[1]).toEqual({ accessKey: { action: 'replace', value: 'synthetic-replacement' }, secretKey: { action: 'clear' } })
})

for (const status of [400, 409, 412, 428, 503]) {
  test(`HTTP ${status} never echoes a secret; a conflict requires a fresh read`, async ({ page }) => {
    await mockSettings(page, catalogFixture())
    let puts = 0
    await page.route(audioUrl, route => {
      if (route.request().method() === 'GET') return json(route, audioOf(catalogFixture()))
      puts++
      return json(route, { message: 'synthetic-secret-do-not-echo' }, status)
    })
    await page.goto('/#/settings/audio-storage')
    await region(page).fill('test-region')
    await reveal(page, 'Secret Key').click()
    await secretKey(page).fill('synthetic-secret-do-not-echo')
    await save(page).click()
    await expect(notice(page)).toContainText(`保存失败（${status}）`)
    await expect(notice(page)).toContainText('密钥输入已清空')
    await expect(page.locator('body')).not.toContainText('synthetic-secret-do-not-echo')
    expect(puts).toBe(1)
    if ([409, 412, 428].includes(status)) {
      await expect(unknownState(page)).toBeVisible()
      await reread(page).click()
    }
    await expect(region(page)).toHaveValue('test-region')
    await expect(secretInputs(page)).toHaveCount(0)
  })
}

test('a lost PUT response is checked with a read, clears secrets and shows the actual state', async ({ page }) => {
  const catalog = catalogFixture()
  await mockSettings(page, () => catalog)
  let puts = 0
  let gets = 0
  await page.route(catalogUrl, route => { gets++; return json(route, catalog) })
  await page.route(audioUrl, route => {
    if (route.request().method() === 'GET') return json(route, audioOf(catalog))
    puts++
    commit(catalog, 'Audio:Region', 'test-region')
    commit(catalog, 'Audio:SecretKey', 'synthetic-new-secret')
    return route.abort('failed')
  })
  await page.goto('/#/settings/audio-storage')
  await region(page).fill('test-region')
  await reveal(page, 'Secret Key').click()
  await secretKey(page).fill('synthetic-new-secret')
  await save(page).click()
  await expect(notice(page)).toContainText('保存响应未确认')
  await expect(secretInputs(page)).toHaveCount(0)
  expect(puts).toBe(1)
  expect(gets).toBe(2)
  await expect(region(page)).toHaveValue('test-region')
  await expect(row(page, 'Audio:SecretKey')).toContainText('已设置')
  await expect(save(page)).toHaveCount(0)
})

test('a lost save and a failed read stay unknown until an explicit read; nothing is replayed', async ({ page }) => {
  await mockSettings(page, catalogFixture())
  let reads = 0
  let puts = 0
  await page.route(catalogUrl, route => {
    reads++
    return reads === 2 ? route.abort('failed') : json(route, catalogFixture())
  })
  await page.route(audioUrl, route => {
    if (route.request().method() === 'GET') return json(route, audioOf(catalogFixture()))
    puts++
    return route.abort('timedout')
  })
  await page.goto('/#/settings/audio-storage')
  await region(page).fill('test-region')
  await reveal(page, 'Secret Key').click()
  await secretKey(page).fill('synthetic-new-secret')
  await save(page).click()
  await expect(notice(page)).toContainText('保存结果未知，回读失败')
  await expect(unknownState(page)).toBeVisible()
  await reread(page).click()
  await expect(secretInputs(page)).toHaveCount(0)
  await expect(region(page)).toHaveValue('test-region')
  expect(puts).toBe(1)
})

test('failed and malformed reads stay unknown; deployment-owned fields are left out of the save', async ({ page }) => {
  await mockSettings(page, catalogFixture())
  let reads = 0
  let puts = 0
  await page.route(catalogUrl, route => {
    reads++
    if (reads === 1) return json(route, {}, 503)
    if (reads === 2) return json(route, { revision })
    const catalog = catalogFixture()
    Object.assign(entry(catalog, 'Audio:InternalEndpoint'), { editable: false, source: 'environment', reason: overridden })
    Object.assign(entry(catalog, 'Audio:AccessKey'), { editable: false, source: 'commandLine', reason: overridden })
    return json(route, catalog)
  })
  await page.route(audioUrl, route => {
    if (route.request().method() === 'GET') return json(route, audioOf(catalogFixture()))
    puts++
    expect(route.request().postDataJSON()).toEqual({ region: 'test-region', secretKey: { action: 'replace', value: 'synthetic-storage-secret' } })
    return json(route, audioOf(catalogFixture()))
  })
  await page.goto('/#/settings/audio-storage')
  await expect(notice(page)).toContainText('状态未知')
  await reread(page).click()
  await expect(notice(page)).toContainText('状态未知')
  await reread(page).click()
  await expect(control(page, '服务访问地址')).toHaveCount(0)
  await expect(replace(page, 'Access Key')).toHaveCount(0)
  await expect(row(page, 'Audio:InternalEndpoint')).toContainText('来源：环境变量 · 网页只读')
  await expect(row(page, 'Audio:AccessKey')).toContainText('来源：启动参数 · 网页只读')
  await region(page).fill('test-region')
  await reveal(page, 'Secret Key').click()
  await secretKey(page).fill('synthetic-storage-secret')
  await save(page).click()
  await expect(notice(page)).toContainText('已保存')
  expect(puts).toBe(1)
})

for (const status of [401, 403]) {
  test(`HTTP ${status} keeps the shared authentication navigation`, async ({ page }) => {
    await mockSettings(page, catalogFixture())
    await page.route(catalogUrl, route => json(route, {}, status))
    await page.goto('/#/settings/audio-storage')
    await expect(page).toHaveURL(status === 401 ? /#\/login/ : /#\/forbidden/)
  })
}

for (const leave of ['navigate', 'logout'] as const) {
  test(`${leave} destroys a sensitive draft, and a stale request cannot reach the next session`, async ({ page }) => {
    await mockSettings(page, catalogFixture())
    let pending: Route | undefined
    await page.route(audioUrl, route => { pending = route })
    await page.goto('/#/settings/audio-storage')
    await region(page).fill('test-region')
    await reveal(page, 'Secret Key').click()
    await secretKey(page).fill('synthetic-private-draft')
    await save(page).click()
    await expect.poll(() => Boolean(pending)).toBe(true)
    if (leave === 'navigate') await page.getByRole('link', { name: '单条导入', exact: true }).click()
    else await page.getByRole('button', { name: '退出登录' }).click()
    await json(pending!, { message: 'Unauthorized' }, 401).catch(() => undefined)
    await expect(page).toHaveURL(leave === 'navigate' ? /#\/import$/ : /#\/login/)
    await expect(page.getByRole('heading', { name: '音频存储', level: 2 })).toHaveCount(0)
    if (leave === 'navigate') {
      await expect(page.locator('.session')).toContainText('ci-admin')
      await page.getByRole('link', { name: '系统设置', exact: true }).click()
      await category(page, '音频存储').click()
      await expect(secretInputs(page)).toHaveCount(0)
    }
  })
}

test('a phone-sized page is keyboard-operable, and a secret reaches no storage, URL or log', async ({ page }) => {
  await mockSettings(page, catalogFixture())
  const logs: string[] = []
  page.on('console', message => logs.push(message.text()))
  await page.setViewportSize({ width: 375, height: 812 })
  await page.goto('/#/settings/audio-storage')
  await storageSwitch(page).focus()
  await page.keyboard.press('Space')
  await expect(storageSwitch(page)).toBeChecked()
  await reveal(page, 'Access Key').click()
  await accessKey(page).fill('synthetic-memory-access')
  await expect(accessKey(page)).toHaveAttribute('type', 'password')
  await page.getByRole('button', { name: '取消', exact: true }).click()
  await reveal(page, 'Secret Key').click()
  await secretKey(page).fill('synthetic-memory-secret')
  await expect(secretKey(page)).toHaveAttribute('type', 'password')
  const everywhere = await secretsEverywhere(page)
  expect(everywhere.local).toEqual({})
  expect(everywhere.session).toEqual({})
  expect(everywhere.url + logs.join()).not.toContain('synthetic-memory-')
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  const results = await new AxeBuilder({ page }).include('.lx-page').withTags(['wcag2a', 'wcag2aa']).analyze()
  expect(results.violations).toEqual([])
})

test('a late successful PUT cannot populate a replacement page', async ({ page }) => {
  await mockSettings(page, catalogFixture())
  let pending: Route | undefined
  await page.route(audioUrl, route => { pending = route })
  await page.goto('/#/settings/audio-storage')
  await region(page).fill('test-region')
  await reveal(page, 'Secret Key').click()
  await secretKey(page).fill('synthetic-private-draft')
  await save(page).click()
  await expect.poll(() => Boolean(pending)).toBe(true)
  await page.getByRole('link', { name: '单条导入', exact: true }).click()
  await page.getByRole('link', { name: '系统设置', exact: true }).click()
  await category(page, '音频存储').click()
  await expect(region(page)).toHaveValue('us-east-1')
  await json(pending!, { ...audioOf(catalogFixture()), enabled: true, region: 'late-region', hasSecretKey: true, revision: nextRevision }).catch(() => undefined)
  await expect(region(page)).toHaveValue('us-east-1')
  await expect(storageSwitch(page)).not.toBeChecked()
  await expect(secretInputs(page)).toHaveCount(0)
  await expect(page.locator('.session')).toContainText('ci-admin')
})

test('a stale revision requires an explicit read, then a new submission with the fresh revision and no old secret', async ({ page }) => {
  await mockSettings(page, catalogFixture())
  let reads = 0
  let puts = 0
  await page.route(catalogUrl, route => {
    reads++
    // Another administrator moved the revision on; the stored region is still the old one.
    const catalog = catalogFixture()
    if (reads > 1) catalog.revision = nextRevision
    return json(route, catalog)
  })
  await page.route(audioUrl, route => {
    if (route.request().method() === 'GET') return json(route, audioOf(catalogFixture()))
    puts++
    if (puts === 1) return json(route, { message: 'Settings changed.' }, 412)
    expect(route.request().headers()['if-match']).toBe(nextRevision)
    expect(route.request().postDataJSON()).toEqual({ region: 'test-region', accessKey: { action: 'keep' }, secretKey: { action: 'keep' } })
    return json(route, audioOf(catalogFixture()))
  })
  await page.goto('/#/settings/audio-storage')
  await region(page).fill('test-region')
  await reveal(page, 'Secret Key').click()
  await secretKey(page).fill('synthetic-do-not-replay')
  await save(page).click()
  await expect(notice(page)).toContainText('保存失败（412）')
  expect(puts).toBe(1)
  await expect(unknownState(page)).toBeVisible()
  await reread(page).click()
  // The draft survives the read for review; the typed secret does not.
  await expect(region(page)).toHaveValue('test-region')
  await expect(secretInputs(page)).toHaveCount(0)
  await expect(save(page)).toBeEnabled()
  await save(page).click()
  await expect(notice(page)).toContainText('已保存到配置文件并立即生效')
  expect(puts).toBe(2)
})

test('an advanced limit is saved for the next start and marked as waiting', async ({ page }) => {
  const catalog = catalogFixture()
  await mockSettings(page, () => catalog)
  let puts = 0
  await page.route(startupUrl, async route => {
    puts++
    expect(route.request().postDataJSON()).toEqual({ 'Audio:MaxConcurrentUploads': 6 })
    expect(route.request().headers()['if-match']).toBe(revision)
    expect(route.request().headers()['x-signacore-csrf']).toBeTruthy()
    await new Promise(resolve => setTimeout(resolve, 100))
    Object.assign(entry(catalog, 'Audio:MaxConcurrentUploads'), { savedValue: 6, pendingRestart: true, source: 'file' })
    catalog.revision = nextRevision
    return json(route, catalog)
  })
  await page.goto('/#/settings/audio-storage')
  await expect(row(page, 'Audio:MaxConcurrentUploads')).toContainText('重启生效')
  await control(page, '并发上传数').fill('6')
  await expect(page.getByRole('status', { name: '未保存的修改' })).toHaveText('1 处未保存，1 项重启后生效')
  await save(page).click()
  await expect(notice(page)).toHaveText('已保存到配置文件，1 项将在重启服务后生效。')
  expect(puts).toBe(1)
  await expect(control(page, '并发上传数')).toHaveValue('6')
  await expect(row(page, 'Audio:MaxConcurrentUploads')).toContainText('待重启')
  const pending = row(page, 'Audio:MaxConcurrentUploads').getByLabel('重启后的值')
  await expect(pending).toContainText('当前生效')
  await expect(pending).toContainText('2')
  await expect(pending).toContainText('重启后')
  await expect(pending).toContainText('6')
  await expect(category(page, '音频存储')).toHaveAttribute('title', '音频存储：1 项已保存，等待重启生效')
})

test('an advanced limit out of range is stopped before it leaves the browser', async ({ page }) => {
  await mockSettings(page, catalogFixture())
  let puts = 0
  await page.route(startupUrl, route => { puts++; return json(route, catalogFixture()) })
  await page.goto('/#/settings/audio-storage')
  await control(page, '并发上传数').fill('9')
  await expect(row(page, 'Audio:MaxConcurrentUploads').getByRole('alert')).toHaveText('取值范围为 1 到 8。')
  await expect(page.getByRole('status', { name: '未保存的修改' })).toContainText('1 项需要修正')
  await expect(save(page)).toBeDisabled()
  expect(puts).toBe(0)
})
