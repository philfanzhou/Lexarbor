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


test('shows complete pending backlog and only fixed safe recovery messages', async ({ page }) => {
  await page.route('**/admin/auth/session', route => json(route, { success: true, data: { username: 'admin', roles: ['admin'] } }))
  await mockAntiforgery(page)
  await page.route('**/admin/system/version', route => json(route, { success: true, data: { version: '1', revision: null, channel: 'test' } }))
  await page.route(/\/admin\/meaning-submissions\?/, route => json(route, { success: true, data: {
    configured: true, assistantEnabled: true, items: [pending], totalCount: 1, totalPage: 1,
    automaticReview: { enabled: true, paused: false, pauseCode: null, pendingCount: 47, lastFailureCode: 'rate_limited', lastFailureAt: '2026-11-02T00:00:00Z', nextAttemptAt: '2026-11-02T00:10:00Z' }
  } }))
  await page.goto('/#/submissions')
  const state = page.getByLabel('自动审核状态')
  await expect(state).toContainText('全部待审积压 47 条')
  await expect(state).toContainText('服务限流')
  await expect(state).toContainText('无需人工处理')
  await expect(state.getByRole('link', { name: '检查自动审核设置' })).toHaveAttribute('href', '#/settings/review')
  await page.getByRole('button', { name: '审核', exact: true }).click()
  await expect(page.getByText('无需手动开始或先删除旧音频', { exact: false })).toBeVisible()
})

test('人工审核人筛选与可选说明保持旧审批，失败保留草稿', async ({ page }) => {
  let posts = 0
  const payloads: unknown[] = [], queries: string[] = []
  await page.route('**/admin/auth/session', route => json(route, { success: true, data: { username: 'admin', roles: ['admin'] } }))
  await mockAntiforgery(page)
  await page.route('**/admin/system/version', route => json(route, { success: true, data: { version: '1', revision: null, channel: 'test' } }))
  await page.route(/\/admin\/meaning-submissions\?/, route => {
    queries.push(new URL(route.request().url()).searchParams.get('reviewerKind') ?? '')
    return json(route, { success: true, data: { configured: true, assistantEnabled: false, items: [{ ...pending, reviewerKind: 'admin', submission: { ...pending.submission, reviewComment: 'existing reason' } }], totalCount: 1, totalPage: 1 } })
  })
  await page.route('**/admin/meaning-submissions/sub-1/approve', route => {
    posts++; payloads.push(route.request().postDataJSON())
    return posts === 1 ? json(route, { success: false, message: 'Synthetic validation refusal' }, 400) : json(route, { success: true, data: pending.submission })
  })
  await page.goto('/#/submissions')
  await page.getByLabel('审核人筛选').selectOption('admin')
  await expect.poll(() => queries.at(-1)).toBe('admin')
  await expect(page.locator('.el-table__body')).toContainText('existing reason')
  await page.getByRole('button', { name: '审核', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: '审核投稿', exact: true })
  await dialog.getByRole('textbox').nth(2).fill('corrected draft')
  await dialog.getByPlaceholder('修改说明或驳回原因（可选）').fill('Manual correction explanation')
  await dialog.getByRole('button', { name: '通过', exact: true }).click()
  await expect(page.getByText('通过失败：Synthetic validation refusal')).toBeVisible()
  await expect(dialog.getByRole('textbox').nth(2)).toHaveValue('corrected draft')
  await expect(dialog.getByPlaceholder('修改说明或驳回原因（可选）')).toHaveValue('Manual correction explanation')
  await dialog.getByRole('button', { name: '通过', exact: true }).click()
  await expect.poll(() => posts).toBe(2)
  expect(payloads[1]).toMatchObject({ meaning: 'corrected draft', reviewComment: 'Manual correction explanation' })
})

for (const comment of ['', 'Manual rejection reason']) test(`人工驳回说明${comment ? '可选填写' : '留空仍兼容'}且取消不提交`, async ({ page }) => {
  const posts: unknown[] = []
  await page.route('**/admin/auth/session', route => json(route, { success: true, data: { username: 'admin', roles: ['admin'] } }))
  await mockAntiforgery(page)
  await page.route('**/admin/system/version', route => json(route, { success: true, data: { version: '1', revision: null, channel: 'test' } }))
  await page.route(/\/admin\/meaning-submissions\?/, route => json(route, { success: true, data: { configured: true, assistantEnabled: false, items: [pending], totalCount: 1, totalPage: 1 } }))
  await page.route('**/admin/meaning-submissions/sub-1/reject', route => { posts.push(route.request().postData()); return json(route, { success: true, data: pending.submission }) })
  await page.goto('/#/submissions')
  await page.locator('.el-table').getByRole('button', { name: '驳回', exact: true }).click()
  await page.getByRole('button', { name: '取消', exact: true }).click()
  expect(posts).toEqual([])
  await expect(page.getByRole('dialog', { name: '驳回投稿' })).not.toBeVisible()
  await page.locator('.el-table').getByRole('button', { name: '驳回', exact: true }).click()
  if (comment) await page.getByPlaceholder('驳回原因（可选，最多 500 字）').fill(comment)
  await page.getByRole('dialog', { name: '驳回投稿' }).getByRole('button', { name: '驳回', exact: true }).click()
  await expect.poll(() => posts.length).toBe(1)
  expect(posts[0]).toBe(comment ? JSON.stringify({ reviewComment: comment }) : null)
})

test('关闭审核后迟到建议不能污染下一条或新草稿', async ({ page }) => {
  let held: Route | undefined
  await page.route('**/admin/auth/session', route => json(route, { success: true, data: { username: 'admin', roles: ['admin'] } }))
  await mockAntiforgery(page)
  await page.route('**/admin/system/version', route => json(route, { success: true, data: { version: '1', revision: null, channel: 'test' } }))
  await page.route(/\/admin\/meaning-submissions\?/, route => json(route, { success: true, data: { configured: true, assistantEnabled: true, items: [pending], totalCount: 1, totalPage: 1 } }))
  await page.route('**/admin/meaning-submissions/sub-1/advice', route => { held = route })
  await page.goto('/#/submissions')
  await page.getByRole('button', { name: '审核', exact: true }).click()
  await page.getByRole('button', { name: 'AI 审核建议' }).click()
  await expect.poll(() => !!held).toBe(true)
  await page.keyboard.press('Escape')
  await expect(page.getByRole('dialog', { name: '审核投稿' })).not.toBeVisible()
  await page.getByRole('button', { name: '审核', exact: true }).click()
  await held?.fulfill({ contentType: 'application/json', body: JSON.stringify({ success: true, data: { verdict: 'revise', meaning: 'STALE-ADVICE', comment: 'STALE-COMMENT' } }) }).catch(() => undefined)
  await expect(page.locator('body')).not.toContainText('STALE-ADVICE')
  await expect(page.getByPlaceholder('修改说明或驳回原因（可选）')).toHaveValue('')
})
