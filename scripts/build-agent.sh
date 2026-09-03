#!/usr/bin/env bash
# 轻量 agent 交叉构建：Linux amd64 / arm64 静态单二进制（NativeAOT + musl + 内嵌静态 OpenSSL）
# 产物：artifacts/agent/linux-amd64/DevicePanel.Agent、artifacts/agent/linux-arm64/DevicePanel.Agent
# 验收：`file` 输出 static-pie linked；目标机无需任何运行时/共享库（musl 与 OpenSSL 均已静态链入），零入站端口
#
# 构建环境（x64 主机，Debian/Ubuntu 类）：
#   dotnet SDK 8+、musl-tools、binutils(objcopy)、cmake、libssl-dev（仅取头文件）
#   aarch64 musl 交叉工具链：https://musl.cc/aarch64-linux-musl-cross.tgz（解压后 bin 加入 PATH）
#   musl 静态 zlib（libz.a，各自标架构）：zlib 源码 `CC=<musl-gcc> ./configure --static && make`
#     x64 放 /usr/lib/x86_64-linux-musl/；arm64 放交叉工具链 sysroot lib/
#   musl 静态 OpenSSL 3.x（各自标架构，供 StaticOpenSslLinking 静态链入）：
#     CC=<musl-gcc> ./Configure linux-{x86_64,aarch64} no-shared no-dso no-tests no-docs no-afalgeng \
#       -DOPENSSL_NO_SECURE_MEMORY && make build_libs
#     x64 的 libssl.a/libcrypto.a 放 /usr/lib/x86_64-linux-musl/；arm64 放工具链 sysroot lib/
#   cmake>=4 兼容补丁：为 ilcompiler runtime 包 native/src/libs/*/CMakeLists.txt 首行插入
#     `cmake_minimum_required(VERSION 3.5)`（shim 源码缺 minimum 声明，cmake 4 拒编）
# 说明：在 Alpine 容器内原生构建可省去交叉工具链与 musl 库准备（apk add zlib-static openssl-static 即可）。
set -euo pipefail
cd "$(dirname "$0")/.."

OPENSSL_SRC="${OPENSSL_SRC:?请设置 OPENSSL_SRC 指向已完成 Configure 的 musl 静态 OpenSSL 3.x 源码树}"

OUT=artifacts/agent
COMMON=(-c Release -p:PublishAot=true -p:StaticExecutable=true -p:StaticOpenSslLinking=true)

# 预置 crypto shim 的 cmake 缓存：指向 musl OpenSSL 源码树取头文件，
# 避免默认 FindOpenSSL 把宿主 glibc 头（/usr/include）混入 musl 目标编译
preseed_shim() { # $1=RID  $2=cross-gcc
  local ilcpkg="/root/.nuget/packages/runtime.$1.microsoft.dotnet.ilcompiler/8.0.30"
  local builddir="src/DevicePanel.Agent/obj/Release/net8.0/$1/libs/System.Security.Cryptography.Native/build"
  mkdir -p "$builddir"
  (cd "$builddir" && rm -rf CMakeCache.txt CMakeFiles && \
    CC="$2" cmake -S "$ilcpkg/native/src/libs/System.Security.Cryptography.Native" \
      -DLOCAL_BUILD:STRING=1 -DCLR_CMAKE_TARGET_UNIX:STRING=1 \
      -DOPENSSL_ROOT_DIR="$OPENSSL_SRC" >/dev/null && make -j"$(nproc)" >/dev/null)
}

echo "==> [1/2] linux-amd64（musl-gcc）"
preseed_shim linux-musl-x64 musl-gcc
dotnet publish src/DevicePanel.Agent/DevicePanel.Agent.csproj "${COMMON[@]}" \
  -r linux-musl-x64 -p:CppCompilerAndLinker=musl-gcc
mkdir -p "$OUT/linux-amd64"
cp src/DevicePanel.Agent/bin/Release/net8.0/linux-musl-x64/publish/DevicePanel.Agent "$OUT/linux-amd64/"

echo "==> [2/2] linux-arm64（aarch64-linux-musl-gcc，需交叉工具链在 PATH）"
preseed_shim linux-musl-arm64 aarch64-linux-musl-gcc
dotnet publish src/DevicePanel.Agent/DevicePanel.Agent.csproj "${COMMON[@]}" \
  -r linux-musl-arm64 -p:CppCompilerAndLinker=aarch64-linux-musl-gcc -p:ObjCopyName=aarch64-linux-musl-objcopy
mkdir -p "$OUT/linux-arm64"
cp src/DevicePanel.Agent/bin/Release/net8.0/linux-musl-arm64/publish/DevicePanel.Agent "$OUT/linux-arm64/"

file "$OUT"/*/DevicePanel.Agent
echo "==> 构建完成：$OUT/linux-{amd64,arm64}/DevicePanel.Agent"
echo "    目标机运行：./DevicePanel.Agent --url wss://<面板地址>/agent/ws --token <设备 token>"
echo "    （亦可用环境变量 PANEL_URL / PANEL_TOKEN）"
