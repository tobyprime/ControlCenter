import { apiFetch } from './base'

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
    throw new Error(message)
  }
  if (response.status === 204) {
    return undefined as T
  }
  return (await response.json()) as T
}

export function listMetricKeys(): Promise<MetricKeyInfo[]> {
  return request<MetricKeyInfo[]>('/api/metrics/keys')
}

export function registerMetricKey(input: {
  key: string
  valueType: MetricValueType
  displayName: string
  unit?: string
}): Promise<MetricKeyInfo> {
  return request<MetricKeyInfo>('/api/metrics/keys', {
    method: 'POST',
    body: JSON.stringify(input),
  })
}

export function updateMetricKey(key: string, displayName: string, unit: string): Promise<MetricKeyInfo> {
  return request<MetricKeyInfo>(`/api/metrics/keys/${encodeURIComponent(key)}`, {
    method: 'PUT',
    body: JSON.stringify({ displayName, unit }),
  })
}

export function deleteMetricKey(key: string): Promise<void> {
  return request<void>(`/api/metrics/keys/${encodeURIComponent(key)}`, { method: 'DELETE' })
}

export function fetchTargetOverview(targetId: number): Promise<MetricOverviewItem[]> {
  return request<MetricOverviewItem[]>(`/api/metrics/${targetId}/overview`)
}

export async function fetchTargetSeries(
  targetId: number,
  keys: string[],
  fromIso: string,
  toIso: string,
  granularity: 'auto' | Granularity = 'auto',
): Promise<TargetSeries> {
  const query = new URLSearchParams({ keys: keys.join(','), from: fromIso, to: toIso, granularity })
  return request<TargetSeries>(`/api/metrics/${targetId}/series?${query.toString()}`)
}
