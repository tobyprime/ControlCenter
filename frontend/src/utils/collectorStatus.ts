import type { Collector } from '@/api/collectors'

export interface CollectorStatusInfo {
  label: string
  cls: string
}

// 采集器状态徽标单一事实源：pull（面板轮询，服务语义经内置标签保留）按在线/探测历史给 正常/异常/未探测，
// push（agent 上报，设备语义）给 在线/离线；null（详情页加载中）回退占位。
// CollectorsView / CollectorDetailView 共用，避免两处逻辑漂移
export function collectorStatusInfo(collector: Collector | null): CollectorStatusInfo {
  if (!collector) return { label: '—', cls: 'offline' }
  if (collector.mode === 'pull') {
    if (collector.online) return { label: '正常', cls: 'online' }
    return collector.lastSeenAtUtc ? { label: '异常', cls: 'alarm' } : { label: '未探测', cls: 'offline' }
  }
  return collector.online ? { label: '在线', cls: 'online' } : { label: '离线', cls: 'offline' }
}
