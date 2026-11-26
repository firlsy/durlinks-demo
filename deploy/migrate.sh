#!/usr/bin/env bash
# ============================================================
# Durlinks 一键迁移：在【旧服务器】上执行，把数据+镜像+脚本推到新机
#   NEW_HOST=1.2.3.4 NEW_USER=root SSHPASS='新机密码' ./migrate.sh
#   可选：WITH_IMAGE=0 跳过镜像搬运（新机能拉镜像时）；NEW_SSH_PORT=22
# 依赖：本脚本同目录需有 backup.sh / bootstrap.sh / docker-compose.prod.yml
# ============================================================
set -euo pipefail

NEW_HOST="${NEW_HOST:?请设置 NEW_HOST（新服务器 IP/域名）}"
NEW_USER="${NEW_USER:-root}"
NEW_SSH_PORT="${NEW_SSH_PORT:-22}"
SSHPASS="${SSHPASS:-}"
WITH_IMAGE="${WITH_IMAGE:-1}"
APP_DIR="${APP_DIR:-/opt/durlinks-demo}"
API_IMAGE="${API_IMAGE:-$(docker inspect durlinks-demo-api --format '{{.Config.Image}}' 2>/dev/null || echo durlinks-demo-api:latest)}"
HERE="$(cd "$(dirname "$0")" && pwd)"
STAGE="$(mktemp -d)"; trap 'rm -rf "$STAGE"' EXIT
log() { printf '\n[%s] %s\n' "$(date +%H:%M:%S)" "$*"; }

for f in backup.sh bootstrap.sh docker-compose.prod.yml; do
  [ -f "$HERE/$f" ] || { echo "缺少 $HERE/$f"; exit 1; }
done

SSH="ssh -p $NEW_SSH_PORT -o StrictHostKeyChecking=no"
SCP="scp -P $NEW_SSH_PORT -o StrictHostKeyChecking=no"
if [ -n "$SSHPASS" ]; then
  command -v sshpass >/dev/null 2>&1 || { echo "需要 sshpass（apt install sshpass），或改用 SSH 密钥免密"; exit 1; }
  export SSHPASS
  SSH="sshpass -e $SSH"; SCP="sshpass -e $SCP"
fi

log "1/5 在旧机生成备份"
"$HERE/backup.sh" | tail -3
ARCHIVE="$(ls -1t "$APP_DIR/backups"/durlinks-*.tar.gz | head -1)"
log "备份：$ARCHIVE（$(du -h "$ARCHIVE" | cut -f1)）"

log "2/5 准备迁移包"
cp "$ARCHIVE" "$STAGE/"
cp "$HERE/backup.sh" "$HERE/bootstrap.sh" "$HERE/docker-compose.prod.yml" "$STAGE/"
# 导入模板（缺少会导致管理端“下载模板”404）
[ -f "$APP_DIR/server/导入模板.xlsx" ] && cp "$APP_DIR/server/导入模板.xlsx" "$STAGE/" || true
if [ "$WITH_IMAGE" = "1" ]; then
  log "导出镜像 $API_IMAGE（约 1-2 分钟）"
  docker save "$API_IMAGE" | gzip -1 > "$STAGE/durlinks-api.tar.gz"
fi

log "3/5 上传到新机 $NEW_HOST:/root/durlinks-migrate"
$SSH "$NEW_USER@$NEW_HOST" "rm -rf /root/durlinks-migrate && mkdir -p /root/durlinks-migrate"
$SCP "$STAGE"/* "$NEW_USER@$NEW_HOST:/root/durlinks-migrate/"

log "4/5 在新机执行初始化"
PG_PASSWORD="${PG_PASSWORD:-}"
if [ -z "$PG_PASSWORD" ]; then
  echo "需要新库口令：请以 PG_PASSWORD='...' 重新运行，或手动执行："
  echo "  ssh $NEW_USER@$NEW_HOST"
  echo "  cd /root/durlinks-migrate && PG_PASSWORD='...' BACKUP_ARCHIVE=$(basename "$ARCHIVE") IMAGE_TAR=durlinks-api.tar.gz bash bootstrap.sh"
  exit 2
fi
$SSH "$NEW_USER@$NEW_HOST" "cd /root/durlinks-migrate && PG_PASSWORD='$PG_PASSWORD' BACKUP_ARCHIVE='$(basename "$ARCHIVE")' IMAGE_TAR=durlinks-api.tar.gz bash bootstrap.sh"

log "5/5 迁移完成"
cat <<EOF
新机自检：
  ssh $NEW_USER@$NEW_HOST "docker ps --format '{{.Names}} {{.Status}}'"
  curl -s -o /dev/null -w '%{http_code}\\n' http://$NEW_HOST:8788/api/auth/captcha/image

切换流量：
  1) 把 Lucky 的两条子规则后端地址改成新机 127.0.0.1:8789 / 8788
  2) 或把域名 DNS 指向新机（TTL 生效前旧机保持运行）
  3) 验证两个域名 200、管理端数据条数正确后，再停旧机容器
回滚：Lucky 改回旧机地址即可，旧机数据还在。
EOF
