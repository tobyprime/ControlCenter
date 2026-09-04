import { apiFetch } from './base'

// 交互模式（约束 C）：核心按目标声明的模式渲染入口，不绑定「控制台」单一形态
export interface InteractionModeInfo {
  key: string
  displayName: string
  description?: string | null
}

async function request<T>(input: string): Promise<T> {
  const response = await apiFetch(input, {
    headers: { 'Content-Type': 'application/json' },
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
  return (await response.json()) as T
}

// 全部已注册交互模式（模式注册表清单）
export function listInteractionModes(): Promise<InteractionModeInfo[]> {
  return request<InteractionModeInfo[]>('/api/interactions/modes')
}

// 目标声明的交互模式：入口渲染的数据源；目标不存在返回 404，未声明返回空列表
export function listDeviceInteractionModes(deviceId: number): Promise<InteractionModeInfo[]> {
  return request<InteractionModeInfo[]>(`/api/devices/${deviceId}/interaction-modes`)
}
