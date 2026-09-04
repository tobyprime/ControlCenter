import { apiFetch } from './base'

// 布局契约（TOB-366）：单用户单套，整份布局读写；
// 卡片条目含 id、类型、排序、显隐与 config（config 后端只透传不解释语义）。
export interface DashboardCard {
  id: string
  type: string
  visible: boolean
  order: number
  config: Record<string, unknown>
}

export interface DashboardLayout {
  cards: DashboardCard[]
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

// 无记录时服务端返回默认布局；前端对失败/空结果另行回退，见 dashboard/cards.ts
export function fetchDashboardLayout(): Promise<DashboardLayout> {
  return request<DashboardLayout>('/api/dashboard/layout')
}

export function saveDashboardLayout(cards: DashboardCard[]): Promise<DashboardLayout> {
  return request<DashboardLayout>('/api/dashboard/layout', {
    method: 'PUT',
    body: JSON.stringify({ cards }),
  })
}
