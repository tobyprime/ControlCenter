import { apiFetch } from './base'

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

export function listAgents(label?: string): Promise<Agent[]> {
  const query = label ? `?label=${encodeURIComponent(label)}` : ''
  return request<Agent[]>(`/api/agents${query}`)
}

export function createAgent(input: { name: string; labels: string[] }): Promise<AgentCreated> {
  return request<AgentCreated>('/api/agents', {
    method: 'POST',
    body: JSON.stringify(input),
  })
}

export function updateAgentLabels(id: number, labels: string[]): Promise<Agent> {
  return request<Agent>(`/api/agents/${id}/labels`, {
    method: 'PUT',
    body: JSON.stringify({ labels }),
  })
}

export function resetAgentToken(id: number): Promise<{ agentToken: string }> {
  return request<{ agentToken: string }>(`/api/agents/${id}/token`, {
    method: 'POST',
    body: JSON.stringify({}),
  })
}

export function deleteAgent(id: number): Promise<void> {
  return request<void>(`/api/agents/${id}`, { method: 'DELETE' })
}
