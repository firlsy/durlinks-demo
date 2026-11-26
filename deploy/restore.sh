#!/usr/bin/env bash
# ============================================================
# Durlinks 恢复脚本（在服务器上执行）
#   ./restore.sh /opt/durlinks-demo/backups/durlinks-20261122-180000.tar.gz --yes
#   ./restore.sh <archive> --yes --with-config --with-static
# 说明：会先清空目标库的 public schema 再导入，属于破坏性操作，必须带 --yes
# ============================================================
set -euo pipefail

ARCHIVE="${1:-}"
shift || true
CONFIRM=0; WITH_CONFIG=0; WITH_STATIC=0; DB_OVERRIDE=""
while [ $# -gt 0 ]; do
  case "$1" in
    --yes) CONFIRM=1 ;;
    --with-config) WITH_CONFIG=1 ;;
    --with-static) WITH_STATIC=1 ;;
    --db-name) DB_OVERRIDE="${2:-}"; shift ;;
    *) echo "未知参数：$1"; exit 2 ;;
  esac
  shift
done

APP_DIR="${APP_DIR:-/opt/durlinks-demo}"
CFG="$APP_DIR/server/appsettings.Production.json"
log() { printf '[%s] %s\n' "$(date +%H:%M:%S)" "$*"; }

[ -n "$ARCHIVE" ] && [ -f "$ARCHIVE" ] || { echo "用法：$0 <备份文件> --yes [--with-config] [--with-static] [--db-name X]"; exit 2; }

# 校验 sha256（存在则校验）
if [ -f "$ARCHIVE.sha256" ]; then
  ( cd "$(dirname "$ARCHIVE")" && sha256sum -c "$(basename "$ARCHIVE").sha256" ) || { echo "校验失败，备份文件可能损坏"; exit 1; }
  log "sha256 校验通过"
fi

WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT
tar -xzf "$ARCHIVE" -C "$WORK"
log "已解包：$(basename "$ARCHIVE")"

read_cfg_db() {
  python3 - "$CFG" <<'PY'
import json, re, sys
try:
    cs = json.load(open(sys.argv[1], encoding="utf-8"))["ConnectionStrings"]["Postgres"]
except Exception:
    cs = ""
kv = dict(re.findall(r"([A-Za-z]+)\s*=\s*([^;]*)", cs))
print(kv.get("Host", "127.0.0.1"), kv.get("Port", "5432"),
      kv.get("Database", "durlinks_demo"), kv.get("Username", "durlinks_demo"),
      kv.get("Password", ""))
PY
}

if [ -f "$WORK/db.dump" ]; then
  read -r PG_HOST PG_PORT PG_DB PG_USER PG_PASS < <(read_cfg_db)
  [ -n "$DB_OVERRIDE" ] && PG_DB="$DB_OVERRIDE"
  export PGPASSWORD="$PG_PASS"
  log "目标数据库：$PG_USER@$PG_HOST:$PG_PORT/$PG_DB"
  if [ "$CONFIRM" != "1" ]; then
    cat <<EOF
将要执行（破坏性）：
  1) 断开该库所有连接
  2) DROP SCHEMA public CASCADE; CREATE SCHEMA public;
  3) pg_restore 导入 $(basename "$ARCHIVE") 中的 db.dump
确认无误请加 --yes 重新执行。
EOF
    exit 1
  fi
  psql -h "$PG_HOST" -p "$PG_PORT" -U "$PG_USER" -d "$PG_DB" -v ON_ERROR_STOP=1 -c \
    "select pg_terminate_backend(pid) from pg_stat_activity where datname = current_database() and pid <> pg_backend_pid();" >/dev/null
  psql -h "$PG_HOST" -p "$PG_PORT" -U "$PG_USER" -d "$PG_DB" -v ON_ERROR_STOP=1 -c \
    "drop schema public cascade; create schema public;" >/dev/null
  pg_restore -h "$PG_HOST" -p "$PG_PORT" -U "$PG_USER" -d "$PG_DB" \
    --no-owner --no-privileges --exit-on-error "$WORK/db.dump"
  log "数据库恢复完成"
  psql -h "$PG_HOST" -p "$PG_PORT" -U "$PG_USER" -d "$PG_DB" -tAc \
    'select (select count(*) from "Links"), (select count(*) from "PublishLibraries" where "IsDeleted"=false), (select count(*) from "Users");' \
    | awk -F'|' '{printf "校验：链接=%s 发布库=%s 用户=%s\n", $1, $2, $3}'
elif [ -f "$WORK/durlinks.db" ]; then
  log "检测到 SQLite 备份"
  if [ "$CONFIRM" != "1" ]; then echo "将覆盖 $APP_DIR/data/durlinks.db，请加 --yes"; exit 1; fi
  mkdir -p "$APP_DIR/data"
  [ -f "$APP_DIR/data/durlinks.db" ] && cp "$APP_DIR/data/durlinks.db" "$APP_DIR/data/durlinks.db.bak-$(date +%Y%m%d-%H%M%S)"
  cp "$WORK/durlinks.db" "$APP_DIR/data/durlinks.db"
  log "SQLite 已覆盖（需重启容器）"
else
  echo "备份中没有数据库文件"; exit 1
fi

if [ "$WITH_CONFIG" = "1" ] && [ -f "$WORK/appsettings.Production.json" ]; then
  mkdir -p "$(dirname "$CFG")"
  [ -f "$CFG" ] && cp "$CFG" "$CFG.bak-$(date +%Y%m%d-%H%M%S)"
  cp "$WORK/appsettings.Production.json" "$CFG"
  chmod 600 "$CFG"
  log "运行配置已恢复（旧配置已备份）"
fi

if [ "$WITH_STATIC" = "1" ]; then
  for d in admin web; do
    if [ -f "$WORK/$d-static.tar.gz" ]; then
      [ -d "$APP_DIR/$d" ] && mv "$APP_DIR/$d" "$APP_DIR/$d.bak-$(date +%Y%m%d-%H%M%S)"
      mkdir -p "$APP_DIR/$d"
      tar -xzf "$WORK/$d-static.tar.gz" -C "$APP_DIR/$d"
      log "$d 静态站已恢复"
    fi
  done
fi

log "完成。请执行：docker restart durlinks-demo-api（或 docker compose restart api），再打开管理端验证。"
