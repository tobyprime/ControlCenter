import { expect, test, type Page, type Route } from '@playwright/test'

const PASSWORD = 'e2e-password-9'
const EVIDENCE_DIR = './evidence'
const LAYOUT_API = '**/api/dashboard/layout'

interface LayoutCard {
  id: string
  type: string
  visible: boolean
  order: number
  config: Record<string, unknown>
}

async function login(page: Page) {
  await page.goto('/login')
  await page.getByPlaceholder('请输入用户名').fill('admin')
  await page.getByPlaceholder('请输入密码').fill(PASSWORD)
  await page.getByRole('button', { name: /登\s*录/ }).click()
  await expect(page.getByRole('heading', { name: /欢迎/ })).toBeVisible()
}

// 布局 API 有状态 mock：无保存记录时 GET 返回 500（前端应回退默认布局）；
// PUT 后 GET 返回已保存内容，模拟 TOB-366 布局持久化（联调前的前端框架验证）
async function mockLayoutApi(page: Page) {
  let stored: LayoutCard[] | null = null
  await page.route(LAYOUT_API, async (route: Route) => {
    if (route.request().method() === 'PUT') {
      const body = route.request().postDataJSON() as { cards: LayoutCard[] }
      stored = body.cards
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ cards: stored }),
      })
      return
    }
    if (stored) {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ cards: stored }),
      })
    } else {
      await route.fulfill({
        status: 500,
        contentType: 'application/json',
        body: JSON.stringify({ error: 'layout store unavailable' }),
      })
    }
  })
}

async function createDevicesViaApi(page: Page, count: number) {
  for (let i = 0; i < count; i += 1) {
    const response = await page.request.post('/api/targets', {
      data: { name: `主页概览机 ${i + 1}`, tags: [] },
    })
    expect(response.ok()).toBe(true)
  }
}

function cardLocator(page: Page, type: string) {
  return page.locator(`[data-card-type="${type}"]`)
}

async function html5Drag(page: Page, sourceSelector: string, targetSelector: string) {
  await page.evaluate(
    ([source, target]) => {
      const sourceEl = document.querySelector(source)
      const targetEl = document.querySelector(target)
      if (!sourceEl || !targetEl) {
        throw new Error(`拖拽元素不存在：${!sourceEl ? source : target}`)
      }
      const dataTransfer = new DataTransfer()
      sourceEl.dispatchEvent(
        new DragEvent('dragstart', { bubbles: true, cancelable: true, dataTransfer }),
      )
      targetEl.dispatchEvent(
        new DragEvent('dragover', { bubbles: true, cancelable: true, dataTransfer }),
      )
      targetEl.dispatchEvent(
        new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer }),
      )
      sourceEl.dispatchEvent(
        new DragEvent('dragend', { bubbles: true, cancelable: true, dataTransfer }),
      )
    },
    [sourceSelector, targetSelector],
  )
}

async function readCardOrder(page: Page) {
  // reload 后等 SPA 挂载与布局加载完成再读顺序
  await page.waitForSelector('[data-card-type]', { state: 'attached' })
  return page.$$eval('[data-card-type]', (elements) =>
    elements.map((element) => element.getAttribute('data-card-type')),
  )
}

test.describe('主页卡片面板（TOB-367）', () => {
  test('布局接口失败回退默认布局，概览数值与 /api/targets 一致（验收 2/3/4）', async ({ page }) => {
    await page.route(LAYOUT_API, (route) =>
      route.fulfill({
        status: 500,
        contentType: 'application/json',
        body: JSON.stringify({ error: 'layout store unavailable' }),
      }),
    )
    await login(page)
    await createDevicesViaApi(page, 2)
    await page.reload()

    // 欢迎信息保留；默认布局三张卡齐全，无空白面板
    await expect(page.getByRole('heading', { name: /欢迎/ })).toBeVisible()
    await expect(cardLocator(page, 'overview-devices-total')).toBeVisible()
    await expect(cardLocator(page, 'overview-devices-online')).toBeVisible()
    await expect(cardLocator(page, 'overview-alerts-active')).toBeVisible()

    // 数值与 /api/targets 一致（并行用例可能并发增删目标，以实时接口为准）；
    // 超时覆盖一次 15s 自动刷新周期——满载下挂载取数偶发失败由下一刷新兜底（验收 4）
    await expect
      .poll(
        async () => {
          const targets = (await (await page.request.get('/api/targets')).json()) as Array<{
            online: boolean
          }>
          const [totalText, onlineText] = await Promise.all([
            cardLocator(page, 'overview-devices-total').locator('.overview-value').textContent(),
            cardLocator(page, 'overview-devices-online').locator('.overview-value').textContent(),
          ])
          return (
            totalText === String(targets.length) &&
            onlineText === String(targets.filter((target) => target.online).length)
          )
        },
        { timeout: 20_000 },
      )
      .toBe(true)
    await page.screenshot({ path: `${EVIDENCE_DIR}/home-default-layout.png`, fullPage: true })
  })

  test('编辑模式隐藏卡片，保存后刷新布局保持（验收 1）', async ({ page }) => {
    await mockLayoutApi(page)
    await login(page)

    await page.getByRole('button', { name: '进入编辑' }).click()
    const onlineCard = cardLocator(page, 'overview-devices-online')
    await onlineCard.getByRole('button', { name: '隐藏' }).click()
    await expect(onlineCard).toHaveClass(/card-hidden/)

    await page.getByRole('button', { name: '保存布局' }).click()
    await expect(page.getByRole('button', { name: '进入编辑' })).toBeVisible()

    await page.reload()
    await expect(cardLocator(page, 'overview-devices-online')).toHaveCount(0)
    await expect(cardLocator(page, 'overview-devices-total')).toBeVisible()
    await expect(cardLocator(page, 'overview-alerts-active')).toBeVisible()
    await page.screenshot({ path: `${EVIDENCE_DIR}/home-card-hidden.png`, fullPage: true })
  })

  test('编辑模式删除与新增卡片，保存后刷新保持（验收 1）', async ({ page }) => {
    await mockLayoutApi(page)
    await login(page)

    // 删除活跃告警卡并保存
    await page.getByRole('button', { name: '进入编辑' }).click()
    await cardLocator(page, 'overview-alerts-active').getByRole('button', { name: '删除' }).click()
    await expect(cardLocator(page, 'overview-alerts-active')).toHaveCount(0)
    await page.getByRole('button', { name: '保存布局' }).click()

    await page.reload()
    await expect(cardLocator(page, 'overview-alerts-active')).toHaveCount(0)

    // 从目录新增回活跃告警卡并保存
    await page.getByRole('button', { name: '进入编辑' }).click()
    await page.getByRole('button', { name: '添加「活跃告警」' }).click()
    await expect(cardLocator(page, 'overview-alerts-active')).toBeVisible()
    await page.getByRole('button', { name: '保存布局' }).click()

    await page.reload()
    await expect(cardLocator(page, 'overview-alerts-active')).toBeVisible()
  })

  test('编辑模式拖拽排序，保存后刷新顺序保持（验收 1）', async ({ page }) => {
    await mockLayoutApi(page)
    await login(page)

    await page.getByRole('button', { name: '进入编辑' }).click()
    expect(await readCardOrder(page)).toEqual([
      'overview-devices-total',
      'overview-devices-online',
      'overview-alerts-active',
    ])

    // 把活跃告警拖到最前
    await html5Drag(
      page,
      '[data-card-type="overview-alerts-active"]',
      '[data-card-type="overview-devices-total"]',
    )
    expect(await readCardOrder(page)).toEqual([
      'overview-alerts-active',
      'overview-devices-total',
      'overview-devices-online',
    ])

    await page.getByRole('button', { name: '保存布局' }).click()
    await page.reload()
    expect(await readCardOrder(page)).toEqual([
      'overview-alerts-active',
      'overview-devices-total',
      'overview-devices-online',
    ])
    await page.screenshot({ path: `${EVIDENCE_DIR}/home-card-reorder.png`, fullPage: true })
  })

  test('编辑模式取消不保存布局修改（验收 1）', async ({ page }) => {
    await mockLayoutApi(page)
    await login(page)

    await page.getByRole('button', { name: '进入编辑' }).click()
    await cardLocator(page, 'overview-devices-total').getByRole('button', { name: '隐藏' }).click()
    await page.getByRole('button', { name: '取消' }).click()

    await expect(cardLocator(page, 'overview-devices-total')).toBeVisible()
    await page.reload()
    await expect(cardLocator(page, 'overview-devices-total')).toBeVisible()
    await expect(cardLocator(page, 'overview-devices-online')).toBeVisible()
    await expect(cardLocator(page, 'overview-alerts-active')).toBeVisible()
  })

  test('保存失败显示错误横幅且保留编辑内容，恢复后可重试成功（阶段 2 问题 1）', async ({
    page,
  }) => {
    let stored: LayoutCard[] | null = null
    let failPut = true
    await page.route(LAYOUT_API, async (route: Route) => {
      if (route.request().method() === 'PUT') {
        if (failPut) {
          await route.fulfill({
            status: 500,
            contentType: 'application/json',
            body: JSON.stringify({ error: 'layout store unavailable' }),
          })
          return
        }
        stored = (route.request().postDataJSON() as { cards: LayoutCard[] }).cards
        await route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ cards: stored }),
        })
        return
      }
      if (stored) {
        await route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ cards: stored }),
        })
      } else {
        await route.fulfill({
          status: 500,
          contentType: 'application/json',
          body: JSON.stringify({ error: 'layout store unavailable' }),
        })
      }
    })
    await login(page)

    await page.getByRole('button', { name: '进入编辑' }).click()
    const totalCard = cardLocator(page, 'overview-devices-total')
    await totalCard.getByRole('button', { name: '隐藏' }).click()

    // 首次保存失败：错误横幅可见、仍在编辑态、编辑内容未丢失
    await page.getByRole('button', { name: '保存布局' }).click()
    await expect(page.getByRole('alert')).toContainText('layout store unavailable')
    await expect(page.getByRole('button', { name: '保存布局' })).toBeVisible()
    await expect(totalCard).toHaveClass(/card-hidden/)

    // 接口恢复后重试保存成功，刷新后布局保持
    failPut = false
    await page.getByRole('button', { name: '保存布局' }).click()
    await expect(page.getByRole('button', { name: '进入编辑' })).toBeVisible()
    await page.reload()
    await expect(totalCard).toHaveCount(0)
  })
})
