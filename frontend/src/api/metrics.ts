// TOB-401：实现由 openapi-ts 生成客户端承载（契约见 src/api/gen/），本模块只保留视图层消费的类型与函数签名。
import {
  deleteApiMetricsKeys,
  getApiMetricsAvailable,
  getApiMetricsKeys,
  getApiMetricsOverview,
  getApiMetricsSeries,
  postApiMetricsKeys,
  putApiMetricsKeys,
} from './base'

export type Granularity = 'raw' | 'hour' | 'day'

export type MetricValueType = 'number' | 'enum' | 'string' | 'bool'

export interface MetricKeyInfo {
  key: string
  valueType: MetricValueType
  displayName: string
  unit: string
  builtIn: boolean
  createdAtUtc: string
  updatedAtUtc: string
}

export interface SeriesPoint {
  t: string
  v: number | null
}

export interface MetricSeries {
  key: string
  points: SeriesPoint[]
}

export interface TargetSeries {
  targetId: number
  granularity: Granularity
  fromUtc: string
  toUtc: string
  series: MetricSeries[]
}

export interface MetricOverviewItem {
  key: string
  valueType: MetricValueType
  displayName: string
  unit: string
  builtIn: boolean
  latestTimeUtc: string | null
  latestValueNum: number | null
  latestValueText: string | null
}

export async function listMetricKeys(): Promise<MetricKeyInfo[]> {
  const { data } = await getApiMetricsKeys<true>({})
  return data as MetricKeyInfo[]
}

export async function registerMetricKey(input: {
  key: string
  valueType: MetricValueType
  displayName: string
  unit?: string
}): Promise<MetricKeyInfo> {
  const { data } = await postApiMetricsKeys<true>({ body: input })
  return data as MetricKeyInfo
}

export async function updateMetricKey(key: string, displayName: string, unit: string): Promise<MetricKeyInfo> {
  const { data } = await putApiMetricsKeys<true>({ path: { key }, body: { displayName, unit } })
  return data as MetricKeyInfo
}

export async function deleteMetricKey(key: string): Promise<void> {
  await deleteApiMetricsKeys<true>({ path: { key } })
}

export async function fetchTargetOverview(targetId: number): Promise<MetricOverviewItem[]> {
  const { data } = await getApiMetricsOverview<true>({ path: { collectorId: targetId } })
  return data as MetricOverviewItem[]
}

/** 按来源可用指标（TOB-374 ①）：优先该来源已上报的 key，无上报数据回退到按类型的内置 key。 */
export async function fetchTargetAvailableMetrics(targetId: number): Promise<MetricKeyInfo[]> {
  const { data } = await getApiMetricsAvailable<true>({ path: { collectorId: targetId } })
  return data as MetricKeyInfo[]
}

export async function fetchTargetSeries(
  targetId: number,
  keys: string[],
  fromIso: string,
  toIso: string,
  granularity: 'auto' | Granularity = 'auto',
): Promise<TargetSeries> {
  const { data } = await getApiMetricsSeries<true>({
    path: { collectorId: targetId },
    query: { keys: keys.join(','), from: fromIso, to: toIso, granularity },
  })
  return data as TargetSeries
}
