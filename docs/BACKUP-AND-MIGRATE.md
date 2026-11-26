# 备份、恢复与服务器迁移手册

> 适用版本：v1.7.x ｜ 生产目录：`/opt/durlinks-demo` ｜ 数据库：PostgreSQL `durlinks_demo`
> 本文所有脚本都在仓库 `deploy/` 下，已同步到服务器 `/opt/durlinks-demo/deploy/`。

---

## 0. 三分钟速查

```bash
# ① 备份（服务器上，随时可跑，不重启服务）
/opt/durlinks-demo/deploy/backup.sh

# ② 恢复（同机回滚 / 演练到临时库）
cd /opt/durlinks-demo
./deploy/restore.sh backups/durlinks-20261122-181428.tar.gz --yes
./deploy/restore.sh backups/xxx.tar.gz --yes --db-name durlinks_verify   # 演练，不碰生产库

# ③ 整机迁移（旧机上一条命令，推数据+镜像到新机）
cd /opt/durlinks-demo/deploy
NEW_HOST=1.2.3.4 NEW_USER=root SSHPASS='新机密码' PG_PASSWORD='新库口令' ./migrate.sh
```

---

## 1. 备份里到底有什么

`backup.sh` 产出一个自包含压缩包：

| 文件 | 内容 | 用途 |
|---|---|---|
| `db.dump` | `pg_dump -Fc` 全库（链接/标签/发布库/用户/LLM 配置） | 恢复数据 |
| `appsettings.Production.json` | 数据库连接串、JWT 密钥、静态目录 | 换机免重配（权限 600） |
| `admin-static.tar.gz` | 管理端 Blazor 静态站 | 不重新构建即可回滚 UI |
| `web-static.tar.gz` | 用户端 Blazor 静态站 | 同上 |
| `manifest.txt` | 备份时间、镜像 tag、容器挂载与启动参数 | 换机时照抄 |
| `counts.txt` | 链接/发布库/用户数量 | 恢复后一眼比对 |
| `*.sha256` | 归档校验和 | 防半截包 |

**不备份**：Docker 镜像（可用 `docker save` 单独搬）、代码（在 Git 里）、`backups/` 自身。

归档位置：`/opt/durlinks-demo/backups/durlinks-YYYYmmdd-HHMMSS.tar.gz`，默认保留最近 14 份（`KEEP=30` 可改）。

---

## 2. 日常备份

### 手动

```bash
/opt/durlinks-demo/deploy/backup.sh
# 想连静态站一起备（默认就备）：INCLUDE_STATIC=1
# 想只备数据库，减小体积：INCLUDE_STATIC=0
```

### 定时（每天 04:00）

```bash
crontab -e
# 追加：
0 4 * * * /opt/durlinks-demo/deploy/backup.sh >> /opt/durlinks-demo/backups/cron.log 2>&1
```

### 异地留存（强烈建议）

备份放在同一台机器上，磁盘坏了就一起没了。任选一种：

```bash
# 方式 A：另一台机器定时来拉
rsync -az --delete root@119.3.215.183:/opt/durlinks-demo/backups/ /data/backups/durlinks/

# 方式 B：对象存储（以阿里云 OSS 为例）
ossutil cp -r /opt/durlinks-demo/backups/ oss://your-bucket/durlinks/ --update
```

### 备份成功的样子

```
[18:14:31] 备份完成：/opt/durlinks-demo/backups/durlinks-20261122-181428.tar.gz（28M）
```

> 实测：库 3 条链接 + 1 个发布库 + 1 个用户时，含两套静态站的完整包约 **28 MB**，耗时 3 秒。

---

## 3. 恢复

### 3.1 同机回滚（改坏了/误删了）

```bash
cd /opt/durlinks-demo
./deploy/restore.sh backups/durlinks-20261122-181428.tar.gz --yes
docker restart durlinks-demo-api
```

`--yes` 是硬门槛：脚本会先打印“将清空目标库 public schema”的计划，确认后才会动手。

### 3.2 演练（不碰生产库）

```bash
su postgres -c 'createdb -O durlinks_demo durlinks_verify'
./deploy/restore.sh backups/xxx.tar.gz --yes --db-name durlinks_verify
# 看输出的“校验：链接=X 发布库=Y 用户=Z”
su postgres -c 'dropdb durlinks_verify'
```

> 已实测：备份包恢复到临时库后，链接/发布库/用户数量与生产一致。

### 3.3 连配置和静态站一起回滚

```bash
./deploy/restore.sh backups/xxx.tar.gz --yes --with-config --with-static
docker restart durlinks-demo-api
```

旧配置/旧静态目录会自动改名为 `*.bak-时间戳`，随时能翻回去。

### 3.4 SQLite 模式

备份包里是 `durlinks.db` 而不是 `db.dump` 时，脚本会走 SQLite 分支：覆盖 `data/durlinks.db` 前先留 `.bak`，然后提示重启容器。

---

## 4. 迁移到新服务器

### 4.1 推荐：一条命令（旧机执行）

**前提**：新机是干净的 Ubuntu/Debian，root 可 SSH 登录；旧机装了 `sshpass`（`apt install sshpass`）或用密钥免密。

```bash
cd /opt/durlinks-demo/deploy
NEW_HOST=1.2.3.4 NEW_USER=root NEW_SSH_PORT=22 \
SSHPASS='新机root密码' PG_PASSWORD='新库口令' \
./migrate.sh
```

它会做五件事：

1. 在旧机跑 `backup.sh`
2. `docker save` 导出当前镜像（`WITH_IMAGE=0` 可跳过，适合新机能拉镜像）
3. 把归档 + 三个脚本 + compose 传到 `/root/durlinks-migrate`
4. 在新机执行 `bootstrap.sh`：装 Docker → 建目录 → 导入镜像 → 起 PG 容器 → 恢复数据 → 起 API
5. 自检 `8788` 并打印切流步骤

新机若走 Docker Hub/私有仓库，把 `WITH_IMAGE=0 API_IMAGE=你的仓库/durlinks-demo-api:v1.7.2` 传给脚本即可。

### 4.2 分步版（想自己掌控每一步）

```bash
# ① 旧机备份
/opt/durlinks-demo/deploy/backup.sh

# ② 旧机导出镜像（离线搬运；有仓库则跳过）
docker save durlinks-demo-api:v1.7.2 | gzip -1 > /tmp/durlinks-api.tar.gz

# ③ 拷到新机
scp /opt/durlinks-demo/backups/durlinks-*.tar.gz root@新机:/root/
scp /tmp/durlinks-api.tar.gz root@新机:/root/
scp deploy/{backup.sh,restore.sh,bootstrap.sh,docker-compose.prod.yml} root@新机:/root/durlinks-migrate/

# ④ 新机初始化（一条命令完成装 Docker + 恢复）
ssh root@新机
cd /root/durlinks-migrate
PG_PASSWORD='新库口令' \
BACKUP_ARCHIVE=/root/durlinks-20261122-181428.tar.gz \
IMAGE_TAR=/root/durlinks-api.tar.gz \
bash bootstrap.sh
```

### 4.3 只换机器、数据不要了

```bash
PG_PASSWORD='新库口令' JWT_SECRET='至少32位随机串' \
IMAGE_TAR=/root/durlinks-api.tar.gz bash bootstrap.sh
```

不给 `BACKUP_ARCHIVE` 时，脚本会新建空库、生成配置，得到一个干净实例。

---

## 5. 新机的 Docker 形态

`bootstrap.sh` 使用 `deploy/docker-compose.prod.yml`：

```
宿主机 127.0.0.1:8789 ──► api 容器 8789（用户端）
宿主机 127.0.0.1:8788 ──► api 容器 8788（管理端）
                api ──► db 容器 5432（PostgreSQL 16，卷 pgdata）
```

与现网差异：现网是“宿主机 PostgreSQL + `--network host` 单容器”；新机默认是“PG 容器 + 端口映射”。两者对外行为一致，Lucky 仍按 `127.0.0.1:8789/8788` 反代，**不改域名、不改端口**。

常用命令：

```bash
cd /opt/durlinks-demo
docker compose -f docker-compose.prod.yml ps
docker compose -f docker-compose.prod.yml logs -f api
docker compose -f docker-compose.prod.yml restart api
docker compose -f docker-compose.prod.yml down          # 停服务，保留数据卷
```

> 数据卷 `durlinks_pgdata` 独立于容器，`down` 不会丢数据；`down -v` 才会删库，别手滑。

---

## 6. 切流与回滚

切流有两条路，任选：

**A. 改 Lucky 后端地址（最快，秒级）**
Lucky → Web 服务 → 两条子规则 → 后端地址分别改成新机 `127.0.0.1:8789` / `127.0.0.1:8788`。旧机保持运行，随时改回。

**B. 改 DNS（适合跨机房）**
把两个域名 A 记录指向新机；旧机继续跑，TTL 过期前双活。验证无误后再停旧机。

**上线验收清单**

```bash
curl -s -o /dev/null -w '%{http_code}\n' https://links.example.com/
curl -s -o /dev/null -w '%{http_code}\n' https://links-admin.example.com/
# 管理端登录后核对：链接数、发布库数、LLM 配置、版本号
```

**回滚**：Lucky 改回旧机地址 / DNS 改回旧 IP。旧机在确认新机稳定前**不要删数据、不要删镜像**。

---

## 7. 容易踩的坑

| 现象 | 原因 | 处理 |
|---|---|---|
| 恢复报 `role "postgres" does not exist` | 用了 `--no-owner` 仍有 ACL | 加 `--no-privileges`（脚本已带） |
| 恢复后管理端 500 | 配置里的 JWT 密钥为空/太短 | 用备份里的 `appsettings.Production.json`（`--with-config`） |
| 页面 404 或 gzip 乱码 | 静态目录被替换后容器挂载 inode 失效 | `docker restart durlinks-demo-api` |
| 页面白屏、WASM 卡住 | 新旧指纹文件混在一起 | 清空 `admin/` 再整包解压（`bootstrap.sh` 已这么做） |
| `pg_dump: server version mismatch` | 新机 PG 版本低于旧机 | 新机用同版本或更高版本 PG |
| 恢复后链接数为 0 | 导错库 / 连接串仍指向旧库 | 看容器环境变量 `ConnectionStrings__Postgres` |
| 备份包很小（几百 KB） | `INCLUDE_STATIC=0` 或静态目录为空 | 正常；数据库仍在 `db.dump` 里 |

---

## 8. 演练清单（建议每季度一次）

- [ ] 跑一次 `backup.sh`，确认退出码 0、包大小正常
- [ ] 恢复到 `durlinks_verify` 临时库，核对链接/发布库/用户数量
- [ ] 用备份里的静态站，在本地或新机起一套只读环境，打开首页与管理端
- [ ] 确认异地副本存在（对象存储/另一台机器）
- [ ] 确认 `secrets.env`、`appsettings.Production.json` 有独立于服务器的副本
- [ ] 记录本次 RTO（恢复耗时）与 RPO（可接受丢失的时间窗）

> 当前实测：备份 ~3 秒 / 28 MB；恢复 ~2 秒；新机从零到可访问约 3-5 分钟（含装 Docker 与导镜像）。
