// TOB-401：实现由 openapi-ts 生成客户端承载（契约见 src/api/gen/），本模块只保留视图层消费的类型与函数签名。
import { getApiDevicesInteractionModes, getApiInteractionsModes } from './gen'

// 交互模式（约束 C）：核心按目标声明的模式渲染入口，不绑定「控制台」单一形态
export interface InteractionModeInfo {
  key: string
  displayName: string
  description?: string | null
}

// 全部已注册交互模式（模式注册表清单）
export async function listInteractionModes(): Promise<InteractionModeInfo[]> {
  const { data } = await getApiInteractionsModes<true>({})
  return data as InteractionModeInfo[]
}

// 目标声明的交互模式：入口渲染的数据源；目标不存在返回 404，未声明返回空列表
export async function listDeviceInteractionModes(deviceId: number): Promise<InteractionModeInfo[]> {
  const { data } = await getApiDevicesInteractionModes<true>({ path: { deviceId } })
  return data as InteractionModeInfo[]
}
