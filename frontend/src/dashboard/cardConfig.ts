import type { MetricKeyInfo, MetricValueType } from '@/api/metrics'

// TOB-368 指标卡：三类卡片按 metric 值类型渲染（约束 A：核心语义中立，
// 新增一种指标 = 注册 key + 类型，卡片无需改核心逻辑）。
export type MetricCardType = 'metric-value' | 'metric-status' | 'metric-chart'

export const METRIC_CARD_TYPE_LABELS: Record<MetricCardType, string> = {
  'metric-value': '数值卡',
  'metric-status': '状态卡',
  'metric-chart': '曲线卡',
}

export function isMetricCardType(type: string): type is MetricCardType {
  return type in METRIC_CARD_TYPE_LABELS
}

// 指标卡配置：config 后端只透传不解释语义（TOB-366 契约），结构契约在前端；
// 随卡片 config 走布局 API 持久化
export interface MetricCardConfig {
  targetId: number
  key: string
  windowHours: number
}

// 时间窗预设档位：与指标曲线页既有档位对齐
export const WINDOW_OPTIONS = [
  { label: '最近 1 小时', hours: 1 },
  { label: '最近 6 小时', hours: 6 },
  { label: '最近 24 小时', hours: 24 },
  { label: '最近 7 天', hours: 24 * 7 },
]

export const DEFAULT_WINDOW_HOURS = 24

// 来源失效降级原因：'' 表示正常
export type MetricCardDegradedReason = '' | 'unconfigured' | 'target-missing' | 'key-missing' | 'type-mismatch'

const DEGRADED_TEXT: Record<Exclude<MetricCardDegradedReason, ''>, string> = {
  unconfigured: '未配置来源',
  'target-missing': '来源目标不存在',
  'key-missing': '指标已不存在',
  'type-mismatch': '指标类型与卡片不匹配',
}

// 防御式解析：字段缺失或类型不符视为未配置（卡片渲染降级占位，不崩页面）
export function parseMetricCardConfig(config: unknown): MetricCardConfig | null {
  if (typeof config !== 'object' || config === null) {
    return null
  }
  const raw = config as Record<string, unknown>
  const targetId = raw.targetId
  const key = raw.key
  if (typeof targetId !== 'number' || !Number.isInteger(targetId) || targetId <= 0) {
    return null
  }
  if (typeof key !== 'string' || key.trim() === '') {
    return null
  }
  const windowHours =
    typeof raw.windowHours === 'number' && Number.isFinite(raw.windowHours) && raw.windowHours > 0
      ? raw.windowHours
      : DEFAULT_WINDOW_HOURS
  return { targetId, key, windowHours }
}

// 值类型 → 可用卡片类型：number 供数值/曲线；enum/bool 供状态；string 暂无适配卡片
export function compatibleCardTypes(valueType: MetricValueType): MetricCardType[] {
  switch (valueType) {
    case 'number':
      return ['metric-value', 'metric-chart']
    case 'enum':
    case 'bool':
      return ['metric-status']
    default:
      return []
  }
}

export function keyInfoOf(keys: MetricKeyInfo[], key: string): MetricKeyInfo | undefined {
  return keys.find((info) => info.key === key)
}

// 卡片状态占位文案：降级 > 加载中 > 无数据；返回 '' 表示有数据可渲染
export function metricCardStateText(input: {
  degradedReason: MetricCardDegradedReason
  loading: boolean
  hasData: boolean
}): string {
  if (input.degradedReason) {
    return DEGRADED_TEXT[input.degradedReason]
  }
  if (input.loading) {
    return '加载中…'
  }
  if (!input.hasData) {
    return '暂无数据'
  }
  return ''
}

function humanizeBytes(value: number): string {
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let current = value
  let index = 0
  while (Math.abs(current) >= 1024 && index < units.length - 1) {
    current /= 1024
    index += 1
  }
  return `${index === 0 ? Math.round(current) : current.toFixed(1)} ${units[index]}`
}

/** 指标数值展示：字节类单位人性化换算，其余最多一位小数并附单位（与指标曲线页口径一致）。 */
export function formatMetricValue(value: number, unit: string): string {
  if (unit === 'B' || unit === 'B/s') {
    const text = humanizeBytes(value)
    return unit === 'B/s' ? `${text}/s` : text
  }
  const rounded = Math.abs(value) >= 100 ? Math.round(value).toString() : (Math.round(value * 10) / 10).toString()
  return unit ? `${rounded} ${unit}` : rounded
}

/** 最新值时间短格式（MM-DD HH:mm）。 */
export function formatLatestTime(utc: string | null | undefined): string {
  if (!utc) {
    return ''
  }
  const date = new Date(utc)
  if (Number.isNaN(date.getTime())) {
    return ''
  }
  const pad = (n: number) => n.toString().padStart(2, '0')
  return `${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}`
}
