#!/usr/bin/env bash
# 轻量 agent 交叉构建：Linux amd64 / arm64 静态单二进制（NativeAOT + musl + 内嵌静态 OpenSSL）
# 产物：artifacts/agent/linux-amd64/DevicePanel.Agent、artifacts/agent/linux-arm64/DevicePanel.Agent
# 验收：`file` 输出 static-pie linked；目标机零依赖（musl 与 OpenSSL 均静态链入）、零入站端口
#
# 用法：
#   OPENSSL_PREFIX=/path/to/openssl-musl-arm64 ./scripts/build-agent.sh [amd64|arm64|all]
#
# OPENSSL_PREFIX 要求（供 StaticOpenSslLinking 的 crypto shim 以 musl 目标编译/链接，
# 避免混入宿主 glibc 头）：目标架构的 OpenSSL 静态安装布局
#   $OPENSSL_PREFIX/include/openssl/*.h   （含该架构生成的 opensslconf.h）
#   $OPENSSL_PREFIX/lib/{libssl.a,libcrypto.a}
# 其 lib 亦作为链接期 -lssl/-lcrypto 的搜索路径之一（sysroot 内同名库亦可）。
#
# 其余依赖：
#   - dotnet SDK 8+、musl-tools（x64 侧 musl-gcc）、objcopy
#   - musl 静态 zlib（libz.a）：zlib 源码 `CC=<musl-gcc> ./configure --static && make`，
#     放入链接器搜索路径（x64: /usr/lib/x86_64-linux-musl/；arm64: 交叉工具链 sysroot lib/）
#   - arm64：aarch64 musl 交叉工具链（如 https://musl.cc/aarch64-linux-musl-cross.tgz），
#     bin 目录加入 PATH（提供 aarch64-linux-musl-gcc / -objcopy）
#   - cmake（crypto shim 以 cmake+make 构建）；cmake>=4 时脚本自动为 ilcompiler 包内的
#     CMakeLists 补 cmake_minimum_required 声明（幂等，仅改本机 NuGet 缓存）
# 说明：在 Alpine 容器内原生构建可省去交叉工具链与 musl 库准备（apk add zlib-static 即可）。
set -euo pipefail
cd "$(dirname "$0")/.."

OPENSSL_PREFIX="${OPENSSL_PREFIX:?请设置 OPENSSL_PREFIX（目标架构 musl 静态 OpenSSL 布局：include/openssl/*.h + lib/*.a）}"
TARGET="${1:-all}"

# NuGet 全局缓存目录动态解析（不硬编码用户路径）；为 cmake>=4 补齐 ilcompiler shim 源码缺失的
# cmake_minimum_required 声明（缺失时 cmake 4 拒绝配置）。仅写本机 NuGet 缓存，幂等。
NUGET_GLOBAL="$(dotnet nuget locals global-packages --list | sed 's/^global-packages: //')"
for f in "$NUGET_GLOBAL"/runtime.*.microsoft.dotnet.ilcompiler/*/native/src/libs/*/CMakeLists.txt; do
  [ -f "$f" ] || continue
  grep -q cmake_minimum_required "$f" || sed -i '1i cmake_minimum_required(VERSION 3.5)' "$f"
done

# 传给 build-local.sh 内部 cmake 的环境：CMAKE_PREFIX_PATH 让 FindOpenSSL 使用 musl 布局，
# 防止其默认搜索 /usr/include 把宿主 glibc 头混入 musl 目标编译
export CMAKE_PREFIX_PATH="$OPENSSL_PREFIX"
COMMON=(-c Release -p:PublishAot=true -p:StaticExecutable=true -p:StaticOpenSslLinking=true)

build_one() { # $1=RID  $2=输出目录名  $3...=附加 publish 参数
  local rid="$1" out="$2"
  shift 2
  echo "==> 构建 $out（$rid）"
  dotnet publish src/DevicePanel.Agent/DevicePanel.Agent.csproj "${COMMON[@]}" -r "$rid" "$@"
  mkdir -p "artifacts/agent/$out"
  cp "src/DevicePanel.Agent/bin/Release/net8.0/$rid/publish/DevicePanel.Agent" "artifacts/agent/$out/"
}

case "$TARGET" in
  amd64)
    build_one linux-musl-x64 linux-amd64 -p:CppCompilerAndLinker=musl-gcc
    ;;
  arm64)
    build_one linux-musl-arm64 linux-arm64 \
      -p:CppCompilerAndLinker=aarch64-linux-musl-gcc -p:ObjCopyName=aarch64-linux-musl-objcopy
    ;;
  all)
    build_one linux-musl-x64 linux-amd64 -p:CppCompilerAndLinker=musl-gcc
    build_one linux-musl-arm64 linux-arm64 \
      -p:CppCompilerAndLinker=aarch64-linux-musl-gcc -p:ObjCopyName=aarch64-linux-musl-objcopy
    ;;
  *)
    echo "用法：$0 [amd64|arm64|all]" >&2
    exit 1
    ;;
esac

file artifacts/agent/*/DevicePanel.Agent
echo "==> 构建完成：artifacts/agent/linux-{amd64,arm64}/DevicePanel.Agent"
echo "    目标机运行：./DevicePanel.Agent --url wss://<面板地址>/agent/ws --token <设备 token>"
echo "    （亦可用环境变量 PANEL_URL / PANEL_TOKEN）"
