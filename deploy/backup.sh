#!/usr/bin/env bash
# ============================================================
# Durlinks 一键备份（在服务器上执行）
#   ./backup.sh
#   APP_DIR=/opt/durlinks-demo KEEP=30 ./backup.sh
# 产物：$BACKUP_DIR/durlinks-YYYYmmdd-HHMMSS.tar.gz（含 db.dump / 配置 / 静态站 / 清单）
# 设计原则：只读数据库，不重启服务；配置与静态站一起打包，灾难时可直接换机恢复
# ============================================================
set -euo pipefail

APP_DIR="${APP_DIR:-/opt/durlinks-demo}"
BACKUP_DIR="${BACKUP_DIR:-$APP_DIR/backups}"
KEEP="${KEEP:-14}"                 # 保留最近 N 份，0 表示不清理
INCLUDE_STATIC="${INCLUDE_STATIC:-1}"  # 1=打包管理端/用户端静态站（便于不重建直接恢复）
CFG="$APP_DIR/server/appsettings.Production.json"
STAMP="$(date +%Y%m%d-%H%M%S)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

log() { printf '[%s] %s\n' "$(date +%H:%M:%S)" "$*"; }

mkdir -p "$BACKUP_DIR"
chmod 700 "$BACKUP_DIR" 2>/dev/null || true

# 从 appsettings.Production.json 读配置（支持 a.b.c 嵌套），失败返回空
cfg() {
  [ -f "$CFG" ] || return 1
  python3 - "$CFG" "$1" <<'PY' 2>/dev/null || exit 1
import json, sys
try:
    d = json.load(open(sys.argv[1], encoding="utf-8"))
except Exception:
    sys.exit(1)
cur = d
for k in sys.argv[2].split("."):
    if not isinstance(cur, dict) or k not in cur:
        sys.exit(1)
    cur = cur[k]
print(cur)
PY
}

PROVIDER="$(cfg Database.Provider || echo postgres)"

log "应用目录：$APP_DIR"
log "数据库类型：$PROVIDER"

# ---------- 1. 数据库 ----------
if [ "$PROVIDER" = "postgres" ]; then
  # 连接串形如 Host=...;Port=...;Database=...;Username=...;Password=...
  read -r PG_HOST PG_PORT PG_DB PG_USER PG_PASS < <(
    python3 - "$CFG" <<'PY'
import json, re, sys
cs = ""
try:
    cs = json.load(open(sys.argv[1], encoding="utf-8"))["ConnectionStrings"]["Postgres"]
except Exception:
    pass
kv = dict(re.findall(r"([A-Za-z]+)\s*=\s*([^;]*)", cs))
print(kv.get("Host", "127.0.0.1"), kv.get("Port", "5432"), kv.get("Database", "durlinks_demo"),
      kv.get("Username", "durlinks_demo"), kv.get("Password", ""))
PY
  )
  log "导出 PostgreSQL：$PG_USER@$PG_HOST:$PG_PORT/$PG_DB"
  PGPASSWORD="$PG_PASS" pg_dump -h "$PG_HOST" -p "$PG_PORT" -U "$PG_USER" -d "$PG_DB" \
    -Fc --no-owner --no-privileges -f "$WORK/db.dump"
  # 关键计数写进清单，恢复后能一眼比对
  PGPASSWORD="$PG_PASS" psql -h "$PG_HOST" -p "$PG_PORT" -U "$PG_USER" -d "$PG_DB" -tAc \
    'select (select count(*) from "Links"), (select count(*) from "PublishLibraries" where "IsDeleted"=false), (select count(*) from "Users");' \
    > "$WORK/counts.txt" 2>/dev/null || echo "n/a" > "$WORK/counts.txt"
elif [ "$PROVIDER" = "sqlite" ]; then
  DB_FILE="$APP_DIR/data/durlinks.db"
  [ -f "$DB_FILE" ] || { log "找不到 SQLite 文件：$DB_FILE"; exit 1; }
  log "备份 SQLite：$DB_FILE"
  if command -v sqlite3 >/dev/null 2>&1; then
    sqlite3 "$DB_FILE" ".backup '$WORK/durlinks.db'"
  else
    cp "$DB_FILE" "$WORK/durlinks.db"
  fi
else
  log "未知数据库类型：$PROVIDER"; exit 1
fi

# ---------- 2. 运行配置 ----------
if [ -f "$CFG" ]; then
  cp "$CFG" "$WORK/appsettings.Production.json"
  chmod 600 "$WORK/appsettings.Production.json"   # 内含数据库口令与 JWT 密钥
  log "已打包运行配置"
else
  log "警告：未找到 $CFG（新机恢复后需手工补 JWT 密钥）"
fi

# ---------- 3. 静态站（可选） ----------
if [ "$INCLUDE_STATIC" = "1" ]; then
  for d in admin web; do
    if [ -d "$APP_DIR/$d" ] && [ -n "$(ls -A "$APP_DIR/$d" 2>/dev/null)" ]; then
      tar -czf "$WORK/$d-static.tar.gz" -C "$APP_DIR/$d" .
      log "已打包 $d 静态站"
    fi
  done
fi

# ---------- 4. 容器与部署信息（换机时照抄即可） ----------
{
  echo "备份时间: $(date -Is)"
  echo "主机: $(hostname)"
  echo "应用目录: $APP_DIR"
  echo "数据库类型: $PROVIDER"
  if [ "$PROVIDER" = "postgres" ]; then
    echo "数据库: $PG_DB @ $PG_HOST:$PG_PORT (user=$PG_USER)"
    echo "计数(链接/发布库/用户): $(cat "$WORK/counts.txt" 2>/dev/null)"
  fi
  echo "镜像: $(docker inspect durlinks-demo-api --format '{{.Config.Image}}' 2>/dev/null || echo '未运行')"
  echo "挂载:"
  docker inspect durlinks-demo-api --format '{{range .Mounts}}  {{.Source}} -> {{.Destination}}{{println}}{{end}}' 2>/dev/null || true
  echo "启动参数:"
  docker inspect durlinks-demo-api --format '  网络={{.HostConfig.NetworkMode}} 重启={{.HostConfig.RestartPolicy.Name}} 环境={{json .Config.Env}}' 2>/dev/null || true
} > "$WORK/manifest.txt"

# ---------- 5. 打包 + 校验 ----------
ARCHIVE="$BACKUP_DIR/durlinks-$STAMP.tar.gz"
tar -czf "$ARCHIVE" -C "$WORK" .
( cd "$BACKUP_DIR" && sha256sum "$(basename "$ARCHIVE")" > "$(basename "$ARCHIVE").sha256" )
SIZE="$(du -h "$ARCHIVE" | cut -f1)"
log "备份完成：$ARCHIVE（$SIZE）"

# ---------- 6. 保留策略 ----------
if [ "$KEEP" -gt 0 ] 2>/dev/null; then
  mapfile -t OLD < <(ls -1t "$BACKUP_DIR"/durlinks-*.tar.gz 2>/dev/null | tail -n +$((KEEP + 1)) || true)
  for f in "${OLD[@]:-}"; do
    [ -n "$f" ] || continue
    rm -f "$f" "$f.sha256"
    log "清理旧备份：$(basename "$f")"
  done
fi

log "现有备份："
ls -lh "$BACKUP_DIR"/durlinks-*.tar.gz 2>/dev/null | tail -5 || true
