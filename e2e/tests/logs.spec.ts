import { expect, test, type Page } from '@playwright/test'

const PASSWORD = 'e2e-password-9'
const EVIDENCE_DIR = './evidence'

interface TailRequestRecord {
  service: string
  kind: string
  lines: number
}

interface FakeLogService {
  name: string
  kind: string
  description: string
}

const DEFAULT_LOG_SERVICES: FakeLogService[] = [
  { name: 'nginx.service', kind: 'systemd', description: 'A high performance web server' },
  { name: 'web', kind: 'docker', description: 'nginx:1.27（Up 2 hours）' },
]

declare global {
  interface Window {
    __logTailRequests?: TailRequestRecord[]
  }
}

async function login(page: Page) {
  await page.goto('/login')
  await page.getByPlaceholder('请输入用户名').fill('admin')
  await page.getByPlaceholder('请输入密码').fill(PASSWORD)
  await page.getByRole('button', { name: /登\s*录/ }).click()
  await expect(page.getByRole('heading', { name: /欢迎/ })).toBeVisible()
}

async function createDeviceViaApi(page: Page, name: string): Promise<{ id: number; agentToken: string }> {
  const response = await page.request.post('/api/collectors', {
    data: { name, tags: ['E2E'] },
  })
  expect(response.ok()).toBeTruthy()
  return (await response.json()) as { id: number; agentToken: string }
}

/** 进入采集器详情页（三期模块3：日志入口并入详情页，独立日志页已移除）。 */
async function openCollectorDetail(page: Page, name: string) {
  await page.getByRole('link', { name: '采集器' }).click()
  const card = page.locator('.collector-card', { hasText: name })
  await expect(card).toBeVisible()
  await card.getByRole('button', { name: '详情' }).click()
  await expect(page.getByRole('heading', { name })).toBeVisible()
}

/**
 * 页内假 agent：接入 /agent/ws 完成 auth 并保持在线。
 * 收到 logs.services.request → 回服务清单；收到 logs.tail.request → 回两行日志（1 error + 1 info），
 * 并把请求参数记录到 window.__logTailRequests 供断言 N 可调与查询参数正确。
 */
async function startLogsAgent(page: Page, token: string, services: FakeLogService[] = DEFAULT_LOG_SERVICES) {
  await page.evaluate(
    ({ token, services }) =>
      new Promise<void>((resolve, reject) => {
        window.__logTailRequests = []
        const ws = new WebSocket(`ws://${location.host}/agent/ws`)
        ws.onopen = () => {
          ws.send(JSON.stringify({ type: 'auth', seq: 1, payload: { token } }))
        }
        ws.onerror = () => reject(new Error('假 agent 连接失败'))
        ws.onmessage = (event) => {
          const envelope = JSON.parse(event.data as string)
          if (envelope.type === 'auth.ok') {
            resolve()
          } else if (envelope.type === 'logs.services.request') {
            ws.send(
              JSON.stringify({
                type: 'logs.services.response',
                seq: envelope.seq,
                payload: { services },
              }),
            )
          } else if (envelope.type === 'logs.tail.request') {
            window.__logTailRequests!.push(envelope.payload)
            ws.send(
              JSON.stringify({
                type: 'logs.tail.response',
                seq: envelope.seq,
                payload: {
                  lines: [
                    { ts: '2026-09-04T08:00:00.000Z', level: 'error', message: 'connect() failed (111: Connection refused)' },
                    { ts: '2026-09-04T08:00:01.000Z', level: 'info', message: 'Configuration file test is successful' },
                  ],
                },
              }),
            )
          }
        }
      }),
    { token, services },
  )
}

test.describe('日志查看（TOB-340 → 三期模块3 入口并入采集器详情）', () => {
  test.setTimeout(120_000)

  test('详情页自动加载服务清单并拉取尾部 N 行，关键字/级别过滤准确，N 可调（验收 1/2/3/4/5）', async ({ page }) => {
    await login(page)

    const deviceName = `日志验收机 ${Date.now()}`
    const device = await createDeviceViaApi(page, deviceName)
    // 假 agent 先在线（页内 /agent/ws 长连接，SPA 导航不断开），随后进采集器详情页
    await startLogsAgent(page, device.agentToken)

    // 详情页挂载即拉服务清单并默认选中首个服务（nginx.service）
    await openCollectorDetail(page, deviceName)
    const serviceSelect = page.locator('.log-controls select').first()
    await expect(serviceSelect).toContainText('nginx.service', { timeout: 15_000 })

    // 验收 1：显示尾部日志，内容与假 agent 上报一致（systemd 服务带来源标注）
    await expect(page.getByText('connect() failed (111: Connection refused)')).toBeVisible()
    await expect(page.getByText('Configuration file test is successful')).toBeVisible()
    await expect(page.locator('.log-line')).toHaveCount(2)

    // 验收 2：关键字过滤——含关键字的保留、不含的隐藏
    const keywordInput = page.locator('.log-keyword input')
    await keywordInput.fill('connect')
    await expect(page.locator('.log-line')).toHaveCount(1)
    await expect(page.getByText('connect() failed')).toBeVisible()
    await expect(page.getByText('Configuration file test is successful')).toBeHidden()
    await keywordInput.fill('不存在关键字')
    await expect(page.locator('.log-line')).toHaveCount(0)
    await expect(page.getByText('没有匹配过滤条件的日志行。')).toBeVisible()
    await keywordInput.fill('')

    // 验收 3：级别过滤——仅保留所选级别
    await page.getByRole('button', { name: /^error/ }).click()
    await expect(page.locator('.log-line')).toHaveCount(1)
    await expect(page.locator('.log-level-error')).toHaveCount(1)
    await page.getByRole('button', { name: /^info/ }).click()
    await expect(page.locator('.log-line')).toHaveCount(1)
    await expect(page.locator('.log-level-info')).toHaveCount(1)
    await page.getByRole('button', { name: /全部级别/ }).click()
    await expect(page.locator('.log-line')).toHaveCount(2)

    // 验收 4：N 可调——改为 500 后 agent 收到的请求带 lines=500
    await page.locator('select.log-lines').selectOption('500')
    await expect(page.locator('.log-line')).toHaveCount(2)
    const tailRequests = await page.evaluate(() => window.__logTailRequests ?? [])
    expect(tailRequests.length).toBeGreaterThanOrEqual(2)
    expect(tailRequests[0].lines).toBe(200)
    expect(tailRequests[tailRequests.length - 1].lines).toBe(500)
    expect(tailRequests[tailRequests.length - 1].service).toBe('nginx.service')
    expect(tailRequests[tailRequests.length - 1].kind).toBe('systemd')

    // 验收 5：拉取为只读按需进行——仅有手动刷新按钮，无自动轮询；假 agent 保持连接即零入站端口
    await expect(page.getByRole('button', { name: /刷新/ })).toBeVisible()
    await page.screenshot({ path: `${EVIDENCE_DIR}/logs-collector-detail.png`, fullPage: true })
  })

  test('离线设备给出明确错误提示（验收 5：不改变目标机状态）', async ({ page }) => {
    await login(page)
    // 不接 agent：设备离线
    const deviceName = `离线日志机 ${Date.now()}`
    await createDeviceViaApi(page, deviceName)

    await openCollectorDetail(page, deviceName)

    await expect(page.getByText('设备离线，无法获取日志')).toBeVisible({ timeout: 15_000 })
  })

  test('跨来源同名服务可分别选中，拉取 kind 与所选条目一致（阶段 1 审查问题 1）', async ({ page }) => {
    await login(page)

    const deviceName = `同名服务验收机 ${Date.now()}`
    const device = await createDeviceViaApi(page, deviceName)
    // systemd unit 与 docker 容器同名（容器名允许含 "."）：两个条目 name 相同、kind 不同
    await startLogsAgent(page, device.agentToken, [
      { name: 'nginx.service', kind: 'systemd', description: 'A high performance web server' },
      { name: 'nginx.service', kind: 'docker', description: 'nginx:1.27（Up 2 hours）' },
    ])

    await openCollectorDetail(page, deviceName)

    const serviceSelect = page.locator('.log-controls select').first()
    await expect(serviceSelect.locator('option')).toHaveCount(2)

    // 选中 docker 条目：发出的拉取请求 kind 必须是 docker
    await serviceSelect.selectOption({ label: 'nginx.service（docker · nginx:1.27（Up 2 hours））' })
    await expect(page.getByText('connect() failed (111: Connection refused)')).toBeVisible()
    let tailRequests = await page.evaluate(() => window.__logTailRequests ?? [])
    expect(tailRequests.length).toBeGreaterThanOrEqual(1)
    expect(tailRequests[tailRequests.length - 1]).toMatchObject({ service: 'nginx.service', kind: 'docker', lines: 200 })

    // 选回 systemd 条目：kind 跟随所选条目变化
    await serviceSelect.selectOption({ label: 'nginx.service（systemd · A high performance web server）' })
    await expect(page.getByText('connect() failed (111: Connection refused)')).toBeVisible()
    tailRequests = await page.evaluate(() => window.__logTailRequests ?? [])
    expect(tailRequests[tailRequests.length - 1]).toMatchObject({ service: 'nginx.service', kind: 'systemd' })
  })
})
