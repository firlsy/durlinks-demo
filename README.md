# Durlinks · 链接仓库

把散落在浏览器书签、聊天记录、Excel 里的链接收进一个自己的仓库：**用户端免登录搜索，管理端集中整理**。数据完全在自己手里，支持 SQLite / PostgreSQL 双后端，**单个 Go 二进制**同时提供用户端（8789）与管理端（8788）。

- 用户端：关键词 / 分组 / 平台 / 标签 / 来源筛选，卡片式布局，手机可用
- 管理端：链接增删改、发布库、分组标签、Excel 批量导入、AI 补料（摘要 + 标签）、用户与 MFA
- 部署：**不需要 .NET 运行时**，编译出一个二进制丢到 Linux / Windows 机器上就能跑
- 默认管理员账户：**`admin` / `admin123`**（首次登录后请立即修改）

> 版本 v1.8.26 · 用户端与管理端均由 Go 服务端直接渲染（`Web.Mode=go`、`Web.AdminMode=go`），不再依赖 Blazor 静态产物。

---

## 一、快速开始（3 分钟）

### 1. 编译

需要 Go 1.27+（仓库 `go.mod` 指定；Go 会自动下载对应 toolchain）。

```bash
# 本机平台编译：Linux/macOS 产物 run/durlinks-server，Windows 为 run/durlinks-server.exe
bash deploy/go/build-local.sh
```

要部署到 Linux 服务器，用交叉编译脚本（产物是静态二进制，拷过去就能跑）：

```bash
bash deploy/go/build-linux.sh          # amd64，产物 dist/durlinks-server-linux-amd64
bash deploy/go/build-linux.sh arm64    # ARM 服务器
```

### 2. 配置

首次构建会在 `run/` 生成 `appsettings.json`（已存在则保留）。关键项：

```json
{
  "Database": { "Provider": "sqlite", "Path": "data/durlinks.db" },
  "Kestrel": { "Host": "localhost" },
  "Ports": { "Public": 8789, "Admin": 8788 },
  "Web": { "Mode": "go", "AdminMode": "go" },
  "Jwt": { "Secret": "换成至少 32 位的随机串" }
}
```

- 生成 JWT 密钥：`openssl rand -base64 48`
- 对外开放把 `Kestrel:Host` 改成 `0.0.0.0`（更推荐只绑本地、用反代暴露）
- 所有配置项都能用环境变量覆盖，见第三节

### 3. 启动

```bash
cd run
./durlinks-server          # Windows：durlinks-server.exe
```

- 用户端：<http://localhost:8789>（免登录）
- 管理端：<http://localhost:8788> → 账号 `admin` / 密码 `admin123`
- 数据库：`run/data/durlinks.db`（SQLite，首次启动自动建库并写入默认管理员）
- 首次建库可用环境变量 `DURLINKS_ADMIN_PASSWORD` 覆盖初始口令

看到两个端口都起来就完事了。没有 `.NET`，没有 `dotnet run`，没有 Blazor 静态目录。

---

## 二、目录结构

| 路径 | 说明 |
|---|---|
| `src/Durlinks.GoServer` | **全部**：Go 服务端，单二进制，SSR 用户端 + 管理端 |
| `web/` · `webadmin/` | Go 服务端内嵌的前端模板与静态资源（`go:embed`，位于 `src/Durlinks.GoServer/` 下） |
| `deploy/go/` | Go 构建与本地启动脚本 |
| `deploy/windows/` | Windows 一键启动脚本（GBK/ANSI 编码，含端口守卫） |
| `deploy/` | Go 版 Dockerfile / Compose / systemd 单元 |
| `docs/` | 数据库配置、版本记录 |

历史 .NET 源码（Server / Web / AdminWeb / Shared / Desktop）已从版本控制移除，仅本地保留作迁移参考，不推送、不需要。

技术栈：Go · `net/http` + `html/template` + `go:embed` · SQLite（modernc）/ PostgreSQL（pgx）· JWT + PBKDF2。

---

## 三、配置项

配置文件：`appsettings.json`（放在二进制同目录），环境变量优先级更高。

| JSON 键 | 环境变量 | 默认 | 说明 |
|---|---|---|---|
| `Database:Provider` | `DURLINKS_DB_PROVIDER` | `sqlite` | `sqlite` 或 `postgres` |
| `Database:Path` | `DURLINKS_DB_PATH` | `data/durlinks.db` | SQLite 路径（相对程序目录） |
| `ConnectionStrings:Postgres` | `DURLINKS_POSTGRES` | — | PostgreSQL 连接串 |
| `Kestrel:Host` | `DURLINKS_HOST` | `localhost` | 对外服务设 `0.0.0.0` |
| `Ports:Public` / `Ports:Admin` | `DURLINKS_PORT_PUBLIC` / `DURLINKS_PORT_ADMIN` | `8789` / `8788` | 用户端 / 管理端端口 |
| `Jwt:Secret` | `DURLINKS_JWT_SECRET` | 占位符 | **生产必须 ≥32 位随机串** |
| `Web:Mode` / `Web:AdminMode` | — | `go` | `go`=服务端渲染；`static`=托管旧静态站 |
| `Security:BehindProxy` | — | `false` | 反代后信任 `X-Forwarded-Proto` |
| `Security:CookieSecure` | — | 自动 | 强制下发 Secure cookie（HTTPS 场景） |
| `DURLINKS_ADMIN_PASSWORD` | 同左 | `admin123` | 仅首次建库时生效 |

---

## 四、部署方式

### 4.1 Linux 二进制 + systemd（推荐单机）

```bash
# 1) 交叉编译（在开发机执行；产物是 Linux 静态二进制，约 21MB，目标机不需要装 Go）
bash deploy/go/build-linux.sh              # amd64
bash deploy/go/build-linux.sh arm64        # ARM 服务器

# 2) 服务用户与目录（SQLite 要写 data/，不能用 nobody）
sudo useradd -r -s /usr/sbin/nologin durlinks 2>/dev/null || true
sudo mkdir -p /opt/durlinks/app /opt/durlinks/data
sudo install -m 0755 dist/durlinks-server-linux-amd64 /opt/durlinks/app/durlinks-server
sudo chown -R durlinks:durlinks /opt/durlinks

# 3) 配置：放二进制同目录，程序启动时自动读 appsettings.json
sudo tee /opt/durlinks/app/appsettings.json >/dev/null <<'EOF'
{
  "Database": { "Provider": "sqlite", "Path": "/opt/durlinks/data/durlinks.db" },
  "Kestrel": { "Host": "0.0.0.0" },
  "Ports": { "Public": 8789, "Admin": 8788 },
  "Web": { "Mode": "go", "AdminMode": "go" },
  "Jwt": { "Secret": "换成 openssl rand -base64 48 的输出" }
}
EOF
sudo chown durlinks:durlinks /opt/durlinks/app/appsettings.json
sudo chmod 600 /opt/durlinks/app/appsettings.json

# 4) systemd（单元文件仓库里现成的：deploy/durlinks.service）
sudo cp deploy/durlinks.service /etc/systemd/system/durlinks.service
sudo systemctl daemon-reload
sudo systemctl enable --now durlinks
sudo systemctl status durlinks --no-pager
```

要点：

- **JWT 密钥必须 ≥32 位**，否则生产模式直接拒绝启动（含 `CHANGE_ME`、`dev-` 占位符也会被拒）；只做本地调试可设 `DURLINKS_ENV=development`。
- 用 PostgreSQL：`Database.Provider` 改 `postgres`，再加 `"ConnectionStrings": { "Postgres": "Host=...;Port=5432;Database=durlinks;Username=durlinks;Password=..." }`；也可以完全走环境变量（见 4.4）。
- 不想用配置文件，就在 unit 里加 `EnvironmentFile=/etc/durlinks/durlinks.env`，键值统一用 `DURLINKS_*`。
- **导入模板已内置进二进制**，不用再往服务器拷 xlsx；要自定义就放 `/opt/durlinks/app/templates/导入模板.xlsx` 覆盖。
- 升级 = 换 `/opt/durlinks/app/durlinks-server` + `sudo systemctl restart durlinks`；数据始终在 `/opt/durlinks/data/`。

防火墙只放行反代机器；直接暴露时放行 8789/8788，但**管理端 8788 不要裸露公网**。

### 4.2 Windows 一键启动

`deploy/windows/` 下脚本已处理 GBK 编码与端口占用（重复启动不会报「绑定端口失败」）：

```
deploy\windows\start.bat             :: 前台启动，Ctrl+C 停止
deploy\windows\start-background.bat  :: 后台启动
deploy\windows\stop.bat              :: 停止
```

工作方式：构建 `run/durlinks-server.exe` → 双击 `start.bat` → 访问 <http://localhost:8788>。
脚本会检查 8788/8789 是否已被本项目占用；被别的程序占用会拒绝启动，`-force` 只结束本项目的进程。

### 4.3 Docker Compose（PostgreSQL）

仓库自带 Go 版 Dockerfile（`deploy/Dockerfile`，多阶段构建，最终镜像只有单个二进制 + CA 证书），开箱即用：

```bash
# SQLite 单机：JWT 密钥必须≥32 位，直接一条命令起
JWT_SECRET="$(openssl rand -base64 48)" \
  docker compose -f deploy/docker-compose.yml up -d --build

# PostgreSQL 生产：先 cp deploy/.env.example deploy/.env，改 PG_PASSWORD / JWT_SECRET，再起
docker compose -f deploy/docker-compose.prod.yml up -d

# 或单独构建镜像
docker build -f deploy/Dockerfile -t durlinks-demo:latest .
```

Compose 文件在 `deploy/` 下，**要在仓库根目录执行**（构建上下文是仓库根）。

Go 服务端直接渲染两个端口，不需要 nginx / 静态站容器。环境变量方式与 4.4 相同。

### 4.4 纯环境变量启动（容器 / 无配置文件场景）

```bash
DURLINKS_DB_PROVIDER=postgres \
DURLINKS_POSTGRES="Host=db;Port=5432;Database=durlinks;Username=durlinks;Password=xxx" \
DURLINKS_HOST=0.0.0.0 \
DURLINKS_JWT_SECRET="至少32位随机串" \
./durlinks-server
```

SQLite 场景更简单，什么都不设，二进制同目录放个 `data/` 就能跑。

### 4.5 反向代理与 HTTPS

两个域名分别指向两个端口：

```nginx
server {
    listen 443 ssl http2;
    server_name links.example.com;
    location / { proxy_pass http://127.0.0.1:8789; proxy_set_header Host $host; }
}
server {
    listen 443 ssl http2;
    server_name links-admin.example.com;
    location / { proxy_pass http://127.0.0.1:8788; proxy_set_header Host $host; }
}
```

- 单域名：`/` 转 8789，`/console/` 转 8788
- Caddy：`links.example.com { reverse_proxy 127.0.0.1:8789 }`
- 反代后若登录态异常，在配置里打开 `Security:BehindProxy=true`

---

## 五、数据库

- **SQLite（默认）**：零依赖，备份就是拷 `durlinks.db`
- **PostgreSQL（多人）**：`Database:Provider=postgres` + `ConnectionStrings:Postgres`

初始化 PostgreSQL：

```sql
CREATE USER durlinks WITH PASSWORD '换成强口令';
CREATE DATABASE durlinks OWNER durlinks;
```

切换后端不会自动搬数据；用管理端「导出数据包 / 导出 Excel」迁移。表结构由服务端幂等升级。

---

## 六、备份 / 恢复

Go 单二进制部署没有独立的静态站目录，备份很简单：

```bash
# SQLite：停服后直接拷数据库文件
cp /opt/durlinks/data/durlinks.db /backup/durlinks-$(date +%F).db

# PostgreSQL：pg_dump
pg_dump -U durlinks -Fc durlinks > durlinks-$(date +%F).dump

# 连同运行配置一起留存
cp /opt/durlinks/app/appsettings.json /backup/
```

恢复时反向操作：放回数据库文件 / `pg_restore` 导入，再把 `appsettings.json` 放回程序目录，重启服务。

跨机器换系统时用管理端「导出数据包」拿到 zip（含链接、发布库、统计），在新机导入即可。数据库配置详见 [`docs/DATABASE.md`](docs/DATABASE.md)。

---

## 七、Excel 导入

模板：管理端「导入 Excel」弹窗里的 **↓ 下载导入模板** 直接下载（模板已内置进二进制，部署时不用再额外带文件；要自定义就放 `templates/导入模板.xlsx` 覆盖）。

14 列：序号 / 链接形式 / 分类 / 名称 / 链接 / 提取码 / 网盘过期时间 / 平台 / 版本 / 附属链接 / 添加时间 / 标签 / 备注 / AI摘要。

- 序号、AI摘要自动生成，不用填
- 只有「网盘」才填提取码与过期时间
- 「下载直链」必填平台
- 大文件（数万行）后台导入，关掉弹窗进度也不丢

---

## 八、安全清单（部署后务必做）

1. 登录管理端，**立刻修改 admin 密码**（账号设置 → 修改密码）
2. 换掉 `Jwt:Secret`，不要用示例值
3. 数据库使用强口令，且只监听本机/内网
4. 管理端口 8788 不要直接暴露公网；必须暴露时加反代鉴权或 IP 白名单
5. 建议给 admin 开启 MFA（账号设置 → 两步验证）
6. `secrets.env`、`appsettings.json`、数据库文件不要提交到 Git

---

## 九、常见问题

**绑定端口失败**：程序已在运行，或有别的程序占了 8788/8789。Windows 用 `deploy/windows/stop.bat` 停掉，或 `start.bat -force` 只结束本项目进程。

**改了 CSS/JS 没生效**：前端资源是 `go:embed` 编进二进制的，必须重新编译（`bash deploy/go/build-local.sh`）并重启进程，光改源文件没用。

**管理端能登录但接口 401**：令牌失效或前后端版本不匹配，重新登录；仍是 401 则重新编译部署。

**登录页白屏**：不要再用旧 Blazor 静态站目录，Go 模式下用户端/管理端由服务端直接渲染。

**导入报时间格式错误**：新版本已修复（Excel 时间按 +08:00 转 UTC），旧库请重新导入。

---

## 十、开发与发布

```bash
cd src/Durlinks.GoServer
go vet ./...                          # 静态检查
go build -trimpath -ldflags "-s -w" . # 本地编译
bash ../../deploy/go/build-local.sh   # 完整本地构建 + 部署到 run/
```

版本记录见 [`docs/VERSION.md`](docs/VERSION.md)。提交前确认 `git status` 里没有 `secrets.env`、数据库、书签导出表、`.workbuddy/` 记忆目录。

## 许可

仓库暂未附带开源许可证文件；如需公开分发，请先补充 LICENSE（例如 MIT）。
