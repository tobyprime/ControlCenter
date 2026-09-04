import { mkdtempSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { defineConfig } from '@playwright/test'

// E2E 验证针对已构建的内嵌前端：webServer 以 dotnet 启动后端（wwwroot 为前端产物）
// DataDir 每次运行独立（mktemp）：避免上一次运行残留的设备/告警配置破坏测试隔离
// 端口可用 E2E_PORT 覆盖（默认 5099）：runner 宿主网络共享，多个工作区并行跑 e2e 时避免端口冲突
const port = process.env.E2E_PORT ?? '5099'
const baseURL = `http://127.0.0.1:${port}`
const dataDir = mkdtempSync(join(tmpdir(), 'device-panel-e2e-'))

export default defineConfig({
  testDir: './tests',
  timeout: 30_000,
  use: {
    baseURL,
    locale: 'zh-CN',
    viewport: { width: 1280, height: 800 },
    screenshot: 'only-on-failure',
  },
  webServer: {
    command: `dotnet run --project ../src/DevicePanel.Web --no-build --urls ${baseURL}`,
    url: `${baseURL}/healthz`,
    reuseExistingServer: false,
    timeout: 60_000,
    env: {
      DevicePanel__Auth__InitialPassword: 'e2e-password-9',
      DevicePanel__DataDir: dataDir,
      ASPNETCORE_ENVIRONMENT: 'Development',
    },
  },
  projects: [
    {
      name: 'chromium',
      use: { browserName: 'chromium' },
    },
  ],
})
