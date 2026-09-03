import { defineConfig } from '@playwright/test'

// E2E 验证针对已构建的内嵌前端：webServer 以 dotnet 启动后端（wwwroot 为前端产物）
export default defineConfig({
  testDir: './tests',
  timeout: 30_000,
  use: {
    baseURL: 'http://127.0.0.1:5099',
    locale: 'zh-CN',
    viewport: { width: 1280, height: 800 },
    screenshot: 'only-on-failure',
  },
  webServer: {
    command:
      'dotnet run --project ../src/DevicePanel.Web --no-build --urls http://127.0.0.1:5099',
    url: 'http://127.0.0.1:5099/healthz',
    reuseExistingServer: false,
    timeout: 60_000,
    env: {
      DevicePanel__Auth__InitialPassword: 'e2e-password-9',
      DevicePanel__DataDir: '/tmp/device-panel-e2e-data',
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
