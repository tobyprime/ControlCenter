import { expect, test, type Page } from '@playwright/test'

const PASSWORD = 'e2e-password-9'

async function login(page: Page) {
  await page.goto('/login')
  await page.getByPlaceholder('请输入用户名').fill('admin')
  await page.getByPlaceholder('请输入密码').fill(PASSWORD)
  await page.getByRole('button', { name: /登\s*录/ }).click()
  await expect(page.getByRole('heading', { name: /欢迎/ })).toBeVisible()
}

async function createTargetViaApi(page: Page, name: string): Promise<{ id: number; agentToken: string }> {
  const response = await page.request.post('/api/targets', {
    data: { name, tags: ['E2E'] },
  })
  expect(response.ok()).toBeTruthy()
  return (await response.json()) as { id: number; agentToken: string }
}

/** 模拟 agent 经 WS 上报一次指标，使目标拥有"已上报指标"（详情页按指标建规则的数据基础）。 */
async function reportOnceViaWebSocket(page: Page, token: string): Promise<void> {
  await page.evaluate(async (token) => {
    const ws = new WebSocket(`ws://${location.host}/agent/ws`)
    await new Promise<void>((resolve, reject) => {
      ws.onopen = () => resolve()
      ws.onerror = () => reject(new Error('WS 连接失败'))
    })
    ws.send(JSON.stringify({ type: 'auth', seq: 1, payload: { token } }))
    await new Promise((resolve) => setTimeout(resolve, 300))
    ws.send(JSON.stringify({ type: 'metrics.report', seq: 2, payload: { cpu: 12, mem: 34, disk: 56, netRx: 1024, netTx: 2048 } }))
    await new Promise((resolve) => setTimeout(resolve, 300))
    ws.close()
  }, token)
}

test('告警规则页：迁移播种的全局规则可见、可新建/关闭/删除规则、渠道配置即生效、待发队列可见', async ({ page }) => {
  await login(page)
  await page.goto('/alerts')

  // 一期全局默认阈值自动迁移为可编辑规则实例：列表应含 CPU/内存/磁盘阈值上越限 + 在线状态不符（验收 2）
  const rulesCard = page.locator('.card', { hasText: '规则列表' })
  await expect(rulesCard).toBeVisible()
  const cpuRow = rulesCard.locator('tbody tr', { hasText: 'CPU 使用率' }).first()
  await expect(cpuRow).toContainText('全局')
  await expect(cpuRow).toContainText('阈值上越限')
  await expect(rulesCard.locator('tbody tr', { hasText: '设备在线状态' }).first()).toContainText('状态不符')

  // 新建规则：全局 内存 阈值下越限（阈值 5）——先选类型（指标列表按类型过滤已存在规则）
  await rulesCard.getByRole('button', { name: '新建规则' }).click()
  const dialog = page.locator('.dialog')
  await dialog.locator('select').nth(2).selectOption({ label: '阈值下越限' })
  await dialog.locator('select').nth(1).selectOption({ label: '内存使用率（mem）' })
  await dialog.locator('input[type="number"]').first().fill('5')
  await dialog.getByRole('button', { name: '创建规则' }).click()
  const memRow = rulesCard.locator('tbody tr', { hasText: '内存使用率' }).filter({ hasText: '阈值下越限' }).first()
  await expect(memRow).toBeVisible()
  await expect(memRow).toContainText('阈值 5')

  // 关闭规则 → 状态变已关闭；再启用（验收 3：关闭的规则不再触发）
  await memRow.getByRole('button', { name: '关闭', exact: true }).click()
  await expect(memRow).toContainText('已关闭')
  await memRow.getByRole('button', { name: '启用', exact: true }).click()
  await expect(memRow).toContainText('启用')

  // 修改参数：阈值 5 → 8
  page.once('dialog', (dialog) => dialog.accept('8'))
  await memRow.getByRole('button', { name: '修改' }).click()
  await expect(memRow).toContainText('阈值 8')

  // 删除规则
  await page.once('dialog', (dialog) => dialog.accept())
  await memRow.getByRole('button', { name: '删除' }).click()
  await expect(rulesCard.locator('tbody tr', { hasText: '内存使用率' }).filter({ hasText: '阈值下越限' })).toHaveCount(0)

  // 目标详情页按已上报指标创建规则（验收 3/5：已上报指标可配规则）
  const targetName = `规则目标-${Date.now()}`
  const target = await createTargetViaApi(page, targetName)
  await reportOnceViaWebSocket(page, target.agentToken)
  await page.goto(`/targets/${target.id}`)
  await expect(page.getByRole('heading', { name: targetName })).toBeVisible()
  await page.getByRole('button', { name: '新建规则' }).click()
  const targetDialog = page.locator('.dialog')
  await targetDialog.locator('select').nth(1).selectOption({ label: '无数据' })
  await targetDialog.locator('input[type="number"]').first().fill('10')
  await targetDialog.getByRole('button', { name: '创建规则' }).click()
  const detailRuleRow = page.locator('.rules-table tbody tr', { hasText: '无数据' }).first()
  await expect(detailRuleRow).toBeVisible()
  await expect(detailRuleRow).toContainText('10 分钟')

  // napcat 渠道配置：保存后 token 只标记"已设置"，明文不回显
  await page.goto('/alerts')
  await page.getByPlaceholder('如 http://127.0.0.1:3000').fill('http://127.0.0.1:39999')
  await page.getByPlaceholder('napcat access_token').fill('e2e-napcat-token')
  await page.getByPlaceholder('QQ 号或群号（数字）').fill('10001')
  await page.getByRole('button', { name: '保存渠道配置' }).click()
  await expect(page.getByText('已保存，napcat 配置即时生效')).toBeVisible()
  await expect(page.getByText(/已设置，留空保持不变/)).toBeVisible()
  const bodyText = await page.locator('body').innerText()
  expect(bodyText).not.toContain('e2e-napcat-token')

  // 待发队列：napcat 未真正可达不阻塞配置，队列区块始终可见
  await expect(page.getByRole('heading', { name: /待发队列/ })).toBeVisible()
  await expect(page.getByText(/队列空闲|条/).first()).toBeVisible()
})
