import { apiFetch } from './base'

export type TargetType = 'device' | 'service'

export interface Target {
  id: number
  type: TargetType
  name: string
  tags: string[]
  createdAtUtc: string
  updatedAtUtc: string
  lastSeenAtUtc?: string | null
  online: boolean
}

export interface TargetCreated extends Target {
  agentToken: string
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

export function listTargets(): Promise<Target[]> {
  return request<Target[]>('/api/targets')
}

export function getTarget(id: number): Promise<Target> {
  return request<Target>(`/api/targets/${id}`)
}

export function createTarget(name: string, tags: string[]): Promise<TargetCreated> {
  return request<TargetCreated>('/api/targets', {
    method: 'POST',
    body: JSON.stringify({ name, tags }),
  })
}

export function updateTarget(id: number, name: string, tags: string[]): Promise<Target> {
  return request<Target>(`/api/targets/${id}`, {
    method: 'PUT',
    body: JSON.stringify({ name, tags }),
  })
}

export function deleteTarget(id: number): Promise<void> {
  return request<void>(`/api/targets/${id}`, { method: 'DELETE' })
}

export function resetTargetToken(id: number): Promise<{ agentToken: string }> {
  return request<{ agentToken: string }>(`/api/targets/${id}/token`, {
    method: 'POST',
    body: JSON.stringify({}),
  })
}
