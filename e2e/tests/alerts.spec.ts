import { expect, test, type Page } from '@playwright/test'

const PASSWORD = 'e2e-password-9'

async function login(page: Page) {
  await page.goto('/login')
  await page.getByPlaceholder('请输入用户名').fill('admin')
  await page.getByPlaceholder('请输入密码').fill(PASSWORD)
  await page.getByRole('button', { name: /登\s*录/ }).click()
  await expect(page.getByRole('heading', { name: /欢迎/ })).toBeVisible()
}

async function createDeviceViaApi(page: Page, name: string): Promise<number> {
  const response = await page.request.post('/api/devices', {
    data: { name, tags: ['E2E'] },
  })
  expect(response.ok()).toBeTruthy()
  const payload = (await response.json()) as { id: number }
  return payload.id
}

async function getTargetId(page: Page, deviceId: number): Promise<number> {
  const response = await page.request.get('/api/targets')
  expect(response.ok()).toBeTruthy()
  const targets = (await response.json()) as { id: number; deviceId: number | null }[]
  return targets.find((t) => t.deviceId === deviceId)!.id
}

test('告警规则页：渠道配置即生效、新设备自带默认规则、规则可增改关删、待发队列可见', async ({ page }) => {
  await login(page)
  await page.goto('/alerts')

  // napcat 渠道配置：保存后 token 只标记"已设置"，明文不回显
  await page.getByPlaceholder('如 http://127.0.0.1:3000').fill('http://127.0.0.1:39999')
  await page.getByPlaceholder('napcat access_token').fill('e2e-napcat-token')
  await page.getByPlaceholder('QQ 号或群号（数字）').fill('10001')
  await page.getByRole('button', { name: '保存渠道配置' }).click()
  await expect(page.getByText('已保存，napcat 配置即时生效')).toBeVisible()
  await expect(page.getByText(/已设置，留空保持不变/)).toBeVisible()
  const bodyText = await page.locator('body').innerText()
  expect(bodyText).not.toContain('e2e-napcat-token')

  // 新设备自带默认规则：阈值上限 ×3 + 心跳无数据
  const deviceName = `告警规则设备-${Date.now()}`
  const deviceId = await createDeviceViaApi(page, deviceName)
  const targetId = await getTargetId(page, deviceId)
  await page.reload()
  const ruleTable = page.locator('.rule-table').first()
  await expect(ruleTable.locator('tbody tr', { hasText: deviceName })).toHaveCount(4)

  // 新建规则：下限阈值（net_rx，B/s）
  const formCard = page.locator('.card', { hasText: '新建规则' })
  await formCard.locator('select').first().selectOption({ index: 1 })
  await formCard.locator('select').nth(1).selectOption('threshold_below')
  await formCard.locator('select').nth(2).selectOption('net_rx')
  await formCard.locator('input[type="number"]').first().fill('50')
  await formCard.getByRole('button', { name: '创建规则' }).click()
  await expect(page.getByText('规则已创建')).toBeVisible()
  await expect(ruleTable.locator('tbody tr', { hasText: '阈值下限' })).toHaveCount(1)

  // 关闭 → 已关闭；再启用
  const belowRow = ruleTable.locator('tbody tr', { hasText: '阈值下限' }).first()
  await belowRow.getByRole('button', { name: '已启用' }).click()
  await expect(ruleTable.locator('tbody tr', { hasText: '阈值下限' }).first().getByRole('button', { name: '已关闭' })).toBeVisible()
  await ruleTable.locator('tbody tr', { hasText: '阈值下限' }).first().getByRole('button', { name: '已关闭' }).click()
  await expect(ruleTable.locator('tbody tr', { hasText: '阈值下限' }).first().getByRole('button', { name: '已启用' })).toBeVisible()

  // 删除
  await ruleTable.locator('tbody tr', { hasText: '阈值下限' }).first().getByRole('button', { name: '删除' }).click()
  await expect(ruleTable.locator('tbody tr', { hasText: '阈值下限' })).toHaveCount(0)

  // 待发队列：napcat 未真正可达不阻塞配置，队列区块始终可见
  await expect(page.getByRole('heading', { name: /待发队列/ })).toBeVisible()
  await expect(page.getByText(/队列空闲|条/).first()).toBeVisible()
})
