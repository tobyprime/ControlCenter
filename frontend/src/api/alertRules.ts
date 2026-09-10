// TOB-401：实现由 openapi-ts 生成客户端承载（契约见 src/api/gen/），本模块只保留视图层消费的类型与函数签名。
import {
  deleteApiAlertRules,
  getApiAlertRules,
  getApiAlertRulesTypes,
  postApiAlertRules,
  putApiAlertRules,
} from './gen'
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

export async function listRuleTypes(): Promise<AlertRuleTypeInfo[]> {
  const { data } = await getApiAlertRulesTypes<true>({})
  return data as AlertRuleTypeInfo[]
}

export async function listAlertRules(filter?: { targetId?: number; metricKey?: string }): Promise<AlertRule[]> {
  const query: { targetId?: number; metricKey?: string } = {}
  if (filter?.targetId !== undefined) {
    query.targetId = filter.targetId
  }
  if (filter?.metricKey) {
    query.metricKey = filter.metricKey
  }
  const { data } = await getApiAlertRules<true>({ query })
  return data as AlertRule[]
}

export async function createAlertRule(input: AlertRuleInput): Promise<AlertRule> {
  const { data } = await postApiAlertRules<true>({ body: input })
  return data as AlertRule
}

export async function updateAlertRule(id: number, input: AlertRuleUpdate): Promise<AlertRule> {
  const { data } = await putApiAlertRules<true>({ path: { id }, body: input })
  return data as AlertRule
}

export async function deleteAlertRule(id: number): Promise<void> {
  await deleteApiAlertRules<true>({ path: { id } })
}
