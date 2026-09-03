import { expect, test, type Page } from '@playwright/test'

const PASSWORD = 'e2e-password-9'
const EVIDENCE_DIR = './evidence'

async function expectNoHorizontalOverflow(page: Page, label: string) {
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
  )
  expect.soft(overflow, `${label} 横向溢出应为 0`).toBeLessThanOrEqual(0)
}

test.describe('登录与会话（骨架验收）', () => {
  test('未登录访问根路径被拒并跳转登录页', async ({ page }) => {
    await page.goto('/')
    await expect(page).toHaveURL(/\/login/)
    await expect(page.getByRole('heading', { name: '设备与环境统一管理面板' })).toBeVisible()
  })

  test('错误密码提示错误信息', async ({ page }) => {
    await page.goto('/login')
    await page.getByPlaceholder('请输入用户名').fill('admin')
    await page.getByPlaceholder('请输入密码').fill('wrong-password')
    await page.getByRole('button', { name: /登\s*录/ }).click()
    await expect(page.getByRole('alert')).toContainText('用户名或密码错误')
  })

  test('登录成功进入主布局，登出后再次访问被拒', async ({ page }) => {
    await page.goto('/login')
    await page.getByPlaceholder('请输入用户名').fill('admin')
    await page.getByPlaceholder('请输入密码').fill(PASSWORD)
    await page.getByRole('button', { name: /登\s*录/ }).click()

    await expect(page.getByRole('heading', { name: /欢迎/ })).toBeVisible()
    await expect(page.getByText('退出登录')).toBeVisible()
    await page.screenshot({ path: `${EVIDENCE_DIR}/desktop-main-layout.png`, fullPage: true })

    await page.getByRole('button', { name: '退出登录' }).click()
    await expect(page).toHaveURL(/\/login/)

    // 登出后携带旧会话访问受保护页面应被拒绝
    await page.goto('/')
    await expect(page).toHaveURL(/\/login/)
  })

  test('375px 视口下登录页无横向溢出', async ({ page }) => {
    await page.setViewportSize({ width: 375, height: 667 })
    await page.goto('/login')
    await expect(page.getByRole('heading', { name: '设备与环境统一管理面板' })).toBeVisible()
    await expectNoHorizontalOverflow(page, '登录页')
    await page.screenshot({ path: `${EVIDENCE_DIR}/mobile-login.png`, fullPage: true })
  })

  test('375px 视口下主布局无横向溢出', async ({ page }) => {
    await page.setViewportSize({ width: 375, height: 667 })
    await page.goto('/login')
    await page.getByPlaceholder('请输入用户名').fill('admin')
    await page.getByPlaceholder('请输入密码').fill(PASSWORD)
    await page.getByRole('button', { name: /登\s*录/ }).click()
    await expect(page.getByRole('heading', { name: /欢迎/ })).toBeVisible()

    await expectNoHorizontalOverflow(page, '主布局')
    await page.screenshot({ path: `${EVIDENCE_DIR}/mobile-main-layout.png`, fullPage: true })
  })

  test('桌面端登录页截图', async ({ page }) => {
    await page.goto('/login')
    await expect(page.getByRole('heading', { name: '设备与环境统一管理面板' })).toBeVisible()
    await page.screenshot({ path: `${EVIDENCE_DIR}/desktop-login.png`, fullPage: true })
  })
})
