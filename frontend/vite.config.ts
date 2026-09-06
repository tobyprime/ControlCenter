import { fileURLToPath, URL } from 'node:url'
import { writeFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { defineConfig, type Plugin } from 'vite'
import vue from '@vitejs/plugin-vue'
import { pagesCacheHeaders } from './pages-headers.mjs'

// 默认模式：构建产物输出到 ASP.NET Core 服务的 wwwroot，由后端内嵌承载（同源）
// pages 模式（--mode pages）：产物输出 dist/，可独立部署到 Cloudflare Pages——
//   静态托管无后端回退，附带 _redirects（SPA 路由全部回退 index.html）；
//   API/WSS 绝对地址以构建环境变量 VITE_API_BASE_URL 注入（如 VITE_API_BASE_URL=https://api.example.com）

// Cloudflare Pages SPA 回退 + 缓存策略（与后端内嵌形态的静态文件缓存策略对齐，TOB-373 发版排查）：
// - _redirects：所有未命中静态文件的路径返回 index.html；
// - _headers：HTML 壳 no-cache 回源校验（发版即换新，不依赖用户强刷），带 hash 的 /assets 长缓存 immutable
function cloudflarePagesSpaFallback(): Plugin {
  return {
    name: 'cloudflare-pages-spa-fallback',
    closeBundle() {
      const outDir = resolve(__dirname, 'dist')
      writeFileSync(resolve(outDir, '_redirects'), '/*    /index.html   200\n')
      // 内容在 pages-headers.mjs（node:test 断言规则结构，防多规则同名头合并回归）
      writeFileSync(resolve(outDir, '_headers'), pagesCacheHeaders)
    },
  }
}

export default defineConfig(({ mode }) => {
  const pagesMode = mode === 'pages'
  return {
    plugins: [vue(), ...(pagesMode ? [cloudflarePagesSpaFallback()] : [])],
    resolve: {
      alias: {
        '@': fileURLToPath(new URL('./src', import.meta.url)),
      },
    },
    server: {
      proxy: {
        '/api': 'http://localhost:5000',
      },
    },
    build: {
      outDir: pagesMode ? 'dist' : '../src/DevicePanel.Web/wwwroot',
      emptyOutDir: true,
    },
  }
})
