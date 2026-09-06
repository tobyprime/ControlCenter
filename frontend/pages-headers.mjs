// Pages _headers 缓存策略文本（TOB-373 发版排查），由 vite pages 模式构建写入 dist/_headers。
// 单独成模块：node:test 可直接断言规则结构（见 pages-headers.test.mjs）。
//
// 注意 Cloudflare _headers 语义：同一路径命中的多条规则全部生效，同名头按逗号合并。
// 因此 HTML 入口用精确路径（/ 与 /index.html），不用 /* catch-all——否则 assets 的
// immutable 会被合并上 no-cache，长缓存目标落空。SPA 深链路由（如 /login）无规则命中，
// 回落 Pages 默认 public, max-age=0, must-revalidate（每次回源校验，发版即换新）。
export const pagesCacheHeaders = [
  '/',
  '  Cache-Control: no-cache',
  '/index.html',
  '  Cache-Control: no-cache',
  '/assets/*',
  '  Cache-Control: public, max-age=31536000, immutable',
  '',
].join('\n')
