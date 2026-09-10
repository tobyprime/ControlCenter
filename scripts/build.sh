#!/usr/bin/env bash
# 一键构建：契约漂移校验 + 前端产物构建 + 后端 Release 发布
# 用法：scripts/build.sh
set -euo pipefail
cd "$(dirname "$0")/.."

echo "==> [1/3] API 契约漂移校验（重建 OpenAPI 文档 → 重生成 SDK → git diff 门禁）"
(cd frontend && npm run check:api-drift)

echo "==> [2/3] 构建前端（产物输出到 src/DevicePanel.Web/wwwroot）"
(cd frontend && npm ci --no-audit --no-fund && npm run build)

echo "==> [3/3] 发布后端（Release，内嵌前端产物）"
dotnet publish src/DevicePanel.Web -c Release -o artifacts/publish

echo "==> 构建完成：artifacts/publish/DevicePanel.Web.dll"
echo "    运行：dotnet artifacts/publish/DevicePanel.Web.dll --urls http://0.0.0.0:5000"
