import { apiFetch } from './base'

export interface Target {
  id: number
  type: 'device' | 'service'
  name: string
  deviceId: number | null
  online: boolean
}

export type MetricValueType = 'number' | 'enum' | 'string' | 'bool'

export interface MetricKey {
  key: string
  valueType: MetricValueType
  unit: string | null
  displayName: string
}

export type Granularity = 'raw' | 'hour' | 'day'

export interface TargetSeriesPoint {
  t: string
  value: number | null
  text: string | null
}

export interface TargetSeries {
  targetId: number
  metric: string
  granularity: Granularity
  fromUtc: string
  toUtc: string
  points: TargetSeriesPoint[]
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

export function listTargets(): Promise<Target[]> {
  return request<Target[]>('/api/targets')
}

export function listMetricKeys(): Promise<MetricKey[]> {
  return request<MetricKey[]>('/api/metric-keys')
}

export function fetchTargetSeries(
  targetId: number,
  metric: string,
  fromIso: string,
  toIso: string,
  granularity: 'auto' | Granularity = 'auto',
): Promise<TargetSeries> {
  const query = new URLSearchParams({ metric, from: fromIso, to: toIso, granularity })
  return request<TargetSeries>(`/api/targets/${targetId}/series?${query.toString()}`)
}
