// 主页控制卡的 config 契约（后端同规则校验）：{ controllers: [{ collectorId, key }] } 非空数组。
// 与指标卡的 parseMetricCardConfig 同语义：非法整体返回 null，卡片走「未配置」态，不渲染半套控件
export interface ControlCardControllerRef {
  collectorId: number
  key: string
}

export interface ControlCardConfig {
  controllers: ControlCardControllerRef[]
}

export function parseControlCardConfig(config: unknown): ControlCardConfig | null {
  if (typeof config !== 'object' || config === null) {
    return null
  }
  const raw = (config as Record<string, unknown>).controllers
  if (!Array.isArray(raw) || raw.length === 0) {
    return null
  }
  const controllers: ControlCardControllerRef[] = []
  for (const item of raw) {
    if (typeof item !== 'object' || item === null) {
      return null
    }
    const collectorId = (item as Record<string, unknown>).collectorId
    const key = (item as Record<string, unknown>).key
    if (typeof collectorId !== 'number' || !Number.isInteger(collectorId) || collectorId <= 0) {
      return null
    }
    if (typeof key !== 'string' || key.trim() === '') {
      return null
    }
    controllers.push({ collectorId, key })
  }
  return { controllers }
}
