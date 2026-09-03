import { expect, test, type Page } from '@playwright/test'

const PASSWORD = 'e2e-password-9'
const EVIDENCE_DIR = './evidence'

interface ReportPayload {
  cpu: number
  mem: number
  disk: number
  netRx: number
  netTx: number
}

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

async function createDeviceViaApi(page: Page, name: string): Promise<{ id: number; agentToken: string }> {
  const response = await page.request.post('/api/devices', {
    data: { name, tags: ['E2E'] },
  })
  expect(response.ok()).toBeTruthy()
  return (await response.json()) as { id: number; agentToken: string }
}

/**
 * 模拟 agent：在页面内建立 WebSocket（同源），auth 握手后按间隔发送 metrics.report。
 * 浏览器自带 WebSocket，无需额外依赖。
 */
async function reportMetricsViaWebSocket(page: Page, token: string, reports: ReportPayload[], intervalMs = 200) {
  const result = await page.evaluate(
    async ({ token, reports, intervalMs }) => {
      const ws = new WebSocket(`ws://${location.host}/agent/ws`)
      const received: { type: string }[] = []
      ws.onmessage = (event) => received.push(JSON.parse(event.data as string))
      await new Promise<void>((resolve, reject) => {
        ws.onopen = () => resolve()
        ws.onerror = () => reject(new Error('WS 连接失败'))
      })

      ws.send(JSON.stringify({ type: 'auth', seq: 1, payload: { token } }))
      const waitType = (type: string) =>
        new Promise<void>((resolve, reject) => {
          const started = Date.now()
          const timer = setInterval(() => {
            if (received.some((m) => m.type === type)) {
              clearInterval(timer)
              resolve()
            } else if (Date.now() - started > 10_000) {
              clearInterval(timer)
              reject(new Error(`等待 ${type} 超时`))
            }
          }, 50)
        })
      await waitType('auth.ok')

      let seq = 1
      for (const payload of reports) {
        ws.send(JSON.stringify({ type: 'metrics.report', seq: ++seq, payload }))
        await new Promise((resolve) => setTimeout(resolve, intervalMs))
      }
      ws.close()
      return reports.length
    },
    { token, reports, intervalMs },
  )
  return result
}

test.describe('指标曲线（TOB-338）', () => {
  test.setTimeout(120_000)

  test('模拟 agent 上报 → 指标页渲染四类曲线 → 切换设备曲线对应（验收 1/5）', async ({ page }) => {
    await login(page)

    // 两台“负载不同”的设备（验收 5：曲线与所选设备对应）
    const lowName = `低负载机 ${Date.now()}`
    const highName = `高负载机 ${Date.now()}`
    const lowLoad = await createDeviceViaApi(page, lowName)
    const highLoad = await createDeviceViaApi(page, highName)

    const lowReports = [10, 14, 12, 16, 11].map((cpu) => ({
      cpu,
      mem: 40,
      disk: 55,
      netRx: 102_400,
      netTx: 51_200,
    }))
    const highReports = [88, 93, 90, 95, 91].map((cpu) => ({
      cpu,
      mem: 70,
      disk: 80,
      netRx: 1_048_576,
      netTx: 524_288,
    }))

    expect(await reportMetricsViaWebSocket(page, lowLoad.agentToken, lowReports)).toBe(5)
    expect(await reportMetricsViaWebSocket(page, highLoad.agentToken, highReports)).toBe(5)

    // 指标页：明确选择低负载设备，最近 1 小时 → 明细口径
    await page.getByRole('link', { name: '指标曲线' }).click()
    await expect(page.getByRole('heading', { name: '指标曲线' })).toBeVisible()
    const deviceSelect = page.locator('select.control-select')
    await deviceSelect.selectOption({ label: lowName })
    await expect(page.getByText('当前口径')).toContainText('明细')

    const cpuCard = page.locator('.chart-card', { hasText: 'CPU 使用率' })
    await expect(cpuCard.locator('svg polyline')).toHaveCount(1)
    await expect(page.locator('.chart-card', { hasText: '内存使用率' }).locator('svg polyline')).toHaveCount(1)
    await expect(page.locator('.chart-card', { hasText: '磁盘使用率' }).locator('svg polyline')).toHaveCount(1)
    // 网络图：下行 + 上行两条曲线
    await expect(page.locator('.chart-card', { hasText: '网络流量' }).locator('svg polyline')).toHaveCount(2)
    await page.screenshot({ path: `${EVIDENCE_DIR}/metrics-low-load.png`, fullPage: true })

    // 切换到高负载设备：曲线数值随之切换（口径与设备对应）
    await deviceSelect.selectOption({ label: highName })
    await expect(page.locator('.chart-card', { hasText: 'CPU 使用率' }).locator('.chart-latest')).toContainText(/9\d%/)
    await page.screenshot({ path: `${EVIDENCE_DIR}/metrics-high-load.png`, fullPage: true })

    await expectNoHorizontalOverflow(page, '指标曲线 1280px')
  })

  test('长跨度切聚合 + 375px 响应式', async ({ page }) => {
    await login(page)

    const device = await createDeviceViaApi(page, `聚合口径机 ${Date.now()}`)
    await reportMetricsViaWebSocket(page, device.agentToken, [
      { cpu: 20, mem: 50, disk: 45, netRx: 2048, netTx: 1024 },
    ])

    await page.getByRole('link', { name: '指标曲线' }).click()
    await expect(page.getByRole('heading', { name: '指标曲线' })).toBeVisible()

    // 最近 30 天 → 天聚合口径（验收 3：聚合视图正常渲染且口径标明）
    await page.getByRole('button', { name: '最近 30 天' }).click()
    await expect(page.getByText('当前口径')).toContainText('天聚合')
    await page.screenshot({ path: `${EVIDENCE_DIR}/metrics-daily-granularity.png`, fullPage: true })

    // 375px 响应式（移动端视口）
    await page.setViewportSize({ width: 375, height: 812 })
    await expect(page.getByRole('heading', { name: '指标曲线' })).toBeVisible()
    await page.screenshot({ path: `${EVIDENCE_DIR}/metrics-mobile-375.png`, fullPage: true })
    await expectNoHorizontalOverflow(page, '指标曲线 375px')
  })
})
