// TOB-401：实现由 openapi-ts 生成客户端承载（契约见 src/api/gen/），本模块只保留视图层消费的类型与函数签名。
import {
  deleteApiCollectors,
  getApiCollectors,
  getApiCollectorsDataTypes,
  getApiCollectorsMetricsLatest,
  getApiCollectorsPull,
  postApiCollectors,
  postApiCollectorsToken,
  putApiCollectors,
  putApiCollectorsPull,
} from './base'
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

export async function listCollectors(): Promise<Collector[]> {
  const { data } = await getApiCollectors<true>({})
  return data as Collector[]
}

/** 创建采集器：带 pull 配置 = pull 采集器；否则 = push 采集器（返回一次性的 agent token）。 */
export async function createCollector(input: {
  name: string
  tags: string[]
  pull?: PullUpsertInput
}): Promise<CollectorCreated> {
  const { data } = await postApiCollectors<true>({ body: input })
  return data as CollectorCreated
}

export async function updateCollector(id: number, name: string, tags: string[]): Promise<Collector> {
  const { data } = await putApiCollectors<true>({ path: { id }, body: { name, tags } })
  return data as Collector
}

export async function deleteCollector(id: number): Promise<void> {
  await deleteApiCollectors<true>({ path: { id } })
}

/** 重置 push 采集器的 agent token；pull 采集器无 token，后端返回 400。 */
export async function resetCollectorToken(id: number): Promise<{ agentToken: string }> {
  const { data } = await postApiCollectorsToken<true>({ path: { id } })
  return data as { agentToken: string }
}

/** 读取 pull 采集器轮询配置；未配置（204）返回 null。 */
export async function getPullConfig(id: number): Promise<PullConfig | null> {
  const { data } = await getApiCollectorsPull<true>({ path: { id } })
  return (data ?? null) as PullConfig | null
}

export async function updatePullConfig(id: number, input: PullUpsertInput): Promise<PullConfig> {
  const { data } = await putApiCollectorsPull<true>({ path: { id }, body: input })
  return data as PullConfig
}

/** 采集器数据类型清单（验收8）：后端经 DI 收集的注册类型全集。 */
export async function listCollectorDataTypes(): Promise<CollectorDataType[]> {
  const { data } = await getApiCollectorsDataTypes<true>({})
  return data as CollectorDataType[]
}

/** 按需查询最新值：push 经 agent 即时采样（离线 409/超时 504），pull 直读面板侧最新样本。 */
export async function fetchLatestValues(id: number): Promise<{ samples: CollectorLatestSample[] }> {
  const { data } = await getApiCollectorsMetricsLatest<true>({ path: { collectorId: id } })
  return data as { samples: CollectorLatestSample[] }
}
