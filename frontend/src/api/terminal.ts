import { apiUrl, wsUrl } from './base'

export interface TerminalSessionInfo {
  id: string
  deviceId: number
  deviceName: string
  operator: string
  openedAtUtc: string
  closedAtUtc?: string | null
  closeReason?: string | null
}

export interface TerminalRecordInfo {
  id: number
  sessionId: string
  direction: 'input' | 'output'
  data: string
  recordedAtUtc: string
}

export async function listTerminalSessions(
  deviceId?: number,
  fromIso?: string,
  toIso?: string,
): Promise<TerminalSessionInfo[]> {
  const query = new URLSearchParams()
  if (deviceId !== undefined) {
    query.set('deviceId', String(deviceId))
  }
  if (fromIso) {
    query.set('from', fromIso)
  }
  if (toIso) {
    query.set('to', toIso)
  }
  const suffix = query.size > 0 ? `?${query.toString()}` : ''
  const response = await fetch(apiUrl(`/api/terminal/sessions${suffix}`))
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
  return (await response.json()) as TerminalSessionInfo[]
}

export async function listTerminalRecords(sessionId: string): Promise<TerminalRecordInfo[]> {
  const response = await fetch(apiUrl(`/api/terminal/sessions/${encodeURIComponent(sessionId)}/records`))
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
  return (await response.json()) as TerminalRecordInfo[]
}

// 浏览器终端 WebSocket 地址（同源或绝对地址，会话 Cookie 随请求携带）
export function terminalWebSocketUrl(deviceId: number, cols: number, rows: number): string {
  const query = new URLSearchParams({ cols: String(cols), rows: String(rows) })
  return wsUrl(`/api/devices/${deviceId}/terminal?${query.toString()}`)
}
