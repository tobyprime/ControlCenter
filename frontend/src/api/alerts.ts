import { apiFetch } from './base'

export interface NapcatSettings {
  baseUrl: string | null
  tokenSet: boolean
  targetType: string | null
  targetId: string | null
}

export interface AlertSettings {
  napcat: NapcatSettings
}

export interface AlertSettingsInput {
  baseUrl?: string
  token?: string
  targetType?: string
  targetId?: string
}

export interface QueueItem {
  id: number
  createdAtUtc: string
  channel: string
  title: string
  content: string
  attempts: number
  lastError: string | null
}

export interface AlertQueue {
  count: number
  items: QueueItem[]
}

export interface AlertRuleParamDescriptor {
  name: string
  type: 'number' | 'string'
  required: boolean
  defaultValue: string | null
  description: string
}

export interface AlertRuleType {
  type: string
  displayName: string
  description: string
  requiresMetric: boolean
  allowsNullMetric: boolean
  paramDescriptors: AlertRuleParamDescriptor[]
}

export interface AlertRule {
  id: number
  targetId: number
  targetName: string
  metric: string | null
  metricDisplayName: string | null
  ruleType: string
  paramsJson: string
  enabled: boolean
  updatedAtUtc: string
}

export interface AlertRuleInput {
  targetId: number
  metric: string | null
  ruleType: string
  params: Record<string, number | string>
  enabled: boolean
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

export function fetchAlertSettings(): Promise<AlertSettings> {
  return request<AlertSettings>('/api/alerts/settings')
}

export function saveAlertSettings(input: AlertSettingsInput): Promise<void> {
  return request<void>('/api/alerts/settings', {
    method: 'PUT',
    body: JSON.stringify(input),
  })
}

export function fetchAlertQueue(): Promise<AlertQueue> {
  return request<AlertQueue>('/api/alerts/queue')
}

export function listRuleTypes(): Promise<AlertRuleType[]> {
  return request<AlertRuleType[]>('/api/alerts/rules/types')
}

export function listRules(targetId?: number): Promise<{ items: AlertRule[] }> {
  const query = targetId ? `?targetId=${targetId}` : ''
  return request<{ items: AlertRule[] }>(`/api/alerts/rules${query}`)
}

export function createRule(input: AlertRuleInput): Promise<{ id: number }> {
  return request<{ id: number }>('/api/alerts/rules', {
    method: 'POST',
    body: JSON.stringify(input),
  })
}

export function updateRule(id: number, input: Partial<Omit<AlertRuleInput, 'targetId'>> & { targetId?: number }): Promise<void> {
  return request<void>(`/api/alerts/rules/${id}`, {
    method: 'PUT',
    body: JSON.stringify(input),
  })
}

export function setRuleEnabled(id: number, enabled: boolean): Promise<void> {
  return request<void>(`/api/alerts/rules/${id}/enabled`, {
    method: 'PUT',
    body: JSON.stringify({ enabled }),
  })
}

export function deleteRule(id: number): Promise<void> {
  return request<void>(`/api/alerts/rules/${id}`, { method: 'DELETE' })
}
