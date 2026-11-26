#!/usr/bin/env bash
# ============================================================
# 一键构建发布产物：
#   dist/server  —— 后端（docker run / systemd / Windows 服务都用它）
#   dist/web     —— 用户端静态站（Blazor WASM）
#   dist/admin   —— 管理端静态站（Blazor WASM）
# 并可选构建 Docker 镜像。
#
# 用法：
#   ./deploy/build-release.sh              # 只出产物
#   ./deploy/build-release.sh --docker     # 产物 + 镜像 durlinks-demo-api:<版本>
#   VERSION=1.9.0 ./deploy/build-release.sh
# 依赖：.NET 10 SDK；（--docker 时）Docker
# ============================================================
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="${VERSION:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -1)}"
OUT="${OUT:-$ROOT/dist}"
WITH_DOCKER=0
[ "${1:-}" = "--docker" ] && WITH_DOCKER=1

log() { printf '\n[%s] %s\n' "$(date +%H:%M:%S)" "$*"; }
need() { command -v "$1" >/dev/null 2>&1 || { echo "缺少依赖：$1"; exit 1; }; }

need dotnet
[ "$WITH_DOCKER" = "1" ] && need docker

log "版本 $VERSION → $OUT"
rm -rf "$OUT"
mkdir -p "$OUT/server" "$OUT/web" "$OUT/admin"

log "发布后端（框架依赖，需目标机安装 .NET 10 Runtime）"
dotnet publish "$ROOT/src/Durlinks.Server/Durlinks.Server.csproj" -c Release -o "$OUT/server" --nologo -v q

log "发布用户端静态站"
dotnet publish "$ROOT/src/Durlinks.Web/Durlinks.Web.csproj" -c Release -o "$OUT/.tmp-web" --nologo -v q
cp -r "$OUT/.tmp-web/wwwroot/." "$OUT/web/"
rm -rf "$OUT/.tmp-web"

log "发布管理端静态站"
dotnet publish "$ROOT/src/Durlinks.AdminWeb/Durlinks.AdminWeb.csproj" -c Release -o "$OUT/.tmp-admin" --nologo -v q
cp -r "$OUT/.tmp-admin/wwwroot/." "$OUT/admin/"
rm -rf "$OUT/.tmp-admin"

# 导入模板跟随后端目录，保证「下载导入模板」可用
cp -f "$ROOT/program-durlinks-demos-导入模板.xlsx" "$OUT/server/templates/导入模板.xlsx" 2>/dev/null \
  || { mkdir -p "$OUT/server/templates"; cp -f "$ROOT/program-durlinks-demos-导入模板.xlsx" "$OUT/server/templates/导入模板.xlsx"; }

if [ "$WITH_DOCKER" = "1" ]; then
  log "构建镜像 durlinks-demo-api:$VERSION（同时打 latest）"
  docker build -f "$ROOT/deploy/Dockerfile" -t "durlinks-demo-api:$VERSION" -t durlinks-demo-api:latest "$ROOT"
fi

log "完成"
du -sh "$OUT/server" "$OUT/web" "$OUT/admin" 2>/dev/null || true
cat <<EOF

下一步：
  后端：$OUT/server/Durlinks.Server
  用户端静态站：$OUT/web   → StaticFiles:UserDir
  管理端静态站：$OUT/admin → StaticFiles:AdminDir
  默认管理员：admin / admin123（首次登录后请立即修改）
EOF
