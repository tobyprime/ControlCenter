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
    await expect(cardLocator(page, 'overview-total-devices')).toBeVisible()
    await expect(cardLocator(page, 'overview-online-devices')).toBeVisible()
    await expect(cardLocator(page, 'overview-active-alerts')).toBeVisible()

    // 数值与 /api/targets 一致（并行用例可能并发增删目标，以实时接口为准）；
    // 超时覆盖一次 15s 自动刷新周期——满载下挂载取数偶发失败由下一刷新兜底（验收 4）
    await expect
      .poll(
        async () => {
          const targets = (await (await page.request.get('/api/targets')).json()) as Array<{
            online: boolean
          }>
          const [totalText, onlineText] = await Promise.all([
            cardLocator(page, 'overview-total-devices').locator('.overview-value').textContent(),
            cardLocator(page, 'overview-online-devices').locator('.overview-value').textContent(),
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
    const onlineCard = cardLocator(page, 'overview-online-devices')
    await onlineCard.getByRole('button', { name: '隐藏' }).click()
    await expect(onlineCard).toHaveClass(/card-hidden/)

    await page.getByRole('button', { name: '保存布局' }).click()
    await expect(page.getByRole('button', { name: '进入编辑' })).toBeVisible()

    await page.reload()
    await expect(cardLocator(page, 'overview-online-devices')).toHaveCount(0)
    await expect(cardLocator(page, 'overview-total-devices')).toBeVisible()
    await expect(cardLocator(page, 'overview-active-alerts')).toBeVisible()
    await page.screenshot({ path: `${EVIDENCE_DIR}/home-card-hidden.png`, fullPage: true })
  })

  test('编辑模式删除与新增卡片，保存后刷新保持（验收 1）', async ({ page }) => {
    await mockLayoutApi(page)
    await login(page)

    // 删除活跃告警卡并保存
    await page.getByRole('button', { name: '进入编辑' }).click()
    await cardLocator(page, 'overview-active-alerts').getByRole('button', { name: '删除' }).click()
    await expect(cardLocator(page, 'overview-active-alerts')).toHaveCount(0)
    await page.getByRole('button', { name: '保存布局' }).click()

    await page.reload()
    await expect(cardLocator(page, 'overview-active-alerts')).toHaveCount(0)

    // 从目录新增回活跃告警卡并保存
    await page.getByRole('button', { name: '进入编辑' }).click()
    await page.getByRole('button', { name: '添加「活跃告警」' }).click()
    await expect(cardLocator(page, 'overview-active-alerts')).toBeVisible()
    await page.getByRole('button', { name: '保存布局' }).click()

    await page.reload()
    await expect(cardLocator(page, 'overview-active-alerts')).toBeVisible()
  })

  test('编辑模式拖拽排序，保存后刷新顺序保持（验收 1）', async ({ page }) => {
    await mockLayoutApi(page)
    await login(page)

    await page.getByRole('button', { name: '进入编辑' }).click()
    expect(await readCardOrder(page)).toEqual([
      'overview-total-devices',
      'overview-online-devices',
      'overview-active-alerts',
    ])

    // 把活跃告警拖到最前
    await html5Drag(
      page,
      '[data-card-type="overview-active-alerts"]',
      '[data-card-type="overview-total-devices"]',
    )
    expect(await readCardOrder(page)).toEqual([
      'overview-active-alerts',
      'overview-total-devices',
      'overview-online-devices',
    ])

    await page.getByRole('button', { name: '保存布局' }).click()
    await page.reload()
    expect(await readCardOrder(page)).toEqual([
      'overview-active-alerts',
      'overview-total-devices',
      'overview-online-devices',
    ])
    await page.screenshot({ path: `${EVIDENCE_DIR}/home-card-reorder.png`, fullPage: true })
  })

  test('编辑模式取消不保存布局修改（验收 1）', async ({ page }) => {
    await mockLayoutApi(page)
    await login(page)

    await page.getByRole('button', { name: '进入编辑' }).click()
    await cardLocator(page, 'overview-total-devices').getByRole('button', { name: '隐藏' }).click()
    await page.getByRole('button', { name: '取消' }).click()

    await expect(cardLocator(page, 'overview-total-devices')).toBeVisible()
    await page.reload()
    await expect(cardLocator(page, 'overview-total-devices')).toBeVisible()
    await expect(cardLocator(page, 'overview-online-devices')).toBeVisible()
    await expect(cardLocator(page, 'overview-active-alerts')).toBeVisible()
  })

  test('真实布局 API：默认布局渲染与保存刷新往返（验收 1/3 前置，不经 mock）', async ({ page }) => {
    // 唯一不 mock 布局 API 的用例：直接对齐 TOB-366 真实契约（GET 返回 sort 字段、PUT 要求 sort），
    // 防止前端字段映射与后端脱节（mock 用例无法暴露这类断裂）
    await login(page)

    // 未保存过布局：真实 GET 返回服务端默认布局，三张概览卡应正常渲染
    await expect(cardLocator(page, 'overview-total-devices')).toBeVisible()
    await expect(cardLocator(page, 'overview-online-devices')).toBeVisible()
    await expect(cardLocator(page, 'overview-active-alerts')).toBeVisible()

    // 保存走真实 PUT，刷新后布局保持
    await page.getByRole('button', { name: '进入编辑' }).click()
    const totalCard = cardLocator(page, 'overview-total-devices')
    await totalCard.getByRole('button', { name: '隐藏' }).click()
    await page.getByRole('button', { name: '保存布局' }).click()
    await expect(page.getByRole('button', { name: '进入编辑' })).toBeVisible()

    await page.reload()
    await expect(cardLocator(page, 'overview-total-devices')).toHaveCount(0)
    await expect(cardLocator(page, 'overview-online-devices')).toBeVisible()
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
    const totalCard = cardLocator(page, 'overview-total-devices')
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

test.describe('主页指标卡（TOB-368）', () => {
  const NOW = Date.parse('2026-09-05T08:00:00Z')

  const METRIC_KEYS = [
    {
      key: 'player.count',
      valueType: 'number',
      displayName: '在线玩家',
      unit: '人',
      builtIn: false,
      createdAtUtc: '2026-09-05T00:00:00Z',
      updatedAtUtc: '2026-09-05T00:00:00Z',
    },
    {
      key: 'svc.status',
      valueType: 'enum',
      displayName: '服务状态',
      unit: '',
      builtIn: false,
      createdAtUtc: '2026-09-05T00:00:00Z',
      updatedAtUtc: '2026-09-05T00:00:00Z',
    },
  ]

  // 概览：player.count 最新 7，svc.status 最新 online
  function overviewItems() {
    return [
      {
        key: 'player.count',
        valueType: 'number',
        displayName: '在线玩家',
        unit: '人',
        builtIn: false,
        latestTimeUtc: '2026-09-05T07:59:00Z',
        latestValueNum: 7,
        latestValueText: null,
      },
      {
        key: 'svc.status',
        valueType: 'enum',
        displayName: '服务状态',
        unit: '',
        builtIn: false,
        latestTimeUtc: '2026-09-05T07:59:00Z',
        latestValueNum: null,
        latestValueText: 'online',
      },
    ]
  }

  // 序列：12 个点（30 分钟间隔），player.count 递增至 12；svc.status 无数值序列
  function seriesFor(key: string) {
    const points =
      key === 'player.count'
        ? Array.from({ length: 12 }, (_, i) => ({
            t: new Date(NOW - (11 - i) * 30 * 60 * 1000).toISOString(),
            v: i + 1,
          }))
        : []
    return [{ key, points }]
  }

  // 指标管道 mock：keys 注册表 / 指标概览 / 序列查询（序列 URL 捕获供时间窗断言）
  async function mockMetricApis(page: Page, options?: { overviewEmpty?: boolean }) {
    await page.route('**/api/metrics/keys', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(METRIC_KEYS),
      }),
    )
    await page.route('**/api/metrics/*/overview', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(options?.overviewEmpty ? [] : overviewItems()),
      }),
    )
  }

  async function mockSeriesApi(page: Page) {
    const urls: string[] = []
    await page.route('**/api/metrics/*/series*', async (route: Route) => {
      const url = route.request().url()
      urls.push(url)
      const key = new URL(url).searchParams.get('keys') ?? ''
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          targetId: Number(url.match(/\/api\/metrics\/(\d+)\//)?.[1]),
          granularity: 'raw',
          fromUtc: new URL(url).searchParams.get('from'),
          toUtc: new URL(url).searchParams.get('to'),
          series: seriesFor(key),
        }),
      })
    })
    return urls
  }

  async function configureCard(card: ReturnType<typeof cardLocator>, targetLabel: string, keyLabel: string, windowLabel: string) {
    await card.getByLabel('目标').selectOption({ label: targetLabel })
    await card.getByLabel('指标').selectOption({ label: keyLabel })
    if (windowLabel) {
      await card.getByLabel('时间窗').selectOption({ label: windowLabel })
    }
  }

  test('曲线卡：配置来源与时间窗，保存刷新后按所选窗口渲染序列（验收 1/2）', async ({ page }) => {
    await mockLayoutApi(page)
    await mockMetricApis(page)
    const seriesUrls = await mockSeriesApi(page)
    await login(page)
    await createDevicesViaApi(page, 1)
    await page.reload()

    await page.getByRole('button', { name: '进入编辑' }).click()
    await page.getByRole('button', { name: '添加「曲线卡」' }).click()
    const chartCard = cardLocator(page, 'metric-chart')
    await configureCard(chartCard, '主页概览机 1', '在线玩家（player.count）', '最近 6 小时')
    await page.getByRole('button', { name: '保存布局' }).click()

    // 保存即渲染：曲线卡出现，带指标名与最新值
    await expect(chartCard.getByRole('heading', { name: '在线玩家' })).toBeVisible()
    await expect(chartCard.locator('svg')).toBeVisible()

    // 序列请求走模块 0 统一管道：keys=player.count，窗口 6h
    expect(seriesUrls.length).toBeGreaterThan(0)
    const url = new URL(seriesUrls[seriesUrls.length - 1])
    expect(url.searchParams.get('keys')).toBe('player.count')
    const from = Date.parse(url.searchParams.get('from') ?? '')
    const to = Date.parse(url.searchParams.get('to') ?? '')
    expect(Math.round((to - from) / 3600000)).toBe(6)

    // 刷新后配置保持：重新拉序列并渲染
    await page.reload()
    await expect(chartCard.getByRole('heading', { name: '在线玩家' })).toBeVisible()
    await expect(chartCard.locator('svg')).toBeVisible()
    await expect(chartCard.getByText(/最新：/)).toContainText('12')
    await page.screenshot({ path: `${EVIDENCE_DIR}/home-metric-chart-card.png`, fullPage: true })
  })

  test('数值卡显示最新值与单位，状态卡显示状态文本；卡片类型可切换（验收 1/2）', async ({ page }) => {
    await mockLayoutApi(page)
    await mockMetricApis(page)
    await mockSeriesApi(page)
    await login(page)
    await createDevicesViaApi(page, 1)
    await page.reload()

    await page.getByRole('button', { name: '进入编辑' }).click()
    await page.getByRole('button', { name: '添加「数值卡」' }).click()
    const valueCard = cardLocator(page, 'metric-value')
    await configureCard(valueCard, '主页概览机 1', '在线玩家（player.count）', '')
    await page.getByRole('button', { name: '保存布局' }).click()

    // 数值卡：最新值 + 单位
    await expect(valueCard).toBeVisible()
    await expect(valueCard.locator('.overview-value')).toHaveText(/7/)
    await expect(valueCard).toContainText('人')

    // 状态卡：enum 指标显示状态文本；类型不兼容项不可选
    await page.getByRole('button', { name: '进入编辑' }).click()
    await page.getByRole('button', { name: '添加「状态卡」' }).click()
    const statusCard = cardLocator(page, 'metric-status')
    await configureCard(statusCard, '主页概览机 1', '服务状态（svc.status）', '')
    await expect(statusCard.getByLabel('卡片类型').locator('option[value="metric-value"]')).toHaveAttribute('disabled', /.*/)
    await page.getByRole('button', { name: '保存布局' }).click()
    await expect(statusCard.locator('.overview-value')).toHaveText('online')

    // 卡片类型可配：曲线卡切到数值卡后按数值卡渲染
    await page.getByRole('button', { name: '进入编辑' }).click()
    await page.getByRole('button', { name: '添加「曲线卡」' }).click()
    const chartCard = cardLocator(page, 'metric-chart')
    await configureCard(chartCard, '主页概览机 1', '在线玩家（player.count）', '')
    await chartCard.getByLabel('卡片类型').selectOption({ label: '数值卡' })
    await expect(cardLocator(page, 'metric-value')).toHaveCount(2)
    await expect(chartCard).toHaveCount(0)
    await page.getByRole('button', { name: '保存布局' }).click()
    await expect(cardLocator(page, 'metric-value').first()).toBeVisible()
    await page.screenshot({ path: `${EVIDENCE_DIR}/home-metric-value-status-cards.png`, fullPage: true })
  })

  test('指标无数据时卡片显示占位不报错（验收 3）', async ({ page }) => {
    await mockLayoutApi(page)
    await mockMetricApis(page, { overviewEmpty: true })
    await mockSeriesApi(page)
    await login(page)
    await createDevicesViaApi(page, 1)
    await page.reload()

    await page.getByRole('button', { name: '进入编辑' }).click()
    await page.getByRole('button', { name: '添加「数值卡」' }).click()
    const valueCard = cardLocator(page, 'metric-value')
    await configureCard(valueCard, '主页概览机 1', '在线玩家（player.count）', '')
    await page.getByRole('button', { name: '保存布局' }).click()

    await expect(valueCard.getByText('暂无数据')).toBeVisible()
    // 页面不崩：其余卡片正常
    await expect(cardLocator(page, 'overview-total-devices')).toBeVisible()
  })

  test('来源目标失效时卡片降级提示，页面不崩（验收 3）', async ({ page }) => {
    // 预置已保存布局：引用不存在的目标 999
    let stored: LayoutCard[] | null = [
      { id: 'overview-total-devices', type: 'overview-total-devices', visible: true, sort: 0, config: {} },
      {
        id: 'metric-value-stale',
        type: 'metric-value',
        visible: true,
        sort: 1,
        config: { targetId: 999, key: 'player.count', windowHours: 24 },
      },
    ]
    await page.route(LAYOUT_API, async (route: Route) => {
      if (route.request().method() === 'PUT') {
        stored = (route.request().postDataJSON() as { cards: LayoutCard[] }).cards
        await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ cards: stored }) })
        return
      }
      if (stored) {
        await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ cards: stored }) })
      } else {
        await route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ error: 'x' }) })
      }
    })
    await mockMetricApis(page)
    await login(page)

    const staleCard = cardLocator(page, 'metric-value')
    await expect(staleCard.getByText('来源目标不存在')).toBeVisible()
    // 页面不崩：概览卡正常渲染
    await expect(cardLocator(page, 'overview-total-devices')).toBeVisible()
    await page.screenshot({ path: `${EVIDENCE_DIR}/home-metric-card-stale-source.png`, fullPage: true })
  })

  test('曲线卡配置经真实布局 API 保存刷新保持（验收 1，不经 mock）', async ({ page }) => {
    // 审查 round 2 问题 1：config 持久化此前仅由 mock 布局 API 覆盖，
    // 而 mock 恰好掩盖过 order/sort 契约断裂——本用例锁真实 PUT→GET→刷新往返
    await login(page)

    // 建唯一名目标并从真实注册表取一个 number 指标（内置 key 随迁移播种，必在）
    // 目标名避开「指标/目标/时间窗」等表单标签词：getByLabel 为子串匹配，
    // 名词混入选项文本会污染其他下拉的可访问名（真实教训：指标卡机 → strict 冲突）
    const created = await page.request.post('/api/targets', {
      data: { name: '真实布局往返机', tags: [] },
    })
    expect(created.ok()).toBe(true)
    const target = (await created.json()) as { id: number }
    const keys = (await (await page.request.get('/api/metrics/keys')).json()) as Array<{
      key: string
      valueType: string
      displayName: string
    }>
    const numeric = keys.find((info) => info.valueType === 'number')
    expect(numeric).toBeTruthy()

    await page.reload()
    await page.getByRole('button', { name: '进入编辑' }).click()
    await page.getByRole('button', { name: '添加「曲线卡」' }).click()
    const chartCard = cardLocator(page, 'metric-chart')
    await configureCard(chartCard, '真实布局往返机', `${numeric!.displayName}（${numeric!.key}）`, '最近 6 小时')
    await page.getByRole('button', { name: '保存布局' }).click()
    await expect(page.getByRole('button', { name: '进入编辑' })).toBeVisible()

    // 真实 GET：wire 字段 sort 存在，config 原样保持（后端透传不解释语义）
    const layout = (await (await page.request.get('/api/dashboard/layout')).json()) as {
      cards: Array<{ type: string; sort: number; config: Record<string, unknown> }>
    }
    const saved = layout.cards.find((card) => card.type === 'metric-chart')
    expect(saved).toBeTruthy()
    expect(Number.isInteger(saved!.sort)).toBe(true)
    expect(saved!.config).toMatchObject({ targetId: target.id, key: numeric!.key, windowHours: 6 })

    // 刷新后按真实 GET 的 config 回填渲染：配置被解析为有效来源，而非降级占位
    await page.reload()
    await expect(chartCard.getByText('暂无数据')).toBeVisible()
    await expect(chartCard.getByText('未配置来源')).toHaveCount(0)
    await expect(chartCard.getByText('来源目标不存在')).toHaveCount(0)
    await expect(chartCard.getByText('指标已不存在')).toHaveCount(0)
  })

  test('来源注册表未就绪时显示加载态，不误报目标/指标不存在（round 2 问题 2）', async ({ page }) => {
    // 预置已保存布局：引用「尚未确认是否存在」的目标/指标
    const stored: LayoutCard[] = [
      { id: 'overview-total-devices', type: 'overview-total-devices', visible: true, sort: 0, config: {} },
      {
        id: 'metric-value-pending',
        type: 'metric-value',
        visible: true,
        sort: 1,
        config: { targetId: 999, key: 'player.count', windowHours: 24 },
      },
    ]
    await page.route(LAYOUT_API, async (route: Route) => {
      if (route.request().method() !== 'PUT') {
        await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ cards: stored }) })
      }
    })

    // 场景 1：targets / 指标注册表请求挂起（首次加载未完成）→ 加载态，不得误报缺失
    await page.route('**/api/targets', () => new Promise(() => {}))
    await page.route('**/api/metrics/keys', () => new Promise(() => {}))
    await login(page)
    const card = cardLocator(page, 'metric-value')
    await expect(card.getByText('加载中…')).toBeVisible()
    await expect(card.getByText('来源目标不存在')).toHaveCount(0)
    await expect(card.getByText('指标已不存在')).toHaveCount(0)

    // 场景 2：targets 接口失败 → 仍不得显示「来源目标不存在」（失败 ≠ 确认缺失）
    await page.route('**/api/targets', (route) =>
      route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ error: 'targets unavailable' }) }),
    )
    await page.route('**/api/metrics/keys', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(METRIC_KEYS) }),
    )
    await page.reload()
    await expect(card.getByText('来源目标不存在')).toHaveCount(0)
    await expect(card.getByText('指标已不存在')).toHaveCount(0)
    // 页面不崩：概览卡正常渲染
    await expect(cardLocator(page, 'overview-total-devices')).toBeVisible()
  })
})
