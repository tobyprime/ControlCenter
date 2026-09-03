import { expect, test, type Page } from '@playwright/test'

const PASSWORD = 'e2e-password-9'

async function login(page: Page) {
  await page.goto('/login')
  await page.getByPlaceholder('请输入用户名').fill('admin')
  await page.getByPlaceholder('请输入密码').fill(PASSWORD)
  await page.getByRole('button', { name: /登\s*录/ }).click()
  await expect(page.getByRole('heading', { name: /欢迎/ })).toBeVisible()
}

async function createDeviceViaApi(page: Page, name: string): Promise<void> {
  const response = await page.request.post('/api/devices', {
    data: { name, tags: ['E2E'] },
  })
  expect(response.ok()).toBeTruthy()
}

test('告警配置页：渠道配置即生效、全局阈值可改、按设备覆盖可增删、待发队列可见', async ({ page }) => {
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

  // 全局阈值：修改 CPU 阈值 → 保存 → 刷新后仍生效
  const globalCard = page.locator('.card', { hasText: '全局默认阈值' })
  await globalCard.locator('input[type="number"]').first().fill('75')
  await globalCard.getByRole('button', { name: '保存CPU 使用率' }).click()
  await expect(page.getByText(/已保存：CPU 使用率 全局阈值 75%/)).toBeVisible()
  await page.reload()
  await expect(page.locator('.card', { hasText: '全局默认阈值' }).locator('input[type="number"]').first()).toHaveValue('75')

  // 按设备覆盖：添加 → 列表可见 → 删除
  const deviceName = `告警覆盖设备-${Date.now()}`
  await createDeviceViaApi(page, deviceName)
  await page.goto('/alerts')
  const overrideCard = page.locator('.card', { hasText: '按设备覆盖阈值' })
  await overrideCard.locator('select').first().selectOption({ label: deviceName })
  await overrideCard.locator('select').nth(1).selectOption('cpu')
  await overrideCard.locator('input[type="number"]').fill('50')
  await overrideCard.getByRole('button', { name: '添加 / 更新覆盖' }).click()
  const row = page.locator('.override-table tbody tr', { hasText: deviceName }).first()
  await expect(row).toContainText('CPU 使用率')
  await expect(row).toContainText('50%')
  await row.getByRole('button', { name: '删除' }).click()
  await expect(page.getByText('暂无覆盖：所有设备按全局默认阈值告警。')).toBeVisible()

  // 待发队列：napcat 未真正可达不阻塞配置，队列区块始终可见
  await expect(page.getByRole('heading', { name: /待发队列/ })).toBeVisible()
  await expect(page.getByText(/队列空闲|条/).first()).toBeVisible()
})
