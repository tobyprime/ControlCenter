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
