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

async function createDeviceViaApi(page: Page, name: string): Promise<{ id: number; agentToken: string }> {
  const response = await page.request.post('/api/targets', {
    data: { name, tags: ['E2E'] },
  })
  expect(response.ok()).toBeTruthy()
  return (await response.json()) as { id: number; agentToken: string }
}

interface EchoAgentResult {
  opened: boolean
  closedSent: boolean
}

/**
 * 页面内假 agent：接入 /agent/ws 完成 auth 并保持在线。
 * 收到 term.open → 回 term.opened；收到 term.input → 回显 term.output 后发送 term.closed（模拟 shell 退出）。
 * 由测试代码向终端 UI 输入命令（同页面两个 WS 连接互不干扰）。
 */
async function startEchoAgent(page: Page, token: string): Promise<EchoAgentResult> {
  return await page.evaluate(
    (token) =>
      new Promise<EchoAgentResult>((resolve, reject) => {
        const ws = new WebSocket(`ws://${location.host}/agent/ws`)
        let opened = false
        ws.onopen = () => {
          ws.send(JSON.stringify({ type: 'auth', seq: 1, payload: { token } }))
        }
        ws.onerror = () => reject(new Error('假 agent 连接失败'))
        ws.onmessage = (event) => {
          const envelope = JSON.parse(event.data as string)
          if (envelope.type === 'auth.ok') {
            // agent 在线，等待面板的 term.open
          } else if (envelope.type === 'term.open') {
            opened = true
            ws.send(JSON.stringify({ type: 'term.opened', seq: envelope.seq, payload: { sessionId: envelope.payload.sessionId } }))
          } else if (envelope.type === 'term.input') {
            const text = atob(envelope.payload.data)
            ws.send(JSON.stringify({ type: 'term.output', seq: envelope.seq, payload: { sessionId: envelope.payload.sessionId, data: btoa(text) } }))
            ws.send(JSON.stringify({ type: 'term.closed', seq: envelope.seq, payload: { sessionId: envelope.payload.sessionId } }))
            setTimeout(() => resolve({ opened, closedSent: true }), 300)
          }
        }
      }),
    token,
  )
}

test.describe('Web 终端（TOB-339）', () => {
  test.setTimeout(120_000)

  test('终端页打开会话（假 agent 回显）→ 留痕可查（验收 1/2/5）', async ({ page }) => {
    await login(page)

    const deviceName = `终端验收机 ${Date.now()}`
    const device = await createDeviceViaApi(page, deviceName)

    // 假 agent 先在线（页内 /agent/ws 长连接），准备好 open→回显→closed 全链路
    const echoPromise = startEchoAgent(page, device.agentToken)

    // 终端页：选择在线设备并打开终端
    await page.getByRole('link', { name: 'Web 终端' }).click()
    await expect(page.getByRole('heading', { name: 'Web 终端' })).toBeVisible()

    const deviceSelect = page.locator('select.device-select')
    await deviceSelect.selectOption({ label: `${deviceName}（在线）` })
    await page.getByRole('button', { name: '打开终端' }).click()
    await expect(page.locator('.status-line')).toContainText('已连上')

    // 向终端键入命令（整段粘贴 → 单帧输入，xterm 获得焦点后经键盘事件到浏览器 WS）
    await page.locator('.term-host').click()
    await page.keyboard.insertText('echo e2e_ok\n')

    // 假 agent 回显 output 后发送 closed（shell 退出语义）
    const echo = await echoPromise
    expect(echo.opened, '假 agent 收到 term.open 并回 term.opened').toBeTruthy()
    expect(echo.closedSent, '假 agent 收到命令输入').toBeTruthy()

    // 浏览器收到 closed：状态行提示会话结束（回显内容由留痕输出档断言）
    await expect(page.locator('.status-line')).toContainText('会话已结束')
    await page.screenshot({ path: `${EVIDENCE_DIR}/terminal-session.png`, fullPage: true })

    // 留痕查询：会话可见、命令留档可查（验收 2）
    const sessionsResponse = await page.request.get('/api/terminal/sessions')
    expect(sessionsResponse.ok()).toBeTruthy()
    const sessions = (await sessionsResponse.json()) as { id: string; deviceName: string }[]
    const session = sessions.find((s) => s.deviceName === deviceName)
    expect(session, '留痕中有本次终端会话').toBeTruthy()

    const recordsResponse = await page.request.get(`/api/terminal/sessions/${session!.id}/records`)
    expect(recordsResponse.ok()).toBeTruthy()
    const records = (await recordsResponse.json()) as { direction: string; data: string }[]
    const inputText = records.filter((r) => r.direction === 'input').map((r) => r.data).join('')
    const outputText = records.filter((r) => r.direction === 'output').map((r) => r.data).join('')
    expect(inputText.includes('echo e2e_ok'), '输入留痕含命令').toBeTruthy()
    expect(outputText.includes('echo e2e_ok'), '输出留痕含回显').toBeTruthy()

    await page.getByRole('link', { name: '终端留痕' }).click()
    await expect(page.getByRole('heading', { name: '终端留痕' })).toBeVisible()
    await expect(page.locator('.session-item', { hasText: deviceName }).first()).toBeVisible()
    await page.locator('.session-item', { hasText: deviceName }).first().click()
    await expect(page.locator('.record-line').first()).toBeVisible()
    await page.screenshot({ path: `${EVIDENCE_DIR}/terminal-records.png`, fullPage: true })

    await expectNoHorizontalOverflow(page, '终端留痕 1280px')
  })

  test('终端页 375px 响应式', async ({ page }) => {
    await login(page)

    await page.getByRole('link', { name: 'Web 终端' }).click()
    await expect(page.getByRole('heading', { name: 'Web 终端' })).toBeVisible()

    await page.setViewportSize({ width: 375, height: 812 })
    await expect(page.getByRole('heading', { name: 'Web 终端' })).toBeVisible()
    await page.screenshot({ path: `${EVIDENCE_DIR}/terminal-mobile-375.png`, fullPage: true })
    await expectNoHorizontalOverflow(page, 'Web 终端 375px')
  })
})
