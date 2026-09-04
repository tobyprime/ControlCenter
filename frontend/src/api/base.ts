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
