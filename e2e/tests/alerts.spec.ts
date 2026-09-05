import { expect, test, type Page } from '@playwright/test'

const PASSWORD = 'e2e-password-9'

async function login(page: Page) {
  await page.goto('/login')
  await page.getByPlaceholder('请输入用户名').fill('admin')
  await page.getByPlaceholder('请输入密码').fill(PASSWORD)
  await page.getByRole('button', { name: /登\s*录/ }).click()
  await expect(page.getByRole('heading', { name: /欢迎/ })).toBeVisible()
}

async function createCollectorViaApi(page: Page, name: string): Promise<{ id: number; agentToken: string }> {
  const response = await page.request.post('/api/collectors', {
    data: { name, tags: ['E2E'] },
  })
  expect(response.ok()).toBeTruthy()
  return (await response.json()) as { id: number; agentToken: string }
}

/** 模拟 agent 经 WS 上报一次指标，使采集器拥有"已上报指标"（详情页按指标建规则的数据基础）。 */
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

  // 修改参数：阈值 5 → 8（修改已换弹窗表单，校验错误内联展示，不再走 window.prompt）
  await memRow.getByRole('button', { name: '修改' }).click()
  const editDialog = page.locator('.dialog', { hasText: '修改规则参数' })
  await editDialog.locator('input[type="number"]').fill('8')
  await editDialog.getByRole('button', { name: '保存' }).click()
  await expect(editDialog).toBeHidden()
  await expect(memRow).toContainText('阈值 8')

  // 删除规则
  await page.once('dialog', (dialog) => dialog.accept())
  await memRow.getByRole('button', { name: '删除' }).click()
  await expect(rulesCard.locator('tbody tr', { hasText: '内存使用率' }).filter({ hasText: '阈值下越限' })).toHaveCount(0)

  // 采集器详情页按已上报指标创建规则（验收 3/5：已上报指标可配规则）
  const collectorName = `规则采集器-${Date.now()}`
  const collector = await createCollectorViaApi(page, collectorName)
  await reportOnceViaWebSocket(page, collector.agentToken)
  await page.goto(`/collectors/${collector.id}`)
  await expect(page.getByRole('heading', { name: collectorName })).toBeVisible()
  await page.getByRole('button', { name: '新建规则' }).click()
  const detailDialog = page.locator('.dialog')
  await detailDialog.locator('select').nth(1).selectOption({ label: '无数据' })
  await detailDialog.locator('input[type="number"]').first().fill('10')
  await detailDialog.getByRole('button', { name: '创建规则' }).click()
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
