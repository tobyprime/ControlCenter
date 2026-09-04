import { expect, test, type Page } from '@playwright/test'

const PASSWORD = 'e2e-password-9'
const EVIDENCE_DIR = './evidence'

async function login(page: Page) {
  await page.goto('/login')
  await page.getByPlaceholder('请输入用户名').fill('admin')
  await page.getByPlaceholder('请输入密码').fill(PASSWORD)
  await page.getByRole('button', { name: /登\s*录/ }).click()
  await expect(page.getByRole('heading', { name: /欢迎/ })).toBeVisible()
}

async function expectNoHorizontalOverflow(page: Page, label: string) {
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
  )
  expect.soft(overflow, `${label} 横向溢出应为 0`).toBeLessThanOrEqual(0)
}

test.describe('目标管理（TOB-337 → TOB-361 泛化）', () => {
  test('新建目标 → 签发 token → 列表离线展示 → 编辑 → 重置 token → 删除', async ({ page }) => {
    await login(page)

    const deviceName = `E2E 测试机 ${Date.now()}`

    await page.getByRole('link', { name: '目标管理' }).click()
    await expect(page.getByRole('heading', { name: '目标管理' })).toBeVisible()

    // 新建设备
    await page.getByRole('button', { name: '新建目标' }).click()
    await page.getByPlaceholder('如：机房A 边缘网关').fill(deviceName)
    await page.getByPlaceholder('如：机房A，网关，内网').fill('机房E2E，冒烟')
    await page.getByRole('button', { name: '保存' }).click()

    // token 只显示一次
    await expect(page.getByText(`「${deviceName}」的 agent token`)).toBeVisible()
    const tokenText = await page.locator('code.token-value').innerText()
    expect(tokenText.startsWith('dpk_')).toBe(true)
    await page.screenshot({ path: `${EVIDENCE_DIR}/targets-token-dialog.png`, fullPage: true })
    await page.getByRole('button', { name: '我已保存，关闭' }).click()

    // 列表：离线状态 + 标签
    const card = page.locator('.device-card', { hasText: deviceName })
    await expect(card).toBeVisible()
    await expect(card.getByText('离线')).toBeVisible()
    await expect(card.getByText('机房E2E')).toBeVisible()
    await expect(card.getByText('冒烟')).toBeVisible()
    await page.screenshot({ path: `${EVIDENCE_DIR}/targets-list-offline.png`, fullPage: true })

    // 编辑
    await card.getByRole('button', { name: '编辑' }).click()
    await page.getByPlaceholder('如：机房A 边缘网关').fill(`${deviceName}（改）`)
    await page.getByPlaceholder('如：机房A，网关，内网').fill('机房E2E')
    await page.getByRole('button', { name: '保存' }).click()
    await expect(page.locator('.device-card', { hasText: `${deviceName}（改）` })).toBeVisible()

    // 重置 token：旧 token 提示、新 token 展示
    page.once('dialog', (dialog) => dialog.accept())
    await page
      .locator('.device-card', { hasText: `${deviceName}（改）` })
      .getByRole('button', { name: '重置 Token' })
      .click()
    await expect(page.getByText(`「${deviceName}（改）」的 agent token`)).toBeVisible()
    const newToken = await page.locator('code.token-value').innerText()
    expect(newToken).not.toBe(tokenText)
    await page.getByRole('button', { name: '我已保存，关闭' }).click()

    // 删除（确认对话框）
    page.once('dialog', (dialog) => dialog.accept())
    await page
      .locator('.device-card', { hasText: `${deviceName}（改）` })
      .getByRole('button', { name: '删除', exact: true })
      .click()
    await expect(page.locator('.device-card', { hasText: deviceName })).toHaveCount(0)
  })

  test('375px 移动视口目标页响应式无横向溢出', async ({ page }) => {
    await login(page)
    await page.setViewportSize({ width: 375, height: 720 })
    await page.goto('/targets')
    await expect(page.getByRole('heading', { name: '目标管理' })).toBeVisible()
    await expectNoHorizontalOverflow(page, '目标页 375px')
    await page.screenshot({ path: `${EVIDENCE_DIR}/targets-mobile-375.png`, fullPage: true })
  })
})
