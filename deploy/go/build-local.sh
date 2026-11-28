#!/usr/bin/env bash
# 本地构建 + 部署 Go 服务端到 <项目根>/run
# 用法：bash deploy/go/build-local.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OUT="$ROOT/run"
GO_BIN="${GO:-go}"

# Windows（Git Bash/MSYS）加 .exe，Linux/macOS 不加，避免把 .exe 当 Linux 二进制拷来拷去
BIN="durlinks-server"
case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*) BIN="durlinks-server.exe" ;;
esac

echo "==> 编译 Go 服务端（本机平台，产物 $BIN）"
cd "$ROOT/src/Durlinks.GoServer"
"$GO_BIN" build -trimpath -ldflags "-s -w" -o "$OUT/$BIN" .

mkdir -p "$OUT/data" "$OUT/templates" "$OUT/wwwroot-user" "$OUT/wwwroot-admin"

echo "==> 导入模板已内置进二进制（如需自定义，放 templates/导入模板.xlsx 覆盖）"

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

echo "==> 完成：$OUT/$BIN"
echo "    启动：cd \"$OUT\" && ./$BIN"
echo "    Linux 服务器请改用 deploy/go/build-linux.sh 交叉编译"
