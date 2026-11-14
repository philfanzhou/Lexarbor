import { expect, test } from '@playwright/test'
import { mockAntiforgery } from './support/antiforgery'

const route = '**/admin/api-keys'
const secret = 'synthetic-one-time-business-key'

test('issues and revokes a key using the admin session without persisting its secret', async ({ page }) => {
  await mockAntiforgery(page)
  await page.route('**/admin/auth/session', request => request.fulfill({ json: { username: 'test-admin', roles: ['admin'] } }))
  await page.route('**/admin/system/version', request => request.fulfill({ json: { version: '1.0.0' } }))
  const metadata = { id: 'test-key', callerId: 'study', scopes: ['catalog'], createdAtUnixMs: 1, revokedAtUnixMs: null as number | null }
  let issued = false
  await page.route(route, async request => {
    if (request.request().method() === 'POST') {
      expect(request.request().headers()['x-signacore-csrf']).toBeTruthy()
      expect(request.request().postDataJSON()).toEqual({ callerId: 'study', scopes: ['catalog'] })
      issued = true
      return request.fulfill({ json: { success: true, data: { key: secret, metadata } } })
    }
    return request.fulfill({ json: { success: true, data: { items: issued ? [metadata] : [] } } })
  })
  await page.route(`${route}/test-key`, request => {
    expect(request.request().method()).toBe('DELETE')
    expect(request.request().headers()['x-signacore-csrf']).toBeTruthy()
    metadata.revokedAtUnixMs = 2
    return request.fulfill({ json: { success: true } })
  })

  await page.goto('/#/settings/api-keys')
  await expect(page.getByText('暂无 API Key')).toBeVisible()
  await page.getByLabel('调用方标识').fill('study')
  await page.getByRole('checkbox', { name: '词汇目录与出题' }).check()
  await page.getByRole('button', { name: '签发 Key' }).click()
  await expect(page.getByLabel('Key 明文，仅此次显示')).toHaveValue(secret)
  expect(await page.evaluate(() => JSON.stringify([localStorage, sessionStorage]).includes('synthetic-one-time-business-key'))).toBe(false)
  await page.reload()
  await expect(page.getByRole('cell', { name: 'study' })).toBeVisible()
  await expect(page.getByLabel('Key 明文，仅此次显示')).toHaveCount(0)
  page.once('dialog', dialog => dialog.accept())
  await page.getByRole('button', { name: '撤销', exact: true }).click()
  await expect(page.getByRole('cell', { name: /已撤销/ })).toBeVisible()
})
