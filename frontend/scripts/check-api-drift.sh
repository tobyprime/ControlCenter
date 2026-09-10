#!/usr/bin/env bash
# TOB-401 API 契约漂移校验：
#   1. 构建期重新生成 OpenAPI 文档（dotnet build 触发 NSwag，不启动服务）
#   2. 以文档重新生成前端 SDK（npm run gen:api）
#   3. git diff 校验生成产物与仓库提交一致；有漂移则非零退出（CI/构建门禁）
# 用法：frontend/scripts/check-api-drift.sh（在仓库任意目录可执行）
set -euo pipefail
cd "$(dirname "$0")/../.."

echo "==> [1/3] dotnet build 重新生成 OpenAPI 文档（artifacts/openapi/）"
dotnet build src/DevicePanel.Web/DevicePanel.Web.csproj --nologo -v q

echo "==> [2/3] openapi-ts 重新生成前端 SDK（frontend/src/api/gen/）"
(cd frontend && npm run gen:api --silent)

echo "==> [3/3] git diff 校验生成产物无漂移"
if git diff --exit-code -- frontend/src/api/gen; then
  echo "==> 契约无漂移：生成 SDK 与仓库提交一致"
else
  echo "==> 检测到 API 契约漂移：后端接口与已提交的前端 SDK 不一致！" >&2
  echo "    请运行 (cd frontend && npm run gen:api) 重新生成并一并提交。" >&2
  exit 1
fi
