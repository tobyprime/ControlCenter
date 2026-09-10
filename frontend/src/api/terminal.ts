// TOB-401：留痕查询走生成客户端；WebSocket 入口保持手写薄封装（WS 不生成）。
import { getApiTerminalSessions, getApiTerminalSessionsRecords } from './gen'
import { wsUrl } from './base'

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
  const query: Record<string, string | number> = {}
  if (deviceId !== undefined) {
    query.deviceId = deviceId
  }
  if (fromIso) {
    query.from = fromIso
  }
  if (toIso) {
    query.to = toIso
  }
  const { data } = await getApiTerminalSessions<true>({ query })
  return data as TerminalSessionInfo[]
}

export async function listTerminalRecords(sessionId: string): Promise<TerminalRecordInfo[]> {
  const { data } = await getApiTerminalSessionsRecords<true>({ path: { sessionId } })
  return data as TerminalRecordInfo[]
}

// 浏览器终端 WebSocket 地址（同源或绝对地址，会话 Cookie 随请求携带）
export function terminalWebSocketUrl(deviceId: number, cols: number, rows: number): string {
  const query = new URLSearchParams({ cols: String(cols), rows: String(rows) })
  return wsUrl(`/api/devices/${deviceId}/terminal?${query.toString()}`)
}
