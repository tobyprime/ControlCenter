import { apiFetch } from './base'
import type { MetricValueType } from './metrics'

export type RuleTypeId = 'threshold_above' | 'threshold_below' | 'no_data' | 'state_mismatch'

export interface AlertRuleTypeInfo {
  typeId: RuleTypeId
  displayName: string
  alertTitle: string
  description: string
  supportedValueTypes: MetricValueType[]
  sampleDriven: boolean
}

export interface AlertRule {
  id: number
  targetId: number | null
  targetName: string
  metricKey: string
  metricDisplayName: string
  ruleType: RuleTypeId
  enabled: boolean
  parameters: Record<string, unknown>
  sustainSeconds: number
  repeatMinutes: number
  createdAtUtc: string
  updatedAtUtc: string
}

export interface AlertRuleInput {
  targetId: number | null
  metricKey: string
  ruleType: RuleTypeId
  parameters: Record<string, unknown>
  sustainSeconds?: number
  repeatMinutes?: number
  enabled?: boolean
}

export interface AlertRuleUpdate {
  parameters: Record<string, unknown>
  sustainSeconds?: number
  repeatMinutes?: number
  enabled?: boolean
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

export function listRuleTypes(): Promise<AlertRuleTypeInfo[]> {
  return request<AlertRuleTypeInfo[]>('/api/alert-rules/types')
}

export function listAlertRules(filter?: { targetId?: number; metricKey?: string }): Promise<AlertRule[]> {
  const query = new URLSearchParams()
  if (filter?.targetId !== undefined) {
    query.set('targetId', String(filter.targetId))
  }
  if (filter?.metricKey) {
    query.set('metricKey', filter.metricKey)
  }
  const suffix = query.size > 0 ? `?${query.toString()}` : ''
  return request<AlertRule[]>(`/api/alert-rules${suffix}`)
}

export function createAlertRule(input: AlertRuleInput): Promise<AlertRule> {
  return request<AlertRule>('/api/alert-rules', {
    method: 'POST',
    body: JSON.stringify(input),
  })
}

export function updateAlertRule(id: number, input: AlertRuleUpdate): Promise<AlertRule> {
  return request<AlertRule>(`/api/alert-rules/${id}`, {
    method: 'PUT',
    body: JSON.stringify(input),
  })
}

export function deleteAlertRule(id: number): Promise<void> {
  return request<void>(`/api/alert-rules/${id}`, { method: 'DELETE' })
}
