#!/usr/bin/env bash
# 面板真实环境部署脚本（TOB-357）：在 k3s 集群节点执行。
# 前置：镜像与 busybox tar 已导入 containerd（k8s.io 命名空间）；同目录有 deploy.env 与 secret.env；
#       执行身份需具备集群管理权限（kubeconfig，如节点用户 toby 的 ~/.kube/config），无需 root。
# 用法：./deploy.sh [deploy.env 路径]   （secret.env 固定在同目录）
set -euo pipefail
cd "$(dirname "$0")"

ENV_FILE="${1:-deploy.env}"
[ -f "$ENV_FILE" ] || { echo "缺少 $ENV_FILE（可从 deploy.env.example 复制填写）"; exit 1; }
[ -f "secret.env" ] || { echo "缺少 secret.env（可从 secret.env.example 复制填写，chmod 600）"; exit 1; }
# shellcheck disable=SC1090
source "$ENV_FILE"
# shellcheck disable=SC1091
source secret.env

: "${IMAGE:?deploy.env 缺 IMAGE}"
: "${PANEL_PUBLIC_BASE_URL:?deploy.env 缺 PANEL_PUBLIC_BASE_URL}"
: "${CORS_ALLOWED_ORIGINS:?deploy.env 缺 CORS_ALLOWED_ORIGINS}"
: "${COOKIE_SAMESITE:?deploy.env 缺 COOKIE_SAMESITE}"
: "${DEVICEPANEL__AUTH__INITIALUSERNAME:?secret.env 缺初始账号}"
: "${DEVICEPANEL__AUTH__INITIALPASSWORD:?secret.env 缺初始密码}"
# 拦下未替换的占位符（如 http://<napcat 地址>:3000），避免把模板值种进面板
for _v in "$DEVICEPANEL__AUTH__INITIALPASSWORD" "${DEVICEPANEL__ALERT__NAPCAT__BASEURL:-}" \
          "${DEVICEPANEL__ALERT__NAPCAT__TOKEN:-}" "${DEVICEPANEL__ALERT__NAPCAT__TARGETID:-}"; do
  if [[ "$_v" == *'<'* ]]; then
    echo "secret.env/deploy.env 仍有未替换的占位符值：${_v:0:24}…，请先填真实值"; exit 1
  fi
done

echo "==> 1/5 命名空间与存储"
kubectl apply -f manifests/00-namespace.yaml
kubectl apply -f manifests/10-pvc.yaml

echo "==> 2/5 ConfigMap（真实部署参数）"
kubectl -n controlcenter apply -f - <<EOF
apiVersion: v1
kind: ConfigMap
metadata:
  name: device-panel-config
  namespace: controlcenter
  labels:
    app.kubernetes.io/part-of: controlcenter
data:
  DEVICEPANEL__DATADIR: /data
  PANEL_PUBLIC_BASE_URL: "${PANEL_PUBLIC_BASE_URL}"
  PANEL_AGENT_WSS_PATH: /agent/ws
  DevicePanel__Cors__AllowedOrigins: "${CORS_ALLOWED_ORIGINS}"
  DevicePanel__Auth__SessionCookieSameSite: "${COOKIE_SAMESITE}"
EOF

echo "==> 3/5 Secret（敏感配置）"
kubectl -n controlcenter create secret generic device-panel-secret \
  --from-literal="DevicePanel__Auth__InitialUsername=${DEVICEPANEL__AUTH__INITIALUSERNAME}" \
  --from-literal="DevicePanel__Auth__InitialPassword=${DEVICEPANEL__AUTH__INITIALPASSWORD}" \
  --from-literal="DevicePanel__Alert__Napcat__BaseUrl=${DEVICEPANEL__ALERT__NAPCAT__BASEURL}" \
  --from-literal="DevicePanel__Alert__Napcat__Token=${DEVICEPANEL__ALERT__NAPCAT__TOKEN}" \
  --from-literal="DevicePanel__Alert__Napcat__TargetType=${DEVICEPANEL__ALERT__NAPCAT__TARGETTYPE}" \
  --from-literal="DevicePanel__Alert__Napcat__TargetId=${DEVICEPANEL__ALERT__NAPCAT__TARGETID}" \
  --dry-run=client -o yaml | kubectl apply -f -

echo "==> 4/5 Deployment（镜像 ${IMAGE}）与 Service"
sed "s#image: controlcenter-panel:local#image: ${IMAGE}#" manifests/30-deployment.yaml | kubectl apply -f -
kubectl apply -f manifests/40-service.yaml

echo "==> 5/5 等待就绪并验证"
kubectl -n controlcenter rollout status deploy/device-panel --timeout=180s
POD="$(kubectl -n controlcenter get pod -l app.kubernetes.io/name=device-panel -o jsonpath='{.items[0].metadata.name}')"
kubectl -n controlcenter get pod "$POD" -o wide

# 经集群内 Service 验证健康检查（一次性 busybox 探针 Pod，输出即 /healthz 响应）
kubectl -n controlcenter delete pod probe-panel-health --ignore-not-found >/dev/null
kubectl -n controlcenter run probe-panel-health --image=busybox:1.36 --restart=Never \
  --command -- sh -c 'wget -qO- http://device-panel.controlcenter.svc.cluster.local/healthz; echo'
kubectl -n controlcenter wait --for=condition=Ready pod/probe-panel-health --timeout=120s >/dev/null
sleep 1
echo -n "healthz（集群内经 Service）: "
kubectl -n controlcenter logs probe-panel-health
kubectl -n controlcenter delete pod probe-panel-health --ignore-not-found >/dev/null

echo "部署完成。后续验证："
echo "  集群内 API 门禁: kubectl -n controlcenter run probe2 --rm -it --image=busybox:1.36 --restart=Never -- wget -qO- http://device-panel/api/devices"
echo "  （未登录应被拒：wget 输出 server replied error，对应 401 JSON）"
echo "  PVC 持久化验证（删 Pod 重建数据不丢）：见 README.md 第 5 步"
