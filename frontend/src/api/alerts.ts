// TOB-401：实现由 openapi-ts 生成客户端承载（契约见 src/api/gen/），本模块只保留视图层消费的类型与函数签名。
import { getApiAlertsActiveCount, getApiAlertsQueue, getApiAlertsSettings, putApiAlertsSettings } from './base'

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

export async function fetchAlertSettings(): Promise<AlertSettings> {
  const { data } = await getApiAlertsSettings<true>({})
  return data as AlertSettings
}

export async function saveAlertSettings(input: AlertSettingsInput): Promise<void> {
  await putApiAlertsSettings<true>({ body: input })
}

export async function fetchAlertQueue(): Promise<AlertQueue> {
  const { data } = await getApiAlertsQueue<true>({})
  return data as AlertQueue
}

export interface ActiveAlertCount {
  count: number
}

export async function fetchActiveAlertCount(): Promise<ActiveAlertCount> {
  const { data } = await getApiAlertsActiveCount<true>({})
  return data as ActiveAlertCount
}
