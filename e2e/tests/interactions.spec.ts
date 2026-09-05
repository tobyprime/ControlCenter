import { expect, test, type Page } from '@playwright/test'

const PASSWORD = 'e2e-password-9'

async function login(page: Page) {
  await page.goto('/login')
  await page.getByPlaceholder('请输入用户名').fill('admin')
  await page.getByPlaceholder('请输入密码').fill(PASSWORD)
  await page.getByRole('button', { name: /登\s*录/ }).click()
  await expect(page.getByRole('heading', { name: /欢迎/ })).toBeVisible()
}

async function createDeviceViaApi(page: Page, name: string): Promise<{ id: number }> {
  const response = await page.request.post('/api/targets', {
    data: { name, tags: ['E2E'] },
  })
  expect(response.ok()).toBeTruthy()
  return (await response.json()) as { id: number }
}

test.describe('交互模式注册表（TOB-365）', () => {
  test('模式清单与目标声明入口由注册表驱动，终端页支持 ?device 深链', async ({ page }) => {
    await login(page)

    // 注册表清单：shell 终端注册为首个模式
    const modesResponse = await page.request.get('/api/interactions/modes')
    expect(modesResponse.ok()).toBeTruthy()
    const modes = (await modesResponse.json()) as { key: string; displayName: string }[]
    const shell = modes.find((m) => m.key === 'shell')
    expect(shell, '注册表含 shell 模式').toBeTruthy()
    expect(shell!.displayName).toContain('Shell')

    // 目标声明入口：设备目标声明 shell；未声明交互模式的断言随 TOB-361 service 目标补充
    const deviceName = `交互模式验收机 ${Date.now()}`
    const device = await createDeviceViaApi(page, deviceName)
    const declaredResponse = await page.request.get(`/api/devices/${device.id}/interaction-modes`)
    expect(declaredResponse.ok()).toBeTruthy()
    const declared = (await declaredResponse.json()) as { key: string }[]
    expect(declared.map((m) => m.key)).toEqual(['shell'])

    // 终端页 ?device=<id> 深链：交互入口按声明预选目标
    await page.goto(`/terminal?device=${device.id}`)
    await expect(page.locator('select.device-select')).toHaveValue(String(device.id))
    await expect(page.getByRole('button', { name: '打开终端' })).toBeVisible()
  })
})
