// API/WSS 基址：默认空串 = 同源（内嵌形态不变）；
// Cloudflare Pages 等独立域部署时以 VITE_API_BASE_URL 构建期注入绝对地址（如 https://api.example.com）
const rawBase = (import.meta.env.VITE_API_BASE_URL ?? '').trim().replace(/\/+$/, '')

export function apiUrl(path: string): string {
  return `${rawBase}${path}`
}

export function wsUrl(path: string): string {
  if (rawBase) {
    return `${rawBase.replace(/^http/, 'ws')}${path}`
  }
  const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:'
  return `${protocol}//${location.host}${path}`
}

// TOB-401：HTTP 调用统一走 src/api/gen/ 生成的客户端，本文件是唯一的 fetch 适配层。
// 原手写 apiFetch 的三条语义在此收口：
// 1. 全部请求带凭据（credentials: 'include'）：同源内嵌形态行为不变；跨站形态（如 Cloudflare Pages
//    独立域 + Tunnel 后端）浏览器才会携带/存储跨站会话 Cookie（SameSite=None; Secure）。
//    fetch 默认 credentials 为 same-origin，跨站请求不带 Cookie、Set-Cookie 也会被丢弃。
// 2. VITE_API_BASE_URL 覆盖基址：注入生成客户端的 baseUrl。
// 3. 失败响应解析 { error } 文案，抛出 Error & { status }（与原 request<T> 完全同构），视图层捕获逻辑不变。
import { client } from './gen/client.gen'

const credentialFetch: typeof fetch = (input, init) => fetch(input, { ...init, credentials: 'include' })

client.setConfig({
  ...(rawBase ? { baseUrl: rawBase } : {}),
  fetch: credentialFetch,
  throwOnError: true,
})

client.interceptors.error.use((error, response) => {
  // 网络层异常（无 response）：保持原样上抛（原实现 fetch 直接 throw，如 TypeError: Failed to fetch）
  if (!response) {
    return error
  }

  let message = `请求失败（${response.status}）`
  if (error && typeof error === 'object' && 'error' in error && typeof (error as { error?: unknown }).error === 'string') {
    message = (error as { error: string }).error
  }
  const normalized = new Error(message) as Error & { status?: number }
  normalized.status = response.status
  return normalized
})
