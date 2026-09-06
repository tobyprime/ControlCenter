// dist/_headers 缓存策略的结构约束（TOB-373 审查问题 1）：
// Cloudflare _headers 对同一路径命中的多条规则全部生效，同名头按逗号合并——
// 无限定 catch-all 的 no-cache 若与 /assets/* immutable 并存，资源实际响应将是
// 「no-cache, public, max-age=31536000, immutable」，长缓存目标落空。
// 因此结构上必须保证：任何路径至多命中一条含 Cache-Control 的规则。
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { pagesCacheHeaders } from './pages-headers.mjs'

// 按 Cloudflare _headers 文法解析：路径行 + 缩进「头: 值」行
function parseRules(text) {
  const rules = []
  for (const line of text.split('\n')) {
    if (!line.trim()) continue
    if (line.startsWith(' ')) {
      const [name, ...rest] = line.trim().split(':')
      rules.at(-1).headers.push([name.trim(), rest.join(':').trim()])
    } else {
      rules.push({ path: line.trim(), headers: [] })
    }
  }
  return rules
}

function pathMatches(rulePath, requestPath) {
  if (rulePath.endsWith('*')) return requestPath.startsWith(rulePath.slice(0, -1))
  return rulePath === requestPath
}

const cacheRules = parseRules(pagesCacheHeaders).filter((r) =>
  r.headers.some(([name]) => name.toLowerCase() === 'cache-control'),
)

test('任何路径至多命中一条 Cache-Control 规则（多规则同名头合并会破坏缓存语义）', () => {
  const samplePaths = ['/', '/index.html', '/assets/index-B0b1ds1E.js', '/assets/x/y.css', '/login', '/collectors']
  for (const p of samplePaths) {
    const hits = cacheRules.filter((r) => pathMatches(r.path, p))
    assert.ok(
      hits.length <= 1,
      `路径 ${p} 同时命中 ${hits.length} 条 Cache-Control 规则（${hits.map((h) => h.path).join(', ')}），同名头将被合并`,
    )
  }
})

test('/assets/* 输出 immutable 长缓存且不含 no-cache', () => {
  const rule = cacheRules.find((r) => r.path === '/assets/*')
  assert.ok(rule, '缺少 /assets/* 规则')
  const value = rule.headers.find(([name]) => name.toLowerCase() === 'cache-control')?.[1] ?? ''
  assert.match(value, /max-age=31536000/)
  assert.match(value, /immutable/)
  assert.doesNotMatch(value, /no-cache/)
})

test('HTML 入口（/ 与 /index.html）保持 no-cache 回源校验', () => {
  for (const path of ['/', '/index.html']) {
    const rule = cacheRules.find((r) => r.path === path)
    assert.ok(rule, `缺少 ${path} 规则`)
    const value = rule.headers.find(([name]) => name.toLowerCase() === 'cache-control')?.[1] ?? ''
    assert.match(value, /no-cache/, `${path} 应 no-cache（发版即换新）`)
  }
})
