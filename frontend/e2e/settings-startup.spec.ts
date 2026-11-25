import { expect, test, type Page } from '@playwright/test'
import {
  catalogFixture, category, clear, control, entry, json, mockSettings, nextRevision, notice, overridden, rateLimitUrl, rateLimitsOf,
  replace, revision, reread, row, save, secretsEverywhere, startupUrl, switchOf, unknownState
} from './support/settingsCatalog'

const authority = (page: Page) => control(page, '身份提供方地址')
const secretInput = (page: Page) => page.locator('input[type="password"]')

// What the restart endpoint's commit leaves behind: waiting for the next start.
function waiting(catalog: ReturnType<typeof catalogFixture>, key: string, value: unknown) {
  const field = entry(catalog, key)
  if (field.kind === 'secret') { field.hasValue = true; field.savedHasValue = value !== '' }
  else field.savedValue = value
  field.pendingRestart = true
  field.source = 'file'
}

test('a secret is told by presence on each side of the restart, and its edit by its action', async ({ page }) => {
  const catalog = catalogFixture()
  // A secret cleared for the next start, and one set for it: neither value ever reaches the page.
  Object.assign(entry(catalog, 'AdminAuthentication:OidcCode:ClientSecret'), { hasValue: true, savedHasValue: false, pendingRestart: true })
  Object.assign(entry(catalog, 'Lexarbor:Integration:ApiKey'), { hasValue: false, savedHasValue: true, pendingRestart: true })
  await mockSettings(page, () => catalog)
  await page.goto('/#/settings/identity')
  const secret = row(page, 'AdminAuthentication:OidcCode:ClientSecret')
  const pending = secret.getByLabel('重启后的值')
  // The label and the value are neighbouring elements, so each is read on its own.
  const says = async (text: string[]) => { for (const part of text) await expect(pending).toContainText(part) }
  await says(['当前生效', '已设置（不显示原值）', '重启后', '未设置'])
  await clear(page, '登录应用 Client Secret').click()
  await says(['保存并重启后', '清除'])
  await page.getByRole('button', { name: '放弃修改', exact: true }).click()
  await says(['重启后', '未设置'])

  await category(page, '学习服务连接').click()
  const key = row(page, 'Lexarbor:Integration:ApiKey').getByLabel('重启后的值')
  await expect(key).toContainText('当前生效')
  await expect(key).toContainText('未设置')
  await expect(key).toContainText('重启后')
  await expect(key).toContainText('已设置（不显示原值）')
})

test('restart-only values are saved for the next start with CSRF and the revision, and marked as waiting', async ({ page }) => {
  const catalog = catalogFixture()
  await mockSettings(page, () => catalog)
  let puts = 0
  await page.route(startupUrl, async route => {
    puts++
    expect(route.request().postDataJSON()).toEqual({
      'IdentityService:Authority': 'https://id.example.com', 'IdentityService:Issuer': 'https://id.example.com'
    })
    expect(route.request().headers()['if-match']).toBe(revision)
    expect(route.request().headers()['x-signacore-csrf']).toBeTruthy()
    await new Promise(resolve => setTimeout(resolve, 100))
    for (const key of ['IdentityService:Authority', 'IdentityService:Issuer']) waiting(catalog, key, 'https://id.example.com')
    catalog.revision = nextRevision
    return json(route, catalog)
  })
  await page.goto('/#/settings/identity')
  await expect(page.getByRole('note')).toContainText('保存后需要重启才生效')
  await authority(page).fill('https://id.example.com')
  await control(page, '令牌签发者').fill('https://id.example.com')
  await expect(page.getByRole('status', { name: '未保存的修改' })).toHaveText('2 处未保存，2 项重启后生效')
  await save(page).click()
  await expect(notice(page)).toHaveText('已保存到配置文件，2 项将在重启服务后生效。')
  expect(puts).toBe(1)
  const pending = row(page, 'IdentityService:Authority').getByLabel('重启后的值')
  await expect(pending).toContainText('当前生效')
  await expect(pending).toContainText('http://localhost:8080')
  await expect(pending).toContainText('https://id.example.com')
  // The control edits what the next start reads.
  await expect(authority(page)).toHaveValue('https://id.example.com')
  await expect(category(page, '登录与身份')).toHaveAttribute('title', '登录与身份：2 项已保存，等待重启生效')
})

test('a credential only the next start reads is replaced through the restart endpoint and never shown', async ({ page }) => {
  const catalog = catalogFixture()
  entry(catalog, 'AdminAuthentication:OidcCode:ClientSecret').hasValue = true
  await mockSettings(page, () => catalog)
  let puts = 0
  await page.route(startupUrl, route => {
    puts++
    expect(route.request().postDataJSON()).toEqual({ 'AdminAuthentication:OidcCode:ClientSecret': { action: 'replace', value: 'synthetic-rotated-secret' } })
    return json(route, catalog)
  })
  await page.goto('/#/settings/identity')
  await expect(row(page, 'AdminAuthentication:OidcCode:ClientSecret')).toContainText('已设置（不显示原值）')
  await replace(page, '登录应用 Client Secret').click()
  await control(page, '登录应用 Client Secret').fill('synthetic-rotated-secret')
  await expect(row(page, 'AdminAuthentication:OidcCode:ClientSecret').getByLabel('重启后的值')).toContainText('已更换（不显示原值）')
  await save(page).click()
  await expect(notice(page)).toContainText('已保存到配置文件')
  await expect(notice(page)).toContainText('密钥输入已清空')
  expect(puts).toBe(1)
  await expect(secretInput(page)).toHaveCount(0)
  await expect(row(page, 'AdminAuthentication:OidcCode:ClientSecret')).toContainText('已设置（不显示原值）')
  await expect(page.locator('body')).not.toContainText('synthetic-rotated-secret')
  const everywhere = await secretsEverywhere(page)
  expect(everywhere.local).toEqual({})
  expect(everywhere.url).not.toContain('synthetic-rotated-secret')
})

test('the automatic choice of a three-way key removes it, and a list is sent exactly as entered', async ({ page }) => {
  const catalog = catalogFixture()
  entry(catalog, 'IdentityService:RequireHttpsMetadata').value = false
  entry(catalog, 'IdentityService:RequireHttpsMetadata').savedValue = false
  entry(catalog, 'IdentityService:RequireHttpsMetadata').source = 'file'
  await mockSettings(page, () => catalog)
  const bodies: unknown[] = []
  await page.route(startupUrl, route => {
    const body = route.request().postDataJSON() as Record<string, unknown>
    bodies.push(body)
    // A committed value is what the next read answers, still waiting for its restart.
    for (const [key, value] of Object.entries(body)) {
      if (value && typeof value === 'object') continue
      entry(catalog, key).savedValue = value
      entry(catalog, key).pendingRestart = true
    }
    return json(route, catalog)
  })
  await page.goto('/#/settings/identity')
  await control(page, '强制 HTTPS 获取签名元数据').selectOption('auto')
  await save(page).click()
  await expect(notice(page)).toContainText('已保存到配置文件')
  expect(bodies[0]).toEqual({ 'IdentityService:RequireHttpsMetadata': null })

  await category(page, '网络与代理').click()
  const proxies = control(page, '受信任的代理地址')
  await proxies.fill('172.18.0.2')
  await proxies.press('Enter')
  await proxies.fill('10.0.0.1')
  await proxies.press('Enter')
  // A repeated entry is not added twice.
  await proxies.fill('10.0.0.1')
  await proxies.press('Enter')
  await expect(row(page, 'Network:TrustedProxies').getByRole('button', { name: /^移除 / })).toHaveCount(2)
  await row(page, 'Network:TrustedProxies').getByRole('button', { name: '移除 172.18.0.2' }).click()
  await control(page, '受信任的代理网段').fill('172.18.0.0/16')
  await control(page, '受信任的代理网段').press('Enter')
  await control(page, '转发跳数上限').fill('2')
  await save(page).click()
  await expect(notice(page)).toContainText('已保存到配置文件，3 项将在重启服务后生效。')
  expect(bodies[1]).toEqual({ 'Network:TrustedProxies': ['10.0.0.1'], 'Network:TrustedNetworks': ['172.18.0.0/16'], 'Network:ForwardLimit': 2 })
})

test('a refusal names its fields, keeps the draft, and clears when the value is corrected', async ({ page }) => {
  await mockSettings(page, catalogFixture())
  let puts = 0
  await page.route(startupUrl, route => {
    puts++
    return puts === 1
      ? json(route, { message: 'Invalid startup settings.', fields: ['IdentityService:Authority'] }, 400)
      : json(route, catalogFixture())
  })
  await page.goto('/#/settings/identity')
  await authority(page).fill('not a url')
  await control(page, '令牌签发者').fill('https://id.example.com')
  await save(page).click()
  await expect(notice(page)).toContainText('保存失败（400）')
  await expect(notice(page)).toContainText('标出的配置项未通过检查')
  const refused = row(page, 'IdentityService:Authority').getByRole('alert')
  await expect(refused).toHaveText('服务器未接受此值，请检查后重试。')
  // The other field is not blamed, and nothing is lost.
  await expect(row(page, 'IdentityService:Issuer').getByRole('alert')).toHaveCount(0)
  await expect(authority(page)).toHaveValue('not a url')
  await expect(page.getByRole('status', { name: '未保存的修改' })).toContainText('1 项需要修正')
  await expect(save(page)).toBeDisabled()
  await authority(page).fill('https://id.example.com')
  await expect(refused).toHaveCount(0)
  await expect(save(page)).toBeEnabled()
  await save(page).click()
  await expect(notice(page)).toContainText('已保存到配置文件')
  expect(puts).toBe(2)
})

test('a deployment override is read-only with its reason and stays out of the save', async ({ page }) => {
  const catalog = catalogFixture()
  entry(catalog, 'IdentityService:Authority').value = 'https://from-environment.example.com'
  Object.assign(entry(catalog, 'IdentityService:Authority'), { editable: false, source: 'environment', reason: overridden })
  await mockSettings(page, () => catalog)
  let puts = 0
  await page.route(startupUrl, route => {
    puts++
    expect(route.request().postDataJSON()).toEqual({ 'IdentityService:Issuer': 'https://id.example.com' })
    return json(route, catalog)
  })
  await page.goto('/#/settings/identity')
  await expect(authority(page)).toHaveCount(0)
  await expect(row(page, 'IdentityService:Authority')).toContainText('https://from-environment.example.com')
  await expect(row(page, 'IdentityService:Authority')).toContainText('来源：环境变量 · 网页只读')
  await expect(row(page, 'IdentityService:Authority')).not.toContainText('默认')
  await control(page, '令牌签发者').fill('https://id.example.com')
  await save(page).click()
  await expect(notice(page)).toContainText('已保存到配置文件')
  expect(puts).toBe(1)
})

test('an override that the server reports is named on its field, and the reread shows the lock', async ({ page }) => {
  let reads = 0
  await mockSettings(page, catalogFixture())
  await page.route(startupUrl, route => json(route, { message: 'Settings are overridden by deployment configuration.', fields: ['IdentityService:Issuer'] }, 409))
  await page.route('**/admin/system/settings/catalog', route => {
    reads++
    const catalog = catalogFixture()
    if (reads > 1) {
      entry(catalog, 'IdentityService:Issuer').value = 'https://locked.example.com'
      Object.assign(entry(catalog, 'IdentityService:Issuer'), { editable: false, source: 'environment', reason: overridden })
    }
    return json(route, catalog)
  })
  await page.goto('/#/settings/identity')
  await control(page, '令牌签发者').fill('https://id.example.com')
  await save(page).click()
  await expect(notice(page)).toContainText('保存失败（409）')
  // The refusal names the field; the form waits for an explicit read before it is shown again.
  await expect(notice(page)).toContainText('涉及：令牌签发者。')
  await expect(unknownState(page)).toBeVisible()
  await reread(page).click()
  await expect(control(page, '令牌签发者')).toHaveCount(0)
  await expect(row(page, 'IdentityService:Issuer')).toContainText('https://locked.example.com')
  await expect(row(page, 'IdentityService:Issuer')).toContainText('网页只读')
})

test('a section the deployment owns says why once, and each of its keys shows only its locked value', async ({ page }) => {
  await mockSettings(page, catalogFixture())
  await page.goto('/#/settings/data-protection')
  const keys = page.locator('section', { has: page.getByRole('heading', { name: '根密钥', exact: true }) })
  await expect(keys.getByRole('note')).toContainText('更换它会让所有管理员登录失效')
  await expect(keys.locator('.lx-setting')).toHaveCount(2)
  await expect(keys.locator('input, button')).toHaveCount(0)
  await expect(keys).toContainText('data/data-protection-root-key')

  // A read-only key beside editable ones keeps its reason on its own row.
  await category(page, '数据库').click()
  const connection = row(page, 'ConnectionStrings:Default')
  await expect(connection).toContainText('PostgreSQL 连接串')
  await expect(connection).toContainText('只读')
  await expect(connection).toContainText('运行中的进程已绑定这个数据库')
  await expect(connection.locator('input, button')).toHaveCount(0)
  await expect(control(page, '启动时初始化数据库')).toBeEnabled()
  await expect(control(page, '旧版 SQLite 数据库路径')).toHaveValue('data/vocabulary.db')
  await expect(control(page, '慢查询阈值')).toHaveValue('500')
})

test('when a live switch commits and a restart value is refused, the first is confirmed and the second named', async ({ page }) => {
  const catalog = catalogFixture()
  await mockSettings(page, () => catalog)
  const matches: (string | undefined)[] = []
  await page.route(rateLimitUrl, async route => {
    matches.push(route.request().headers()['if-match'])
    expect(route.request().postDataJSON()).toEqual({ adminLoginEnabled: true })
    await new Promise(resolve => setTimeout(resolve, 100))
    entry(catalog, 'RateLimits:AdminLogin:Enabled').value = true
    entry(catalog, 'RateLimits:AdminLogin:Enabled').source = 'file'
    catalog.revision = nextRevision
    return json(route, rateLimitsOf(catalog, nextRevision))
  })
  await page.route(startupUrl, route => {
    matches.push(route.request().headers()['if-match'])
    // The second request carries the revision the first one answered.
    return matches.length === 2
      ? json(route, { message: 'Invalid startup settings.', fields: ['RateLimits:AdminLogin:PermitLimit'] }, 400)
      : json(route, catalog)
  })
  await page.goto('/#/settings/rate-limits')
  await switchOf(page, '启用登录限流').check()
  await control(page, '登录请求次数上限').fill('500')
  await save(page).click()
  await expect(notice(page)).toHaveText('前 1 项已保存并回读确认。保存失败（400）：标出的配置项未通过检查，请修正后重新保存。 涉及：登录请求次数上限。')
  expect(matches).toEqual([revision, nextRevision])
  // The switch is in effect; the refused number is still a draft with its reason.
  await expect(switchOf(page, '启用登录限流')).toBeChecked()
  await expect(row(page, 'RateLimits:AdminLogin:PermitLimit').getByRole('alert')).toBeVisible()
  await expect(control(page, '登录请求次数上限')).toHaveValue('500')
  await control(page, '登录请求次数上限').fill('400')
  await save(page).click()
  await expect(notice(page)).toHaveText('已保存到配置文件，1 项将在重启服务后生效。')
  expect(matches[2]).toBe(nextRevision)
})

test('a draft survives an unknown state and still asks before the page is left', async ({ page }) => {
  await mockSettings(page, catalogFixture())
  await page.route(startupUrl, route => json(route, { message: 'Settings changed.' }, 412))
  await page.goto('/#/settings/identity')
  await authority(page).fill('https://id.example.com')
  await save(page).click()
  await expect(unknownState(page)).toBeVisible()
  const dismissed = page.waitForEvent('dialog')
  page.once('dialog', dialog => dialog.dismiss())
  await category(page, '日志').click()
  expect((await dismissed).message()).toContain('未保存的修改')
  await expect(page).toHaveURL(/#\/settings\/identity$/)
  await reread(page).click()
  await expect(authority(page)).toHaveValue('https://id.example.com')
  await expect(save(page)).toBeEnabled()
})

test('the service page reports what the host is, none of which a configuration changes', async ({ page }) => {
  await mockSettings(page, catalogFixture())
  await page.goto('/#/settings/service')
  const facts = page.getByRole('region', { name: '运行信息' })
  await expect(facts).toContainText('版本')
  await expect(facts).toContainText('0.0.0-dev')
  await expect(facts).toContainText('渠道 开发版')
  await expect(facts).toContainText('HTTP 监听端口')
  await expect(facts).toContainText('5008 · 固定，仅 HTTP/1')
  await expect(facts).toContainText('data/appsettings.json')
  await expect(facts.locator('input')).toHaveCount(0)
  await expect(control(page, '实例标识')).toBeEnabled()
})
