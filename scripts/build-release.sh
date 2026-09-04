#!/usr/bin/env bash
# 一键发布构建：面板本机产物 + 容器镜像 + agent 双架构交叉编译（可重复执行）
#
# 产物：
#   artifacts/publish/                       面板运行产物（前端构建 + 后端发布，本机运行用）
#   <镜像名:标签>                             面板容器镜像（k3s 部署用，多阶段自包含）
#   artifacts/agent/linux-{amd64,arm64}/     agent 静态单二进制（目标机零依赖）
#
# 用法：
#   scripts/build-release.sh                  # 全部：本机产物 + 镜像 + agent amd64/arm64
#   scripts/build-release.sh image            # 仅容器镜像（仅依赖 docker）
#   scripts/build-release.sh host             # 仅本机产物（依赖 .NET 8 SDK、Node 20+）
#   scripts/build-release.sh agent            # 仅 agent 双架构
#   scripts/build-release.sh agent amd64      # 仅 agent 单架构
#
# agent 交叉编译前置（详见 scripts/build-agent.sh 头注）：
#   OPENSSL_PREFIX（目标架构 musl 静态 OpenSSL 布局）、musl 交叉工具链、静态 zlib、cmake
set -euo pipefail
cd "$(dirname "$0")/.."

TARGET="${1:-all}"

usage() {
  echo "用法：$0 [image|host|agent|all] （agent 后可接 amd64|arm64）" >&2
}

case "$TARGET" in
  image)
    scripts/build-image.sh "${IMAGE:-controlcenter-panel:local}"
    ;;
  host)
    scripts/build.sh
    ;;
  agent)
    shift || true
    scripts/build-agent.sh "${1:-all}"
    ;;
  all)
    scripts/build.sh
    scripts/build-image.sh "${IMAGE:-controlcenter-panel:local}"
    scripts/build-agent.sh all
    ;;
  *)
    usage
    exit 1
    ;;
esac
