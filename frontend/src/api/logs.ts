import { apiUrl } from './base'

export type LogKind = 'systemd' | 'docker'

export type LogLevel = 'error' | 'warn' | 'info' | 'debug'

export interface LogServiceInfo {
  name: string
  kind: LogKind
  description: string
}

export interface LogLineInfo {
  ts: string
  level: LogLevel | string
  message: string
}

async function request<T>(path: string): Promise<T> {
  const response = await fetch(apiUrl(path))
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
  return (await response.json()) as T
}

export async function listLogServices(deviceId: number): Promise<LogServiceInfo[]> {
  const payload = await request<{ services: LogServiceInfo[] }>(`/api/devices/${deviceId}/logs/services`)
  return payload.services
}

export async function fetchLogTail(
  deviceId: number,
  service: string,
  kind: LogKind,
  lines: number,
): Promise<LogLineInfo[]> {
  const query = new URLSearchParams({ service, kind, lines: String(lines) })
  const payload = await request<{ lines: LogLineInfo[] }>(`/api/devices/${deviceId}/logs/tail?${query.toString()}`)
  return payload.lines
}
