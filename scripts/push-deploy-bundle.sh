#!/usr/bin/env bash
# 真实环境部署包组装与推送（TOB-357）。
# 在构建机（可 docker 构建、可 SSH 到集群节点）执行：
#   1. 构建面板镜像并 docker save
#   2. 拉取/复用 busybox:1.36（集群内探针与 init 容器用，避免节点外网拉取失败）
#   3. 组装部署包（manifests + deploy.sh + env 模板）
#   4. rsync 推送到集群节点，远端 sha256 校验
# 敏感的 secret.env 由部署执行方在节点上按 secret.env.example 填写，不经本脚本分发。
#
# 用法：scripts/push-deploy-bundle.sh [镜像标签]
# 可调环境变量：
#   NODE_SSH   集群节点 SSH 目标（默认 toby@192.168.3.74，落盘 /home/toby/controlcenter）
#   IMAGE_TAG  面板镜像标签（默认 0.1.0-deploy.1）
set -euo pipefail
cd "$(dirname "$0")/.."

IMAGE_TAG="${1:-${IMAGE_TAG:-0.1.0-deploy.1}}"
IMAGE="controlcenter-panel:${IMAGE_TAG}"
NODE_SSH="${NODE_SSH:-toby@192.168.3.74}"
REMOTE_DIR="/home/toby/controlcenter"
BUNDLE="artifacts/deploy-bundle"

echo "==> 1/4 构建面板镜像 ${IMAGE}"
scripts/build-image.sh "${IMAGE}"

echo "==> 2/4 准备 busybox:1.36"
docker image inspect busybox:1.36 >/dev/null 2>&1 || docker pull busybox:1.36

echo "==> 3/4 组装部署包 ${BUNDLE}/"
rm -rf "${BUNDLE}"
mkdir -p "${BUNDLE}/images" "${BUNDLE}/manifests"
docker save -o "${BUNDLE}/images/controlcenter-panel.tar" "${IMAGE}"
docker save -o "${BUNDLE}/images/busybox-1.36.tar" busybox:1.36
cp deploy/k3s/*.yaml "${BUNDLE}/manifests/"
cp deploy/real-deploy/deploy.sh deploy/real-deploy/deploy.env.example deploy/real-deploy/secret.env.example "${BUNDLE}/"
cp deploy/real-deploy/README.md "${BUNDLE}/"
chmod +x "${BUNDLE}/deploy.sh"
( cd "${BUNDLE}" && find . -type f ! -name sha256sums -exec sha256sum {} + > sha256sums )

echo "==> 4/4 推送到 ${NODE_SSH}:${REMOTE_DIR}"
ssh "${NODE_SSH}" "mkdir -p ${REMOTE_DIR}"
rsync -az --info=progress2 "${BUNDLE}/" "${NODE_SSH}:${REMOTE_DIR}/"
ssh "${NODE_SSH}" "cd ${REMOTE_DIR} && sha256sum -c sha256sums --quiet && echo '远端校验 OK' && ls -la"

echo
echo "部署包已就绪。接下来在集群节点以 root 执行："
echo "  ctr -n k8s.io images import ${REMOTE_DIR}/images/controlcenter-panel.tar"
echo "  ctr -n k8s.io images import ${REMOTE_DIR}/images/busybox-1.36.tar"
echo "  cp ${REMOTE_DIR}/deploy.env.example ${REMOTE_DIR}/deploy.env && vi ${REMOTE_DIR}/deploy.env"
echo "  cp ${REMOTE_DIR}/secret.env.example ${REMOTE_DIR}/secret.env && chmod 600 ${REMOTE_DIR}/secret.env && vi ${REMOTE_DIR}/secret.env"
echo "  cd ${REMOTE_DIR} && ./deploy.sh"
echo "完整步骤与验证见 ${REMOTE_DIR}/README.md"
