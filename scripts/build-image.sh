#!/usr/bin/env bash
# 面板镜像构建：前端构建 + 后端发布均在镜像内多阶段完成，可重复执行
# 用法：scripts/build-image.sh [镜像名:标签]   （默认 controlcenter-panel:local）
set -euo pipefail
cd "$(dirname "$0")/.."

IMAGE="${1:-controlcenter-panel:local}"
# 构建容器无法直连外网（npm/NuGet 拉取失败）的环境：DOCKER_BUILD_NETWORK=host 复用宿主网络
NETWORK_ARGS=()
if [ -n "${DOCKER_BUILD_NETWORK:-}" ]; then
  NETWORK_ARGS=(--network "$DOCKER_BUILD_NETWORK")
fi
docker build "${NETWORK_ARGS[@]}" -t "$IMAGE" .

echo "==> 镜像构建完成：$IMAGE"
echo "    本地运行：docker run -d -p 8080:8080 -v panel-data:/data $IMAGE"
echo "    导入 k3s：docker save $IMAGE | sudo k3s ctr images import -"
