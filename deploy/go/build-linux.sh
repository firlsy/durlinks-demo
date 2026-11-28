#!/usr/bin/env bash
# 交叉编译 Linux 单二进制（默认 amd64，可传 arm64 / armv7 / 386）
# 用法：bash deploy/go/build-linux.sh [amd64|arm64]
# 产物：dist/durlinks-server-linux-<arch>（CGO 关闭、静态链接；前端与导入模板已 embed 进二进制）
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
ARCH="${1:-amd64}"
OUT="$ROOT/dist"
BIN="$OUT/durlinks-server-linux-$ARCH"
GO_BIN="${GO:-go}"

mkdir -p "$OUT"
cd "$ROOT/src/Durlinks.GoServer"

echo "==> 编译 linux/$ARCH（CGO_ENABLED=0 静态链接）"
CGO_ENABLED=0 GOOS=linux GOARCH="$ARCH" "$GO_BIN" build -trimpath -ldflags "-s -w" -o "$BIN" .

echo "==> 产物：$BIN"
echo "    大小：$(ls -lh "$BIN" | awk '{print $5}')"
if command -v sha256sum >/dev/null 2>&1; then
    echo "    sha256：$(sha256sum "$BIN" | cut -d' ' -f1)"
elif command -v shasum >/dev/null 2>&1; then
    echo "    sha256：$(shasum -a 256 "$BIN" | cut -d' ' -f1)"
fi
echo "==> 部署：远端只需要这一个二进制 + appsettings.json + data/ 目录（导入模板已内置）"
echo "    scp \"$BIN\" root@your-host:/opt/durlinks/app/durlinks-server"
