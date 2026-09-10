// TOB-401：实现由 openapi-ts 生成客户端承载（契约见 src/api/gen/），本模块只保留视图层消费的类型与函数签名。
import { getApiDashboardLayout, putApiDashboardLayout } from './gen'

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

// 无记录时服务端返回默认布局；前端对失败/空结果另行回退，见 dashboard/cards.ts
export async function fetchDashboardLayout(): Promise<DashboardLayout> {
  const { data } = await getApiDashboardLayout<true>({})
  const layout = data as { cards: DashboardCardWire[] }
  return { cards: layout.cards.map(wireCardToCard) }
}

// 后端整份替换保存，成功返回 204 无响应体
export async function saveDashboardLayout(cards: DashboardCard[]): Promise<void> {
  await putApiDashboardLayout<true>({ body: { cards: cards.map(cardToWireCard) } })
}
