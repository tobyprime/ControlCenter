import { apiFetch } from './base'
import type { MetricValueType } from './metrics'

/** 采集模式：push = agent 周期上报；pull = 面板侧轮询（无需 agent）。模式由后端推导，前端只读展示。 */
export type CollectorMode = 'push' | 'pull'

export interface CollectorAgentSummary {
  id: number
  name: string
  /** 能力声明（module2 schema）；null 表示未声明（旧版 agent 兼容，视为具备全部能力）。 */
  capabilities: string[] | null
  online: boolean
}

export interface Collector {
  id: number
  mode: CollectorMode
  name: string
  tags: string[]
  createdAtUtc: string
  updatedAtUtc: string
  lastSeenAtUtc?: string | null
  online: boolean
  /** push 采集器关联的 agent 摘要；pull 采集器为 null。 */
  agent?: CollectorAgentSummary | null
}

export interface CollectorCreated extends Collector {
  agentToken: string
}

export interface PullMetricMappingInput {
  metricKey: string
  jsonPath: string
  valueType: 'number' | 'enum' | 'string'
  displayName: string
  unit: string
}

export interface PullUpsertInput {
  url: string
  intervalSeconds?: number
  mappings: PullMetricMappingInput[]
}

export interface PullMapping {
  metricKey: string
  jsonPath: string
  valueType: MetricValueType
  displayName: string
  unit: string
}

export interface PullConfig {
  url: string
  intervalSeconds: number
  mappings: PullMapping[]
  createdAtUtc: string
  updatedAtUtc: string
}

export interface CollectorDataType {
  key: string
  displayName: string
}

/** 按需查询返回的最新值样本（只读，不落历史）。 */
export interface CollectorLatestSample {
  key: string
  timeUtc: string
  valueNum: number | null
  valueText: string | null
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

export function listCollectors(): Promise<Collector[]> {
  return request<Collector[]>('/api/collectors')
}

/** 创建采集器：带 pull 配置 = pull 采集器；否则 = push 采集器（返回一次性的 agent token）。 */
export function createCollector(input: {
  name: string
  tags: string[]
  pull?: PullUpsertInput
}): Promise<CollectorCreated> {
  return request<CollectorCreated>('/api/collectors', {
    method: 'POST',
    body: JSON.stringify(input),
  })
}

export function updateCollector(id: number, name: string, tags: string[]): Promise<Collector> {
  return request<Collector>(`/api/collectors/${id}`, {
    method: 'PUT',
    body: JSON.stringify({ name, tags }),
  })
}

export function deleteCollector(id: number): Promise<void> {
  return request<void>(`/api/collectors/${id}`, { method: 'DELETE' })
}

/** 重置 push 采集器的 agent token；pull 采集器无 token，后端返回 400。 */
export function resetCollectorToken(id: number): Promise<{ agentToken: string }> {
  return request<{ agentToken: string }>(`/api/collectors/${id}/token`, {
    method: 'POST',
    body: JSON.stringify({}),
  })
}

/** 读取 pull 采集器轮询配置；未配置（204）返回 null。 */
export async function getPullConfig(id: number): Promise<PullConfig | null> {
  const config = await request<PullConfig | null>(`/api/collectors/${id}/pull`)
  return config ?? null
}

export function updatePullConfig(id: number, input: PullUpsertInput): Promise<PullConfig> {
  return request<PullConfig>(`/api/collectors/${id}/pull`, {
    method: 'PUT',
    body: JSON.stringify(input),
  })
}

/** 采集器数据类型清单（验收8）：后端经 DI 收集的注册类型全集。 */
export function listCollectorDataTypes(): Promise<CollectorDataType[]> {
  return request<CollectorDataType[]>('/api/collectors/data-types')
}

/** 按需查询最新值：push 经 agent 即时采样（离线 409/超时 504），pull 直读面板侧最新样本。 */
export function fetchLatestValues(id: number): Promise<{ samples: CollectorLatestSample[] }> {
  return request<{ samples: CollectorLatestSample[] }>(`/api/collectors/${id}/metrics/latest`)
}
