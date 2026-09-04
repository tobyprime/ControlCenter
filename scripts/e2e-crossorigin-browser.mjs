// 跨站浏览器端到端测试体（由 scripts/e2e-crossorigin-browser.sh 拉起，环境变量传入拓扑）：
// 模拟 Cloudflare Pages 独立前端（https://panel.test:5173）+ 独立后端（https://api.test:5443）
// 两个不同站点，在真实 Chrome 中验证：登录会话 Cookie 跨站存储（SameSite=None; Secure）、
// 后续所有 /api 请求携带 Cookie、设备/指标/终端/日志各页可用。
// 审查问题 1（TOB-357 阶段 1）的回归锚点：fetch 未带凭据时本测试必红。
import { createRequire } from 'node:module'
import { existsSync, statSync, createReadStream, readFileSync } from 'node:fs'
import { extname, join } from 'node:path'
import https from 'node:https'

const require = createRequire(import.meta.url)

const PANEL_ORIGIN = process.env.PANEL_ORIGIN
const API_ORIGIN = process.env.API_ORIGIN
const PANEL_PORT = Number(process.env.PANEL_PORT)
const DIST_DIR = process.env.DIST_DIR
const TLS_CERT = process.env.TLS_CERT
const TLS_KEY = process.env.TLS_KEY
const DEV_NAME = process.env.DEV_NAME

let failures = 0
function check(cond, message) {
  if (cond) {
    console.log(`  ✓ ${message}`)
  } else {
    failures += 1
    console.error(`  ✗ ${message}`)
  }
}

// ---- 解析 playwright：全局安装优先，降级脚本预装的 playwright-core ----
function loadPlaywright() {
  try {
    return require('playwright')
  } catch {
    return require(join(process.env.PW_CORE_DIR ?? '', 'node_modules', 'playwright-core'))
  }
}

const executablePath = process.env.CHROME_PATH
if (!executablePath || !existsSync(executablePath)) {
  console.error(`✗ 未找到 Chrome：请安装 google-chrome 或以 CHROME_PATH 指定（当前：${executablePath}）`)
  process.exit(2)
}

// ---- 静态托管前端 dist（HTTPS，SPA 回退 index.html，等价 Cloudflare Pages）----
const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript',
  '.css': 'text/css',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.woff2': 'font/woff2',
  '.json': 'application/json',
}
const staticServer = https.createServer(
  { cert: readFileSync(TLS_CERT), key: readFileSync(TLS_KEY) },
  (req, res) => {
    const pathname = decodeURIComponent(new URL(req.url, 'http://localhost').pathname)
    let file = join(DIST_DIR, pathname === '/' ? 'index.html' : pathname.slice(1))
    if (!existsSync(file) || statSync(file).isDirectory()) {
      file = join(DIST_DIR, 'index.html') // SPA 回退（/_redirects 等价行为）
    }
    res.setHeader('Cache-Control', 'no-store')
    res.setHeader('Content-Type', MIME[extname(file)] ?? 'application/octet-stream')
    createReadStream(file).pipe(res)
  },
)
await new Promise((resolve) => staticServer.listen(PANEL_PORT, '127.0.0.1', resolve))
console.log(`== 前端静态源已就绪：${PANEL_ORIGIN}`)

// ---- 等后端就绪 ----
for (let i = 0; i < 30; i += 1) {
  try {
    const r = await fetch(`${API_ORIGIN}/healthz`)
    if (r.ok) break
  } catch {
    // 后端未就绪，重试
  }
  await new Promise((r) => setTimeout(r, 1000))
}
console.log(`== 后端就绪：${API_ORIGIN}`)

const { chromium } = loadPlaywright()
const browser = await chromium.launch({
  executablePath,
  headless: true,
  args: ['--no-sandbox', '--disable-dev-shm-usage', '--no-proxy-server'],
})
try {
  const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1440, height: 900 } })
  const page = await context.newPage()

  let loggedIn = false
  const apiCalls = []
  page.on('response', async (r) => {
    if (r.url().startsWith(`${API_ORIGIN}/api/`)) {
      // request.headers() 不含 cookie 头，需用 allHeaders()
      const headers = await r.request().allHeaders()
      apiCalls.push({
        url: r.url(),
        status: r.status(),
        cookie: headers['cookie'] ?? '',
        phase: loggedIn ? 'post' : 'pre',
      })
    }
  })
  const consoleErrors = []
  page.on('pageerror', (e) => consoleErrors.push(String(e)))

  // ---- 1. 未登录 → 落在登录页 ----
  console.log('== 1. 打开前端（跨站独立源）→ 应回落到登录页')
  await page.goto(`${PANEL_ORIGIN}/`, { waitUntil: 'networkidle' })
  await page.waitForURL((u) => u.pathname.endsWith('/login'), { timeout: 15000 })
  check(page.url().includes('/login'), '未登录被门禁拦截到 /login')

  // ---- 2. 登录（跨站 POST，凭据语义关键路径）----
  try {
  console.log('== 2. 跨站登录 → 会话 Cookie 必须被浏览器存储到后端域')
  await page.fill('input[name="username"]', 'admin')
  await page.fill('input[name="password"]', process.env.ADMIN_PASS)
  await Promise.all([
    page.waitForResponse((r) => r.url().endsWith('/api/auth/login') && r.request().method() === 'POST'),
    page.click('button.login-button'),
  ])
  await page.waitForURL((u) => !u.pathname.endsWith('/login'), { timeout: 15000 })
  loggedIn = true
  const cookies = await context.cookies(API_ORIGIN)
  const session = cookies.find((c) => c.name === 'device_panel_session')
  check(!!session, '后端域（api.test）Cookie 罐中存在会话 Cookie device_panel_session')
  check(
    session?.sameSite === 'None',
    `会话 Cookie SameSite=None（实际 ${session?.sameSite}），跨站请求才会携带`,
  )
  check(session?.secure === true, '会话 Cookie 带 Secure 标记')

  // ---- 3. 设备管理页：列表请求携带 Cookie 且 200 ----
  console.log('== 3. 设备管理页（REST 需携带会话 Cookie）')
  await page.goto(`${PANEL_ORIGIN}/devices`, { waitUntil: 'networkidle' })
  await page.waitForFunction((name) => document.body.innerText.includes(name), DEV_NAME, { timeout: 15000 })
  check(true, `设备「${DEV_NAME}」出现在列表`)

  // ---- 4. 指标曲线页：series 200 且有数据点 ----
  console.log('== 4. 指标曲线页（series 请求 + 数据点）')
  await page.goto(`${PANEL_ORIGIN}/metrics`, { waitUntil: 'networkidle' })
  let seriesPoints = -1
  const seriesDeadline = Date.now() + 90_000
  while (seriesPoints < 1 && Date.now() < seriesDeadline) {
    const r = await Promise.race([
      page.waitForResponse((r) => r.url().includes('/api/metrics/') && r.url().includes('/series'), { timeout: 20_000 }).catch(() => null),
      new Promise((r) => setTimeout(r, 21_000)),
    ])
    if (r) {
      try {
        seriesPoints = (await r.json()).points.length
      } catch {
        seriesPoints = -1
      }
    }
  }
  check(seriesPoints >= 1, `指标 series 返回且有数据点（实际 ${seriesPoints} 个）`)

  // ---- 5. Web 终端页：WS 握手（浏览器自动携带 Cookie）+ shell 往返 ----
  console.log('== 5. Web 终端页（WS 握手带凭据 + shell 回显）')
  await page.goto(`${PANEL_ORIGIN}/terminal`, { waitUntil: 'networkidle' })
  await page.selectOption('select.device-select', { label: `${DEV_NAME}（在线）` })
  const wsOpened = page.waitForEvent('websocket', { timeout: 20_000 })
  await page.click('button:has-text("打开终端")')
  const ws = await wsOpened
  await page.waitForFunction(() => document.body.innerText.includes('已连上'), null, { timeout: 20_000 })
  check(ws.isClosed() === false, '终端 WebSocket 已建立（握手通过面板认证）')
  await page.click('.xterm-screen').catch(() => {})
  await page.keyboard.type('echo cross-e2e-ok')
  await page.keyboard.press('Enter')
  await page.waitForFunction(() => document.body.innerText.includes('cross-e2e-ok'), null, { timeout: 20_000 })
  check(true, '终端 shell 命令往返（echo 回显）')

  // ---- 6. 日志页：服务清单 REST 200 ----
  console.log('== 6. 日志查看页（services 请求）')
  await page.goto(`${PANEL_ORIGIN}/logs`, { waitUntil: 'networkidle' })
  const servicesCall = apiCalls.find((c) => c.url.includes('/logs/services'))
  check(!!servicesCall && servicesCall.status === 200, `日志服务清单返回 200（实际 ${servicesCall ? servicesCall.status : '无请求'}）`)

  // ---- 7. 告警配置页（alerts.ts 覆盖）----
  console.log('== 7. 告警配置页（settings/thresholds 请求）')
  await page.goto(`${PANEL_ORIGIN}/alerts`, { waitUntil: 'networkidle' })
  const alertsCall = apiCalls.filter((c) => c.url.includes('/api/alerts/'))
  check(alertsCall.length >= 1 && alertsCall.every((c) => c.status === 200), `告警配置加载（${alertsCall.length} 个请求，全部 2xx）`)

  // ---- 8. 登录后的所有 /api 请求：全部携带会话 Cookie 且无 4xx/5xx ----
  console.log('== 8. 登录后 API 请求总核对')
  const postLogin = apiCalls.filter((c) => c.phase === 'post')
  check(postLogin.length >= 6, `登录后捕获到足量 API 请求（实际 ${postLogin.length} 个）`)
  const noCookie = postLogin.filter((c) => !c.cookie.includes('device_panel_session'))
  check(noCookie.length === 0, `所有登录后请求携带会话 Cookie${noCookie.length ? `（未携带：${noCookie.map((c) => c.url).join(', ')}）` : ''}`)
  const failed = postLogin.filter((c) => c.status >= 400)
  check(failed.length === 0, `所有登录后请求 2xx/3xx${failed.length ? `（失败：${failed.map((c) => `${c.url}→${c.status}`).join(', ')}）` : ''}`)
  check(consoleErrors.length === 0, `无前端页面异常${consoleErrors.length ? `：${consoleErrors.join(' | ')}` : ''}`)

  await page.screenshot({ path: join(process.env.WORK_DIR ?? '/tmp', 'crossorigin-e2e.png'), fullPage: false })
  } catch (error) {
    failures += 1
    console.error(`  ✗ 流程中断：${error instanceof Error ? error.message.split('\n')[0] : error}`)
    await page.screenshot({ path: join(process.env.WORK_DIR ?? '/tmp', 'crossorigin-e2e-failed.png'), fullPage: false }).catch(() => {})
  }
} finally {
  await browser.close()
  staticServer.close()
}

if (failures > 0) {
  console.error(`== 跨站浏览器端到端：FAIL（${failures} 项未过）`)
  process.exit(1)
}
console.log('== 跨站浏览器端到端：PASS（登录 → 设备/指标/终端/日志 全链路，会话 Cookie 跨站存储与携带）')
