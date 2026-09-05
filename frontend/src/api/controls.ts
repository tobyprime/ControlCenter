import { apiFetch } from './base'

/** 控制类型注册表条目（后端 ControlTypeCatalog 清单：新增类型 = 注册 IControlType 后自动出现）。 */
export interface ControlTypeInfo {
  key: string
  displayName: string
}

/** 采集器上已声明的控制器实体（agent 能力上报的持久化副本；paramsSchema 为该类型声明的原始 JSON）。 */
export interface ControllerDeclaration {
  key: string
  type: string
  label: string
  tags: string[]
  paramsSchema: unknown
}

/** 一次控制下发的结论：status ∈ success / failure / timeout（离线也以 failure 留痕，HTTP 409 表达）。 */
export interface ControlInvokeOutcome {
  status: string
  message: string | null
}

/** 一条控制留痕（后端 ControlLogEntry）：何时、哪台采集器上的哪个控制器、谁、带了什么参数、结果如何。 */
export interface ControlLogEntry {
  id: number
  collectorId: number
  controllerKey: string
  controllerType: string
  controllerLabel: string
  operator: string
  parameters: unknown
  status: string
  resultMessage: string | null
  createdAtUtc: string
}

/** 下发状态展示名（与后端 ControlLogStatuses 一致）。 */
export const CONTROL_STATUS_LABELS: Record<string, string> = {
  success: '成功',
  failure: '失败',
  timeout: '超时',
}

export function controlStatusLabel(status: string): string {
  return CONTROL_STATUS_LABELS[status] ?? status
}

// 内置控制类型展示名（与后端 ControlTypeCatalog 一致；面板侧按类型渲染，新增类型需前端补充渲染支持）
export const CONTROL_TYPE_LABELS: Record<string, string> = {
  button: '按钮',
  toggle: '开关',
  input: '输入框',
  slider: '滑块',
}

export function controlTypeLabel(type: string): string {
  return CONTROL_TYPE_LABELS[type] ?? type
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

/** 控制类型注册表清单（验收4 的对外表面）。 */
export function listControlTypes(): Promise<{ types: ControlTypeInfo[] }> {
  return request<{ types: ControlTypeInfo[] }>('/api/controls/types')
}

/** 采集器已声明的控制器实体（来自 agent 能力上报）。 */
export function listCollectorControllers(id: number): Promise<{ controllers: ControllerDeclaration[] }> {
  return request<{ controllers: ControllerDeclaration[] }>(`/api/collectors/${id}/controllers`)
}

/** 下发一次控制并即时取回结论；失败语义：离线 409 / agent 报错 502 / 超时 504，错误体为 { error, status }。 */
export function invokeController(id: number, key: string, params: unknown): Promise<ControlInvokeOutcome> {
  return request<ControlInvokeOutcome>(`/api/collectors/${id}/controllers/${encodeURIComponent(key)}/invoke`, {
    method: 'POST',
    body: JSON.stringify({ params }),
  })
}

export interface ControlLogQuery {
  collectorId?: number
  controllerKey?: string
  fromUtc?: string
  toUtc?: string
  limit?: number
}

/** 控制留痕查询：按控制器/时间筛选，最新在前。 */
export function listControlLogs(query: ControlLogQuery = {}): Promise<{ logs: ControlLogEntry[] }> {
  const search = new URLSearchParams()
  if (query.collectorId !== undefined) {
    search.set('collectorId', String(query.collectorId))
  }
  if (query.controllerKey) {
    search.set('controllerKey', query.controllerKey)
  }
  if (query.fromUtc) {
    search.set('from', query.fromUtc)
  }
  if (query.toUtc) {
    search.set('to', query.toUtc)
  }
  if (query.limit !== undefined) {
    search.set('limit', String(query.limit))
  }
  const qs = search.toString()
  return request<{ logs: ControlLogEntry[] }>(`/api/controls/logs${qs ? `?${qs}` : ''}`)
}
