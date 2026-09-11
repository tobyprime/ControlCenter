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

async function createCollectorViaApi(page: Page, name: string): Promise<{ id: number; agentToken: string }> {
  const response = await page.request.post('/api/collectors', { data: { name, tags: [] } })
  expect(response.ok()).toBeTruthy()
  return (await response.json()) as { id: number; agentToken: string }
}

/** 模拟 agent 经 WS 上报一次指标，让详情页指标总览出现「看曲线」行内按钮 */
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

test.describe('F7 行内操作热区 ≥40×40（TOB-408）', () => {
  // 机器校验：dp-touch-inline 的命中区（本体矩形按 ::before inset 外扩）四边均 ≥40px。
  // ::before 取的是 computed inset，伪元素本身没有布局盒，直接按外扩几何算
  async function assertTouchTargets(page: Page, label: string) {
    const results = await page.evaluate(() => {
      return [...document.querySelectorAll('.dp-touch-inline')].map((el) => {
        const rect = el.getBoundingClientRect()
        const style = getComputedStyle(el, '::before')
        const top = parseFloat(style.top || '0')
        const left = parseFloat(style.left || '0')
        return {
          text: (el.textContent || '').trim().slice(0, 12),
          width: rect.width - left * 2,
          height: rect.height - top * 2,
          visible: rect.width > 0 && rect.height > 0,
        }
      })
    })
    const targets = results.filter((item) => item.visible)
    expect(targets.length, `${label} 应存在行内操作按钮`).toBeGreaterThan(0)
    for (const target of targets) {
      expect(target.width, `${label}「${target.text}」命中区宽`).toBeGreaterThanOrEqual(40)
      expect(target.height, `${label}「${target.text}」命中区高`).toBeGreaterThanOrEqual(40)
    }
  }

  test('告警规则行内按钮命中区 ≥40×40', async ({ page }) => {
    await login(page)
    await page.goto('/alerts')
    await expect(page.locator('.card', { hasText: '规则列表' })).toBeVisible()
    await assertTouchTargets(page, '告警页')
    await page.screenshot({ path: `${EVIDENCE_DIR}/f7-alerts-hotzone.png`, fullPage: true })
  })

  test('指标曲线与采集器详情行内按钮命中区 ≥40×40', async ({ page }) => {
    await login(page)
    await page.goto('/metrics')
    // 指标注册表（行内删除按钮只出现在自定义指标行）默认收起：展开并注册一个自定义指标后再校验
    await page.getByRole('button', { name: '注册新指标' }).click()
    await expect(page.locator('.register-card')).toBeVisible()
    await page.getByPlaceholder('如 temp.cpu / player.count').fill('temp.cpu')
    await page.getByPlaceholder('如 CPU 温度').fill('CPU 温度')
    await page.getByPlaceholder('如 °C、%、B/s').fill('°C')
    await page.locator('.register-card').getByRole('button', { name: '注册', exact: true }).click()
    await expect(page.locator('.register-card').getByRole('button', { name: '删除' })).toBeVisible()
    await assertTouchTargets(page, '指标曲线页')

    await page.goto('/collectors')
    const collector = await createCollectorViaApi(page, '热区走查机 1')
    await reportOnceViaWebSocket(page, collector.agentToken)
    await page.reload()
    await page.locator('.collector-card', { hasText: '热区走查机 1' }).getByRole('button', { name: '详情' }).click()
    await page.waitForURL(/\/collectors\/\d+/)
    await expect(page.locator('.card').first()).toBeVisible()
    // 指标总览异步加载完成、行内「看曲线」按钮渲染后再做命中区校验
    await expect(page.getByRole('button', { name: '看曲线' }).first()).toBeVisible()
    await assertTouchTargets(page, '采集器详情页')
    await page.screenshot({ path: `${EVIDENCE_DIR}/f7-collector-hotzone.png`, fullPage: true })
  })
})

test.describe('移动端 375 轻量走查（TOB-408）', () => {
  test.use({ viewport: { width: 375, height: 812 } })

  test('登录/导航/主页/告警页 375 视口可用并留档截图', async ({ page }) => {
    // 登录页：无横向滚动
    await page.goto('/login')
    await expect(page.getByPlaceholder('请输入用户名')).toBeVisible()
    expect(await getHorizontalOverflowPx(page)).toBe(0)
    await page.screenshot({ path: `${EVIDENCE_DIR}/mobile-375-login.png`, fullPage: true })

    // 登录后主页：F10 五卡纵向堆叠，无横向溢出
    await login(page)
    await expect(page.locator('[data-card-type]').first()).toBeVisible()
    expect(await getHorizontalOverflowPx(page)).toBe(0)
    await page.screenshot({ path: `${EVIDENCE_DIR}/mobile-375-home.png`, fullPage: true })

    // 导航在窄视口可用（侧栏/抽屉形态均可，关键是导航入口可见可点）
    const navToggle = page.getByRole('button', { name: /菜单|导航/ })
    if ((await navToggle.count()) > 0) {
      await navToggle.first().click()
    }
    await page.screenshot({ path: `${EVIDENCE_DIR}/mobile-375-nav.png`, fullPage: false })

    // 告警页：规则列表可见，行内按钮热区在窄屏同样 ≥40×40
    await page.goto('/alerts')
    await expect(page.locator('.card', { hasText: '规则列表' })).toBeVisible()
    const targets = await page.evaluate(() =>
      [...document.querySelectorAll('.dp-touch-inline')].map((el) => {
        const rect = el.getBoundingClientRect()
        const style = getComputedStyle(el, '::before')
        return {
          width: rect.width - parseFloat(style.left || '0') * 2,
          height: rect.height - parseFloat(style.top || '0') * 2,
        }
      }),
    )
    for (const target of targets) {
      expect(target.width).toBeGreaterThanOrEqual(40)
      expect(target.height).toBeGreaterThanOrEqual(40)
    }
    await page.screenshot({ path: `${EVIDENCE_DIR}/mobile-375-alerts.png`, fullPage: true })
  })
})

async function getHorizontalOverflowPx(page: Page): Promise<number> {
  return page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
}
