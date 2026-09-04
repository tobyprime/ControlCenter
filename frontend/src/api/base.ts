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

// 全部请求带凭据（include）：同源内嵌形态行为不变；跨站形态（如 Cloudflare Pages
// 独立域 + Tunnel 后端）浏览器才会携带/存储跨站会话 Cookie（SameSite=None; Secure）。
// fetch 默认 credentials 为 same-origin，跨站请求不带 Cookie、Set-Cookie 也会被丢弃。
export function apiFetch(path: string, init?: RequestInit): Promise<Response> {
  return fetch(apiUrl(path), { credentials: 'include', ...init })
}
