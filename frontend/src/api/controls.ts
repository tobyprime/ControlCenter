// TOB-401：实现由 openapi-ts 生成客户端承载（契约见 src/api/gen/），本模块只保留视图层消费的类型与函数签名。
import { getApiCollectorsControllers, getApiControlsLogs, getApiControlsTypes, postApiCollectorsControllersInvoke } from './gen'

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

/** 控制类型注册表清单（验收4 的对外表面）。 */
export async function listControlTypes(): Promise<{ types: ControlTypeInfo[] }> {
  const { data } = await getApiControlsTypes<true>({})
  return data as { types: ControlTypeInfo[] }
}

/** 采集器已声明的控制器实体（来自 agent 能力上报）。 */
export async function listCollectorControllers(id: number): Promise<{ controllers: ControllerDeclaration[] }> {
  const { data } = await getApiCollectorsControllers<true>({ path: { collectorId: id } })
  return data as { controllers: ControllerDeclaration[] }
}

/** 下发一次控制并即时取回结论；失败语义：离线 409 / agent 报错 502 / 超时 504，错误体为 { error, status }。 */
export async function invokeController(id: number, key: string, params: unknown): Promise<ControlInvokeOutcome> {
  const { data } = await postApiCollectorsControllersInvoke<true>({
    path: { collectorId: id, key },
    body: { params },
  })
  return data as ControlInvokeOutcome
}

export interface ControlLogQuery {
  collectorId?: number
  controllerKey?: string
  fromUtc?: string
  toUtc?: string
  limit?: number
}

/** 控制留痕查询：按控制器/时间筛选，最新在前。 */
export async function listControlLogs(query: ControlLogQuery = {}): Promise<{ logs: ControlLogEntry[] }> {
  const search: Record<string, string | number> = {}
  if (query.collectorId !== undefined) {
    search.collectorId = query.collectorId
  }
  if (query.controllerKey) {
    search.controllerKey = query.controllerKey
  }
  if (query.fromUtc) {
    search.from = query.fromUtc
  }
  if (query.toUtc) {
    search.to = query.toUtc
  }
  if (query.limit !== undefined) {
    search.limit = query.limit
  }
  const { data } = await getApiControlsLogs<true>({ query: search })
  return data as { logs: ControlLogEntry[] }
}
