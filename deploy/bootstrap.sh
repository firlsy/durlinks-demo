#!/usr/bin/env bash
# ============================================================
# Durlinks 新服务器初始化（Ubuntu/Debian，root 执行）
# 目标：一台空机 → 可访问的完整系统（PostgreSQL 容器 + API 容器 + 静态站）
#
# 用法（在备份包与脚本所在目录）：
#   PG_PASSWORD='xxx' BACKUP_ARCHIVE=./durlinks-20261122-180000.tar.gz \
#   IMAGE_TAR=./durlinks-api.tar.gz ./bootstrap.sh
#
# 环境变量：
#   PG_PASSWORD   必填，新库口令
#   BACKUP_ARCHIVE 备份包（可选；有则自动恢复数据库与静态站）
#   IMAGE_TAR     离线镜像包（docker save 产物，可选；没有则按 API_IMAGE 拉取）
#   API_IMAGE     镜像名（默认 durlinks-demo-api:latest）
#   JWT_SECRET    备份里没有 appsettings 时必填
# ============================================================
set -euo pipefail

APP_DIR="${APP_DIR:-/opt/durlinks-demo}"
API_IMAGE="${API_IMAGE:-durlinks-demo-api:latest}"
IMAGE_TAR="${IMAGE_TAR:-}"
BACKUP_ARCHIVE="${BACKUP_ARCHIVE:-}"
PG_PASSWORD="${PG_PASSWORD:?请设置 PG_PASSWORD}"
PG_DATABASE="${PG_DATABASE:-durlinks_demo}"
PG_USER="${PG_USER:-durlinks_demo}"
JWT_SECRET="${JWT_SECRET:-}"

log() { printf '\n[%s] %s\n' "$(date +%H:%M:%S)" "$*"; }
need_root() { [ "$(id -u)" = "0" ] || { echo "请用 root 执行"; exit 1; }; }

need_root
export DEBIAN_FRONTEND=noninteractive

# ---------- 1. Docker ----------
if ! command -v docker >/dev/null 2>&1; then
  log "安装 Docker"
  if command -v curl >/dev/null 2>&1; then
    curl -fsSL https://get.docker.com | sh || {
      log "官方脚本失败，回退 apt"
      apt-get update -qq && apt-get install -y -qq docker.io docker-compose-v2
    }
  else
    apt-get update -qq && apt-get install -y -qq docker.io docker-compose-v2
  fi
fi
systemctl enable --now docker >/dev/null 2>&1 || true

if docker compose version >/dev/null 2>&1; then
  DC="docker compose"
elif command -v docker-compose >/dev/null 2>&1; then
  DC="docker-compose"
else
  log "缺少 compose 插件，尝试安装"; apt-get update -qq && apt-get install -y -qq docker-compose-v2 || true
  docker compose version >/dev/null 2>&1 && DC="docker compose" || { echo "请先安装 docker compose"; exit 1; }
fi
log "Docker 就绪：$(docker --version) / $DC"

# ---------- 2. 目录 ----------
log "创建目录 $APP_DIR"
mkdir -p "$APP_DIR"/{server,admin,web,data,backups,incoming,deploy}
chmod 700 "$APP_DIR/backups"

# 脚本与编排文件（与本脚本同目录时自动复制）
HERE="$(cd "$(dirname "$0")" && pwd)"
for f in backup.sh restore.sh docker-compose.prod.yml; do
  [ -f "$HERE/$f" ] && cp -f "$HERE/$f" "$APP_DIR/deploy/$f"
done
cp -f "$APP_DIR/deploy/docker-compose.prod.yml" "$APP_DIR/docker-compose.prod.yml" 2>/dev/null || true
chmod +x "$APP_DIR/deploy/"*.sh 2>/dev/null || true

# ---------- 3. 镜像 ----------
if [ -n "$IMAGE_TAR" ] && [ -f "$IMAGE_TAR" ]; then
  log "导入离线镜像：$IMAGE_TAR"
  case "$IMAGE_TAR" in
    *.gz) gunzip -c "$IMAGE_TAR" | docker load ;;
    *) docker load -i "$IMAGE_TAR" ;;
  esac
else
  log "尝试拉取镜像：$API_IMAGE"
  docker pull "$API_IMAGE" || log "拉取失败（若使用离线镜像可忽略）"
fi
docker image inspect "$API_IMAGE" >/dev/null 2>&1 || { echo "镜像 $API_IMAGE 不存在，请提供 IMAGE_TAR 或先 docker load"; exit 1; }

# ---------- 4. 从备份还原配置与静态站 ----------
STAGE="$(mktemp -d)"
if [ -n "$BACKUP_ARCHIVE" ] && [ -f "$BACKUP_ARCHIVE" ]; then
  log "解包备份：$BACKUP_ARCHIVE"
  tar -xzf "$BACKUP_ARCHIVE" -C "$STAGE"

  if [ -f "$STAGE/appsettings.Production.json" ]; then
    cp "$STAGE/appsettings.Production.json" "$APP_DIR/server/appsettings.Production.json"
    chmod 600 "$APP_DIR/server/appsettings.Production.json"
    log "已恢复运行配置"
  fi
  for d in admin web; do
    if [ -f "$STAGE/$d-static.tar.gz" ]; then
      rm -rf "$APP_DIR/$d"; mkdir -p "$APP_DIR/$d"
      tar -xzf "$STAGE/$d-static.tar.gz" -C "$APP_DIR/$d"
      log "已恢复 $d 静态站"
    fi
  done
fi

# 没有配置就生成一份（JWT 必须显式给）
if [ ! -f "$APP_DIR/server/appsettings.Production.json" ]; then
  [ -n "$JWT_SECRET" ] || { echo "缺少 appsettings.Production.json，且未提供 JWT_SECRET"; exit 1; }
  cat > "$APP_DIR/server/appsettings.Production.json" <<JSON
{
  "Database": { "Provider": "postgres" },
  "Kestrel": { "Host": "0.0.0.0" },
  "Ports": { "Public": 8789, "Admin": 8788 },
  "Jwt": { "Secret": "$JWT_SECRET" },
  "StaticFiles": {
    "UserDir": "/opt/durlinks-demo/web",
    "AdminDir": "/opt/durlinks-demo/admin"
  }
}
JSON
  chmod 600 "$APP_DIR/server/appsettings.Production.json"
  log "已生成运行配置（含随机/指定 JWT 密钥）"
fi

# 模板文件（缺了导入模板下载会 404）
if [ ! -f "$APP_DIR/server/导入模板.xlsx" ]; then
  if [ -f "$HERE/导入模板.xlsx" ]; then
    cp "$HERE/导入模板.xlsx" "$APP_DIR/server/导入模板.xlsx"
  elif [ -f "$HERE/../program-durlinks-demos-导入模板.xlsx" ]; then
    cp "$HERE/../program-durlinks-demos-导入模板.xlsx" "$APP_DIR/server/导入模板.xlsx"
  else
    log "提示：未找到导入模板，Excel 导入模板下载会不可用"
  fi
fi

# ---------- 5. compose 环境 ----------
cat > "$APP_DIR/.env" <<ENV
PG_DATABASE=$PG_DATABASE
PG_USER=$PG_USER
PG_PASSWORD=$PG_PASSWORD
API_IMAGE=$API_IMAGE
ENV
chmod 600 "$APP_DIR/.env"

# ---------- 6. 起数据库 → 恢复数据 → 起 API ----------
cd "$APP_DIR"
log "启动数据库容器"
$DC -f docker-compose.prod.yml up -d db

log "等待数据库就绪"
for i in $(seq 1 60); do
  if docker exec durlinks-db pg_isready -U "$PG_USER" -d "$PG_DATABASE" >/dev/null 2>&1; then break; fi
  sleep 1
  [ "$i" = "60" ] && { echo "数据库启动超时"; docker logs --tail 40 durlinks-db; exit 1; }
done

if [ -f "$STAGE/db.dump" ]; then
  log "导入数据库备份"
  docker exec -i durlinks-db psql -U "$PG_USER" -d "$PG_DATABASE" -v ON_ERROR_STOP=1 \
    -c 'drop schema public cascade; create schema public;' >/dev/null
  docker exec -i durlinks-db pg_restore -U "$PG_USER" -d "$PG_DATABASE" \
    --no-owner --no-privileges --exit-on-error < "$STAGE/db.dump"
  log "数据导入完成：$(docker exec durlinks-db psql -U "$PG_USER" -d "$PG_DATABASE" -tAc 'select count(*) from "Links";' | tr -d ' ') 条链接"
fi
rm -rf "$STAGE"

log "启动 API 容器"
$DC -f docker-compose.prod.yml up -d api
sleep 6

# ---------- 7. 自检 ----------
log "自检"
if curl -fsS -o /dev/null --max-time 10 http://127.0.0.1:8788/api/auth/captcha/image; then
  echo "  ✓ 管理端口 8788 正常"
else
  echo "  ✗ 管理端口 8788 异常，请 docker logs durlinks-demo-api"; exit 1
fi
curl -fsS -o /dev/null --max-time 10 http://127.0.0.1:8789/api/links?page=1\&pageSize=1 \
  && echo "  ✓ 用户端口 8789 正常" || echo "  ! 用户端口 8789 异常"

cat <<EOF

================ 部署完成 ================
目录：$APP_DIR
容器：durlinks-db / durlinks-demo-api（127.0.0.1:8788 / 8789）
下一步：
  1) 在 Lucky 把两个域名指到本机 8080 → 8789 / 8788（详见 deploy/lucky.md）
  2) 管理端登录验证数据条数与版本号
  3) 设置每日备份：crontab -e
     0 4 * * * $APP_DIR/deploy/backup.sh >> $APP_DIR/backups/cron.log 2>&1
==========================================
EOF
