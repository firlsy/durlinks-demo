#!/usr/bin/env bash
# 本地构建 + 部署 Go 服务端到 <项目根>/run
# 用法：bash deploy/go/build-local.sh [--publish-web]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OUT="$ROOT/run"
GO_BIN="${GO:-go}"

echo "==> 编译 Go 服务端"
cd "$ROOT/src/Durlinks.GoServer"
"$GO_BIN" build -trimpath -ldflags "-s -w" -o "$OUT/durlinks-server.exe" .

mkdir -p "$OUT/data" "$OUT/templates" "$OUT/wwwroot-user" "$OUT/wwwroot-admin"

echo "==> 复制导入模板"
if [ -f "$ROOT/src/Durlinks.Server/templates/导入模板.xlsx" ]; then
    cp -f "$ROOT/src/Durlinks.Server/templates/导入模板.xlsx" "$OUT/templates/导入模板.xlsx"
fi

echo "==> 复制静态站（用户端由 Go 直出，此目录仅 Web.Mode=static 回退时使用）"
cp -rf "$ROOT/src/Durlinks.AdminWeb/wwwroot/." "$OUT/wwwroot-admin/" 2>/dev/null || true
if [ -f "$ROOT/src/Durlinks.AdminWeb/login-assets/login.html" ]; then
    cp -f "$ROOT/src/Durlinks.AdminWeb/login-assets/login.html" "$OUT/wwwroot-admin/login.html"
fi
cp -rf "$ROOT/src/Durlinks.Web/wwwroot/." "$OUT/wwwroot-user/" 2>/dev/null || true

echo "==> 生成 appsettings.json（已存在则保留）"
if [ ! -f "$OUT/appsettings.json" ]; then
    SECRET="$(head -c 48 /dev/urandom | base64 | tr -d '\n')"
    cat > "$OUT/appsettings.json" <<EOF
{
  "Database": { "Provider": "sqlite", "Path": "data/durlinks.db" },
  "Kestrel": { "Host": "localhost" },
  "Ports": { "Public": 8789, "Admin": 8788 },
  "StaticFiles": { "UserDir": "wwwroot-user", "AdminDir": "wwwroot-admin" },
  "Web": { "Mode": "go" },
  "Jwt": { "Secret": "$SECRET" }
}
EOF
    echo "    已写入随机 JWT 密钥"
fi

echo "==> 完成：$OUT/durlinks-server.exe"
echo "    启动：cd \"$OUT\" && ./durlinks-server.exe"
