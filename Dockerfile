# 面板服务镜像：前端构建 → 后端发布（内嵌前端产物）→ 运行时镜像
# 构建：scripts/build-image.sh（等价于 docker build -t <image> .）
# 运行：容器内数据目录固定 /data，部署时必须挂载卷（k3s manifests 已按此配置）
# syntax=docker/dockerfile:1

# ---- 阶段 1：前端构建（产物输出 /repo/src/DevicePanel.Web/wwwroot）----
FROM node:20-alpine AS frontend
WORKDIR /repo/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY frontend/ ./
RUN npm run build

# ---- 阶段 2：后端发布（Release，恢复层缓存友好）----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS backend
WORKDIR /repo
COPY Directory.Build.props ./
COPY src/DevicePanel.Protocol/DevicePanel.Protocol.csproj ./src/DevicePanel.Protocol/
COPY src/DevicePanel.Web/DevicePanel.Web.csproj ./src/DevicePanel.Web/
RUN dotnet restore src/DevicePanel.Web
COPY src/DevicePanel.Protocol/ ./src/DevicePanel.Protocol/
COPY src/DevicePanel.Web/ ./src/DevicePanel.Web/
RUN dotnet publish src/DevicePanel.Web -c Release --no-restore -o /app/publish \
    && rm -rf /app/publish/wwwroot
# 前端产物以本镜像构建为准（仓库内 wwwroot 仅为构建结果备份，不作为镜像来源）
COPY --from=frontend /repo/src/DevicePanel.Web/wwwroot/ /app/publish/wwwroot/

# ---- 阶段 3：运行时 ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim
WORKDIR /app
COPY --from=backend /app/publish/ ./
# .NET 8 基础镜像默认 ASPNETCORE_HTTP_PORTS=8080，监听 0.0.0.0:8080
EXPOSE 8080
# 镜像契约：数据目录固定 /data，部署时必须挂载卷（k3s manifests 已按此配置）
ENV DEVICEPANEL__DATADIR=/data
# 非 root 运行；/data 由部署挂载并提供归属（k3s manifests 的 initContainer 负责 chown）
USER app
ENTRYPOINT ["dotnet", "DevicePanel.Web.dll"]
