import type { Target } from '@/api/targets'

export interface TargetStatusInfo {
  label: string
  cls: string
}

// 目标状态徽标单一事实源：服务目标（探针）按在线/探测历史给 正常/异常/未探测，
// 设备目标给 在线/离线；item 为 null（详情页加载中）回退占位。
// TargetsView / TargetDetailView 共用，避免两处逻辑漂移
export function targetStatusInfo(target: Target | null): TargetStatusInfo {
  if (!target) return { label: '—', cls: 'offline' }
  if (target.type === 'service') {
    if (target.online) return { label: '正常', cls: 'online' }
    return target.lastSeenAtUtc ? { label: '异常', cls: 'alarm' } : { label: '未探测', cls: 'offline' }
  }
  return target.online ? { label: '在线', cls: 'online' } : { label: '离线', cls: 'offline' }
}
