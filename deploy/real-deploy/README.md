# 面板真实环境部署（TOB-357）— 节点侧执行手册

本目录是自包含部署包：镜像 tar、manifests、参数模板与本手册。部署范围是**业务负载**
（namespace `controlcenter` 内的 Deployment/PVC/Secret/ConfigMap/Service），不改任何集群基础组件。

## 0. 包内容

```
images/controlcenter-panel.tar   面板镜像（deploy.env 里 IMAGE 与之一致）
images/busybox-1.36.tar          busybox（init 容器 chown 与验证探针用，免节点外网拉取）
manifests/                       基础 manifests（与仓库 deploy/k3s/ 一致）
deploy.sh                        一键部署脚本（生成真实值 ConfigMap/Secret → apply → 验证）
deploy.env.example / secret.env.example
sha256sums                       完整性校验（推送方已生成，rsync 后应校验通过）
```

## 1. 导入镜像（root）

```bash
cd /home/toby/controlcenter
sha256sum -c sha256sums                 # 校验通过后继续
ctr -n k8s.io images import images/controlcenter-panel.tar
ctr -n k8s.io images import images/busybox-1.36.tar
crictl img | grep -E 'controlcenter-panel|busybox'   # 应能看到两个镜像
```

## 2. 填参数（root；secret 只落节点，不进 issue/git）

```bash
cp deploy.env.example deploy.env
vi deploy.env          # 填 PANEL_PUBLIC_BASE_URL（Tunnel 域名）、CORS_ALLOWED_ORIGINS（Pages 域名）、COOKIE_SAMESITE
cp secret.env.example secret.env && chmod 600 secret.env
vi secret.env          # 初始账号密码；napcat 地址优先填集群内 Service（webstack 现成，例如 http://<napcat-svc>.webstack.svc.cluster.local:<port>）+ token + 通知目标
```

 napcat 集群内地址确认：`kubectl -n webstack get svc`（选 OneBot HTTP 端口对应的服务名）。

## 3. 一键部署

```bash
./deploy.sh
```

脚本依次：namespace/PVC → ConfigMap（真实值，含 CORS 与 Cookie 策略）→ Secret → Deployment/Service →
rollout 等待 → 经集群内 Service 探针 `/healthz`。

## 4. 部署后验证（root）

```bash
# Pod Running、READY 1/1
kubectl -n controlcenter get pods -o wide

# 未登录被拒（期待 wget 报 server replied error，即 401）
kubectl -n controlcenter run probe-api --rm -it --image=busybox:1.36 --restart=Never -- \
  wget -qO- http://device-panel/api/devices

# PVC：数据文件在卷上
kubectl -n controlcenter get pvc
```

## 5. PVC 持久化实测（完成标准 1：删 Pod 重建数据不丢）

```bash
# 5.1 记录当前设备数（登录取 secret.env 里的账号；或直接看库）
kubectl -n controlcenter exec deploy/device-panel -- ls -la /data

# 5.2 删 Pod（PVC 不动）
POD=$(kubectl -n controlcenter get pod -l app.kubernetes.io/name=device-panel -o jsonpath='{.items[0].metadata.name}')
kubectl -n controlcenter delete pod "$POD"
kubectl -n controlcenter rollout status deploy/device-panel --timeout=180s

# 5.3 新 Pod 起来后：/data 内容与账号/数据仍在（登录原账号仍成功）
kubectl -n controlcenter exec deploy/device-panel -- ls -la /data
```

## 6. 需要回报给业务方的两个确认项

1. **napcat 集群内地址**：已填入 secret.env 的实际值（服务名:端口），以及从 controlcenter Pod
   `wget` 可达的验证结果：
   ```bash
   kubectl -n controlcenter run probe-napcat --rm -it --image=busybox:1.36 --restart=Never -- \
     wget -S -O /dev/null --header="Authorization: Bearer test" http://<napcat-svc>:<port>/get_login_info 2>&1 | head -5
   ```
2. **cloudflared 管理模式**（决定外网入口由谁配置）：
   - `kubectl -n webstack get deploy cloudflared -o yaml | grep -A2 args` ；
   - 若是 **token 远程管理**（Zero Trust 控制台配置 Public Hostname）→ 由用户按业务方给的清单在控制台添加；
   - 若是 **本地 config 管理**（ConfigMap/文件内 ingress 规则）→ 在 ingress 中加一条：
     `hostname: <Tunnel 域名> → service: http://device-panel.controlcenter.svc.cluster.local:80`（属集群内基础组件配置，由 EnvOps 操作）。

## 7. 升级与回滚

- 升级：新的镜像 tar 导入后 `kubectl -n controlcenter set image deploy/device-panel device-panel=<新镜像>`（strategy=Recreate，避免 SQLite 双写）。
- 回滚：`kubectl -n controlcenter rollout undo deploy/device-panel`；数据在 PVC，不受影响。
