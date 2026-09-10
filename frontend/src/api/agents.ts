// TOB-401：实现由 openapi-ts 生成客户端承载（契约见 src/api/gen/），本模块只保留视图层消费的类型与函数签名。
import {
  deleteApiAgents,
  getApiAgents,
  postApiAgents,
  postApiAgentsToken,
  putApiAgentsLabels,
} from './base'

/** Agent 台账（三期模块2）：连接身份与能力声明的宿主；capabilities 为 null 表示未声明（旧版 agent 兼容）。 */
export interface Agent {
  id: number
  name: string
  labels: string[]
  capabilities: string[] | null
  createdAtUtc: string
  updatedAtUtc: string
  lastSeenAtUtc?: string | null
  online: boolean
  /** 关联的采集器 id；null 表示台账直建、未关联采集器。 */
  collectorId?: number | null
}

export interface AgentCreated extends Agent {
  agentToken: string
}

export async function listAgents(label?: string): Promise<Agent[]> {
  const { data } = await getApiAgents<true>({ query: label ? { label } : undefined })
  return data as Agent[]
}

export async function createAgent(input: { name: string; labels: string[] }): Promise<AgentCreated> {
  const { data } = await postApiAgents<true>({ body: input })
  return data as AgentCreated
}

export async function updateAgentLabels(id: number, labels: string[]): Promise<Agent> {
  const { data } = await putApiAgentsLabels<true>({ path: { id }, body: { labels } })
  return data as Agent
}

export async function resetAgentToken(id: number): Promise<{ agentToken: string }> {
  const { data } = await postApiAgentsToken<true>({ path: { id } })
  return data as { agentToken: string }
}

export async function deleteAgent(id: number): Promise<void> {
  await deleteApiAgents<true>({ path: { id } })
}
