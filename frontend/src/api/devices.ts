import { apiUrl } from './base'

export interface Device {
  id: number
  name: string
  tags: string[]
  createdAtUtc: string
  updatedAtUtc: string
  lastSeenAtUtc?: string | null
  online: boolean
}

export interface DeviceCreated extends Device {
  agentToken: string
}

async function request<T>(input: string, init?: RequestInit): Promise<T> {
  const response = await fetch(apiUrl(input), {
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

export function listDevices(): Promise<Device[]> {
  return request<Device[]>('/api/devices')
}

export function createDevice(name: string, tags: string[]): Promise<DeviceCreated> {
  return request<DeviceCreated>('/api/devices', {
    method: 'POST',
    body: JSON.stringify({ name, tags }),
  })
}

export function updateDevice(id: number, name: string, tags: string[]): Promise<Device> {
  return request<Device>(`/api/devices/${id}`, {
    method: 'PUT',
    body: JSON.stringify({ name, tags }),
  })
}

export function deleteDevice(id: number): Promise<void> {
  return request<void>(`/api/devices/${id}`, { method: 'DELETE' })
}

export function resetDeviceToken(id: number): Promise<{ agentToken: string }> {
  return request<{ agentToken: string }>(`/api/devices/${id}/token`, {
    method: 'POST',
    body: JSON.stringify({}),
  })
}
