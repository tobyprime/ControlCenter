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

test.describe('采集器管理（TOB-361 → 三期模块3 统一采集器）', () => {
  test('新建 push 采集器 → 签发 token → 列表离线展示 → 编辑 → 重置 token → 删除', async ({ page }) => {
    await login(page)

    const deviceName = `E2E 测试机 ${Date.now()}`

    await page.getByRole('link', { name: '采集器' }).click()
    await expect(page.getByRole('heading', { name: '采集器' })).toBeVisible()

    // 新建设备（push：轮询地址留空 = agent 上报模式）
    await page.getByRole('button', { name: '新建采集器' }).click()
    await page.getByPlaceholder('如：机房A 边缘网关').fill(deviceName)
    await page.getByPlaceholder('如：机房A，网关，内网').fill('机房E2E，冒烟')
    await page.getByRole('button', { name: '保存' }).click()

    // token 只显示一次
    await expect(page.getByText(`「${deviceName}」的 agent token`)).toBeVisible()
    const tokenText = await page.locator('code.token-value').innerText()
    expect(tokenText.startsWith('dpk_')).toBe(true)
    await page.screenshot({ path: `${EVIDENCE_DIR}/collectors-token-dialog.png`, fullPage: true })
    await page.getByRole('button', { name: '我已保存，关闭' }).click()

    // 列表：离线状态 + 标签
    const card = page.locator('.collector-card', { hasText: deviceName })
    await expect(card).toBeVisible()
    await expect(card.getByText('离线')).toBeVisible()
    await expect(card.getByText('机房E2E')).toBeVisible()
    await expect(card.getByText('冒烟')).toBeVisible()
    await page.screenshot({ path: `${EVIDENCE_DIR}/collectors-list-offline.png`, fullPage: true })

    // 编辑
    await card.getByRole('button', { name: '编辑' }).click()
    await page.getByPlaceholder('如：机房A 边缘网关').fill(`${deviceName}（改）`)
    await page.getByPlaceholder('如：机房A，网关，内网').fill('机房E2E')
    await page.getByRole('button', { name: '保存' }).click()
    await expect(page.locator('.collector-card', { hasText: `${deviceName}（改）` })).toBeVisible()

    // 重置 token：旧 token 提示、新 token 展示
    page.once('dialog', (dialog) => dialog.accept())
    await page
      .locator('.collector-card', { hasText: `${deviceName}（改）` })
      .getByRole('button', { name: '重置 Token' })
      .click()
    await expect(page.getByText(`「${deviceName}（改）」的 agent token`)).toBeVisible()
    const newToken = await page.locator('code.token-value').innerText()
    expect(newToken).not.toBe(tokenText)
    await page.getByRole('button', { name: '我已保存，关闭' }).click()

    // 删除（确认对话框）
    page.once('dialog', (dialog) => dialog.accept())
    await page
      .locator('.collector-card', { hasText: `${deviceName}（改）` })
      .getByRole('button', { name: '删除', exact: true })
      .click()
    await expect(page.locator('.collector-card', { hasText: deviceName })).toHaveCount(0)
  })

  test('新建 pull 采集器 → 轮询自动探测 → 详情轮询配置与指标入口 → 删除', async ({ page }) => {
    await login(page)
    await page.getByRole('link', { name: '采集器' }).click()
    await page.getByRole('button', { name: '新建采集器' }).click()

    // 填轮询地址 = pull 采集器（面板定时 GET，无需 agent），出现间隔与提取映射表单
    const serviceName = `E2E MC 服务 ${Date.now()}`
    await page.getByPlaceholder('如：机房A 边缘网关').fill(serviceName)
    await page.getByPlaceholder('如：https://mc.zenoxs.cn/api/status（留空 = agent 上报模式）').fill('http://127.0.0.1:5099/healthz')
    await page.getByRole('button', { name: '+ 添加映射' }).click()
    await page.getByPlaceholder('指标名，如 mc.players').fill('e2e.status')
    await page.getByPlaceholder('JSONPath，如 $.players.length()').fill('$.status')
    await page.locator('.mapping-row select').selectOption('string')
    await page.screenshot({ path: `${EVIDENCE_DIR}/pull-collector-create-form.png`, fullPage: true })
    await page.getByRole('button', { name: '保存' }).click()

    // pull 采集器不签发 token（无需 agent），直接出现在列表
    const card = page.locator('.collector-card', { hasText: serviceName })
    await expect(card).toBeVisible()
    await expect(page.getByText(/的 agent token/)).toHaveCount(0)
    await expect(card.getByRole('button', { name: '重置 Token' })).toHaveCount(0)

    // 首轮轮询即探测：状态转「正常」，最近探测时间刷新
    await expect(card.getByText('正常')).toBeVisible({ timeout: 20000 })

    // 详情：轮询配置卡片 + 指标总览（服务状态 / 响应时间 / 提取指标）
    await card.getByRole('button', { name: '详情' }).click()
    await expect(page.getByRole('heading', { name: '轮询配置' })).toBeVisible()
    await expect(page.locator('code', { hasText: 'http://127.0.0.1:5099/healthz' })).toBeVisible()
    const overview = page.locator('.overview-table')
    await expect(overview.locator('code', { hasText: 'status' }).first()).toBeVisible()
    await expect(overview.getByText('服务状态')).toBeVisible()
    await expect(overview.getByText('响应时间')).toBeVisible()
    await page.screenshot({ path: `${EVIDENCE_DIR}/pull-collector-detail.png`, fullPage: true })

    // 提取指标与状态指标均可直接配置告警规则（规则类型随指标值类型过滤）
    await page.getByRole('button', { name: '新建规则' }).click()
    await expect(page.locator('.dialog select').first()).toContainText('e2e.status')
    await page.getByRole('button', { name: '取消' }).click()

    // 删除清理
    await page.getByRole('button', { name: '返回采集器列表' }).click()
    page.once('dialog', (dialog) => dialog.accept())
    await page.locator('.collector-card', { hasText: serviceName }).getByRole('button', { name: '删除', exact: true }).click()
    await expect(page.locator('.collector-card', { hasText: serviceName })).toHaveCount(0)
  })

  test('375px 移动视口采集器页响应式无横向溢出', async ({ page }) => {
    await login(page)
    await page.setViewportSize({ width: 375, height: 720 })
    await page.goto('/collectors')
    await expect(page.getByRole('heading', { name: '采集器' })).toBeVisible()
    await expectNoHorizontalOverflow(page, '采集器页 375px')
    await page.screenshot({ path: `${EVIDENCE_DIR}/collectors-mobile-375.png`, fullPage: true })
  })
})
