import { expect, test, type Route } from '@playwright/test'
import { mockAntiforgery } from './support/antiforgery'

const json = (route: Route, data: unknown, status = 200) => route.fulfill({
  status, contentType: 'application/json', body: JSON.stringify(data)
})

const pending = {
  source: 'ruoyu.student',
  submission: {
    id: 'sub-1', word: 'serendipity', partOfSpeech: 'n.', meaning: '运气', example: null,
    status: 'pending', definitionKept: false, createdAt: '2026-11-01T00:00:00Z', reviewedAt: null
  }
}

test('reviews a submission with assistant advice and approves the corrected definition', async ({ page }) => {
  const approvals: unknown[] = []
  let approved = false
  await page.route('**/admin/auth/session', route => json(route, { success: true, data: { username: 'admin', roles: ['admin'] } }))
  await mockAntiforgery(page)
  await page.route('**/admin/system/version', route => json(route, { success: true, data: { version: '1', revision: null, channel: 'test' } }))
  await page.route(/\/admin\/meaning-submissions\?/, route => json(route, { success: true, data: {
    configured: true, bookId: 'book-s', assistantEnabled: true,
    items: approved ? [] : [pending], totalCount: approved ? 0 : 1, totalPage: 1
  } }))
  await page.route('**/admin/meaning-submissions/sub-1/advice', route => json(route, { success: true, data: {
    verdict: 'revise', partOfSpeech: 'n.', meaning: '意外发现', phoneticUk: 'ˌserənˈdɪpəti', phoneticUs: null, comment: '释义不准确'
  } }))
  await page.route('**/admin/meaning-submissions/sub-1/approve', route => {
    approvals.push(route.request().postDataJSON())
    approved = true
    return json(route, { success: true, data: { ...pending.submission, status: 'approved' } })
  })

  await page.goto('/#/books')
  await page.getByRole('link', { name: '投稿审核' }).click()
  await expect(page.getByText('共 1 条')).toBeVisible()
  await page.getByRole('button', { name: '审核' }).click()
  await page.getByRole('button', { name: 'AI 审核建议' }).click()
  await expect(page.getByText('建议修改后通过')).toBeVisible()
  await page.getByRole('button', { name: '采用建议' }).click()
  await page.getByRole('button', { name: '通过', exact: true }).click()
  await expect.poll(() => approvals.length).toBe(1)
  expect(approvals[0]).toMatchObject({ word: 'serendipity', meaning: '意外发现', phoneticUk: 'ˌserənˈdɪpəti' })
  await expect(page.getByText('共 0 条')).toBeVisible()
})

test('warns when no supplementary book is configured', async ({ page }) => {
  await page.route('**/admin/auth/session', route => json(route, { success: true, data: { username: 'admin', roles: ['admin'] } }))
  await mockAntiforgery(page)
  await page.route('**/admin/system/version', route => json(route, { success: true, data: { version: '1', revision: null, channel: 'test' } }))
  await page.route(/\/admin\/meaning-submissions\?/, route => json(route, { success: true, data: {
    configured: false, bookId: null, assistantEnabled: false, items: [], totalCount: 0, totalPage: 0
  } }))
  await page.goto('/#/submissions')
  await expect(page.getByText('未配置补充词书')).toBeVisible()
})
