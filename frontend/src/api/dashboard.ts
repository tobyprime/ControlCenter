import { apiFetch } from './base'

// 布局契约（TOB-366）：单用户单套，整份布局读写；
// 卡片条目含 id、类型、排序、显隐与 config（config 后端只透传不解释语义）。
// wire 排序字段为 sort（后端契约）；前端内部统一用 order，收发在此映射。
export interface DashboardCard {
  id: string
  type: string
  visible: boolean
  order: number
  config: Record<string, unknown>
}

export interface DashboardLayout {
  cards: DashboardCard[]
}

interface DashboardCardWire {
  id: string
  type: string
  visible: boolean
  sort: number
  config: Record<string, unknown>
}

function wireCardToCard(card: DashboardCardWire): DashboardCard {
  const { sort, ...rest } = card
  return { ...rest, order: sort }
}

function cardToWireCard(card: DashboardCard): DashboardCardWire {
  const { order, ...rest } = card
  return { ...rest, sort: order }
}

async function request<T>(input: string, init?: RequestInit): Promise<T> {
  const response = await apiFetch(input, {
    headers: { 'Content-Type': 'application/json' },
    ...init,
  })
  if (!response.ok) {
    let message = `请求失败（${response.status}）`
    try {
      const body = (await response.json()) as { error?: string }
      if (body.error) {
        message = body.error
      }
    } catch {
      // 忽略非 JSON 响应体
    }
    const error = new Error(message) as Error & { status?: number }
    error.status = response.status
    throw error
  }
  if (response.status === 204) {
    return undefined as T
  }
  return (await response.json()) as T
}

// 无记录时服务端返回默认布局；前端对失败/空结果另行回退，见 dashboard/cards.ts
export async function fetchDashboardLayout(): Promise<DashboardLayout> {
  const layout = await request<{ cards: DashboardCardWire[] }>('/api/dashboard/layout')
  return { cards: layout.cards.map(wireCardToCard) }
}

// 后端整份替换保存，成功返回 204 无响应体
export async function saveDashboardLayout(cards: DashboardCard[]): Promise<void> {
  await request<void>('/api/dashboard/layout', {
    method: 'PUT',
    body: JSON.stringify({ cards: cards.map(cardToWireCard) }),
  })
}
