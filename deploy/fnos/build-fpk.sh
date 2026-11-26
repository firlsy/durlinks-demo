#!/usr/bin/env bash
# ============================================================
# 打飞牛 fnOS 安装包（.fpk）
#
# 前置：
#   1) .NET 10 SDK（用于发布静态站；已有 dist/web、dist/admin 可跳过）
#   2) Docker（构建内置静态站的镜像 durlinks-demo-api:<版本>-fnos）
#   3) 飞牛官方 fnpack CLI（https://developer.fnnas.com/docs/cli/fnpack/）
#
# 用法：
#   ./deploy/fnos/build-fpk.sh
#   FNPACK_BIN=/usr/local/bin/fnpack ./deploy/fnos/build-fpk.sh
#
# 产物：
#   dist/fnos/durlinks_<版本>_all.fpk
#   dist/fnos/durlinks-demo-api_<版本>-fnos.tar.gz（离线安装用镜像）
# ============================================================
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
VERSION="${VERSION:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -1)}"
OUT="$ROOT/dist/fnos"
WORK="$OUT/durlinks"
FNPACK_BIN="${FNPACK_BIN:-fnpack}"

log() { printf '\n[%s] %s\n' "$(date +%H:%M:%S)" "$*"; }

[ -d "$ROOT/dist/web" ] && [ -d "$ROOT/dist/admin" ] || {
  log "缺少 dist/web 或 dist/admin，先执行 ./deploy/build-release.sh"
  "$ROOT/deploy/build-release.sh"
}

log "构建镜像 durlinks-demo-api:$VERSION-fnos"
docker build -f "$ROOT/deploy/fnos/Dockerfile" \
  --build-arg BASE_IMAGE="durlinks-demo-api:$VERSION" \
  -t "durlinks-demo-api:$VERSION-fnos" "$ROOT"

log "组装 FPK 目录"
rm -rf "$WORK"; mkdir -p "$WORK"
cp -r "$ROOT/deploy/fnos/." "$WORK/"
rm -f "$WORK/build-fpk.sh" "$WORK/README.md" "$WORK/Dockerfile"
chmod +x "$WORK/cmd/"* 2>/dev/null || true

# 版本号注入（manifest 与 compose 必须一致）
sed -i.bak "s/^version=.*/version=$VERSION/" "$WORK/manifest"
sed -i.bak "s#image: durlinks-demo-api:.*-fnos#image: durlinks-demo-api:$VERSION-fnos#" "$WORK/app/docker/docker-compose.yaml"
rm -f "$WORK/manifest.bak" "$WORK/app/docker/docker-compose.yaml.bak"

if ! command -v "$FNPACK_BIN" >/dev/null 2>&1; then
  log "未找到 fnpack。FPK 目录已准备好：$WORK"
  echo "请安装飞牛官方 fnpack 后执行： cd \"$WORK\" && fnpack build"
  exit 1
fi

log "fnpack 打包"
( cd "$WORK" && "$FNPACK_BIN" build )
FPK="$(find "$WORK" -maxdepth 2 -name '*.fpk' | head -1)"
[ -n "$FPK" ] && mv -f "$FPK" "$OUT/" && log "产物：$OUT/$(basename "$FPK")"

log "导出离线镜像（NAS 无法拉取本地镜像时用）"
docker save "durlinks-demo-api:$VERSION-fnos" | gzip -1 > "$OUT/durlinks-demo-api_$VERSION-fnos.tar.gz"

log "完成"; ls -lh "$OUT" | tail -5
