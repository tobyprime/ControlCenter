import { apiUrl } from './base'

export type Granularity = 'raw' | 'hour' | 'day'

export interface SeriesPoint {
  t: string
  cpu: number
  mem: number
  disk: number
  netRx: number
  netTx: number
}

export interface MetricsSeries {
  deviceId: number
  granularity: Granularity
  fromUtc: string
  toUtc: string
  points: SeriesPoint[]
}

export async function fetchMetricsSeries(
  deviceId: number,
  fromIso: string,
  toIso: string,
  granularity: 'auto' | Granularity = 'auto',
): Promise<MetricsSeries> {
  const query = new URLSearchParams({ from: fromIso, to: toIso, granularity })
  const response = await fetch(apiUrl(`/api/metrics/${deviceId}/series?${query.toString()}`))
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
  return (await response.json()) as MetricsSeries
}
