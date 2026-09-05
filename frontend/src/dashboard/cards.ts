import type { DashboardCard } from '@/api/dashboard'

export interface DashboardCardDef {
  type: string
  label: string
  hint: string
  /** 可否重复添加（指标卡按来源多实例；概览卡单实例） */
  multiple?: boolean
}

// 内置卡片目录：一期概览卡（类型名与 TOB-366 服务端默认布局一致）
// 与 TOB-368 三类指标卡（来源/时间窗配置见 dashboard/cardConfig.ts）
export const BUILTIN_CARD_DEFS: DashboardCardDef[] = [
  { type: 'overview-total-devices', label: '设备总数', hint: '去设备管理页查看' },
  { type: 'overview-online-devices', label: '在线设备', hint: '每 15 秒自动刷新' },
  { type: 'overview-active-alerts', label: '活跃告警', hint: '告警功能建设中' },
  { type: 'metric-value', label: '数值卡', hint: '指标最新值与单位', multiple: true },
  { type: 'metric-status', label: '状态卡', hint: '指标当前状态', multiple: true },
  { type: 'metric-chart', label: '曲线卡', hint: '指标按时间窗的序列', multiple: true },
]

export function cardDef(type: string): DashboardCardDef | undefined {
  return BUILTIN_CARD_DEFS.find((def) => def.type === type)
}

// 前端回退默认布局（接口不可用/无记录）：等价一期主页概览（单实例卡），
// 与 TOB-366 服务端默认布局保持一致；指标卡由用户显式添加并配置来源
export function createDefaultLayout(): DashboardCard[] {
  return BUILTIN_CARD_DEFS.filter((def) => !def.multiple).map((def, index) => ({
    id: def.type,
    type: def.type,
    visible: true,
    order: index,
    config: {},
  }))
}

// 防御式归一：过滤未知类型/缺字段条目，按 order 稳定排序并重排索引，
// 保证主页永不渲染不可识别的空白卡片
export function normalizeLayout(cards: unknown): DashboardCard[] {
  if (!Array.isArray(cards)) {
    return []
  }
  const known = cards.filter(isDashboardCard)
  known.sort((a, b) => a.order - b.order)
  return known.map((card, index) => ({ ...card, order: index }))
}

function isDashboardCard(value: unknown): value is DashboardCard {
  if (typeof value !== 'object' || value === null) {
    return false
  }
  const card = value as Record<string, unknown>
  return (
    typeof card.id === 'string' &&
    typeof card.type === 'string' &&
    cardDef(card.type) !== undefined &&
    typeof card.visible === 'boolean' &&
    typeof card.order === 'number' &&
    Number.isFinite(card.order) &&
    typeof card.config === 'object' &&
    card.config !== null
  )
}
