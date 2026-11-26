# Durlinks · 链接仓库

把散落在浏览器书签、聊天记录、Excel 里的链接收进一个自己的仓库：**用户端免登录搜索，管理端集中整理**。数据完全在自己手里，支持 SQLite / PostgreSQL 双后端，单进程双端口（用户端只读、管理端全权限）。

- 用户端：关键词 / 分组 / 平台 / 标签 / 来源筛选，卡片式布局，手机可用
- 管理端：链接增删改、发布库、分组管理、Excel 批量导入、AI 识别与摘要、用户与 MFA
- 客户端：Avalonia 桌面端（Windows / macOS / Linux），纯搜索
- 部署：二进制、Docker、Docker Compose、Windows 服务、飞牛 fnOS（FPK）、各类 NAS/面板

> 当前版本 **v1.8.26** · 默认管理员 **admin / admin123**（首次登录请立即修改）

---

## 一、目录结构

| 路径 | 说明 |
|---|---|
| `src/Durlinks.Shared` | 实体、DTO、枚举 |
| `src/Durlinks.Server` | ASP.NET Core 后端，单进程监听 8789（公开）/ 8788（管理） |
| `src/Durlinks.Web` | Blazor WASM 用户端 |
| `src/Durlinks.AdminWeb` | Blazor WASM 管理端 |
| `src/Durlinks.Desktop` | Avalonia 桌面客户端 |
| `deploy/` | Dockerfile、compose、备份/迁移脚本、systemd、Windows、fnOS 打包 |
| `docs/` | 数据库、备份迁移、版本记录 |
| `program-durlinks-demos-导入模板.xlsx` | Excel 导入模板（14 列） |

技术栈：.NET 10 · ASP.NET Core · EF Core（SQLite / Npgsql）· Blazor WebAssembly · Avalonia · ClosedXML · JWT + TOTP。

---

## 二、5 分钟本地跑起来

要求：.NET 10 SDK。

```bash
dotnet build Durlinks.slnx
dotnet run --project src/Durlinks.Server
```

- 用户端 <http://localhost:8789>（免登录）
- 管理端 <http://localhost:8788> → `admin / admin123`
- 数据库：`src/Durlinks.Server/data/durlinks.db`（SQLite，自动建库）
- 首次启动日志会打印管理员账号；可用环境变量 `DURLINKS_ADMIN_PASSWORD` 覆盖初始口令

想导入样例数据：把 `Database:SeedXlsx` 指向一个 xlsx（见 `appsettings.Development.json`）。

---

## 三、配置项

配置文件：`appsettings.json`，生产用 `appsettings.Production.json` 或环境变量覆盖（层级用 `__`）。

| 键 | 默认 | 说明 |
|---|---|---|
| `Database:Provider` | `sqlite` | `sqlite` 或 `postgres` |
| `Database:Path` | `data/durlinks.db` | SQLite 路径（相对程序目录） |
| `ConnectionStrings:Postgres` | — | PostgreSQL 连接串 |
| `Kestrel:Host` | `localhost` | 对外提供服务时设 `0.0.0.0` |
| `Ports:Public` / `Ports:Admin` | `8789` / `8788` | 公开 / 管理端口 |
| `StaticFiles:UserDir` / `AdminDir` | `wwwroot-user` / `wwwroot-admin` | 两套前端静态站目录 |
| `Jwt:Secret` | 占位符 | **生产必须 ≥32 位随机串**，否则拒绝启动 |
| `DURLINKS_ADMIN_PASSWORD` | `admin123` | 仅首次建库时生效 |

生成密钥：

```bash
openssl rand -base64 48
```

---

## 四、部署方式（按场景选一种）

| 场景 | 方式 | 入口 |
|---|---|---|
| 本机试用 | `dotnet run` | 第二节 |
| 一台 Linux 服务器 | 二进制 + systemd | 4.3 |
| 想省事、要 PostgreSQL | Docker Compose | 4.1 |
| 已有 Docker、单容器 | `docker run` | 4.2 |
| Windows 电脑/小主机 | 二进制 + Windows 服务 | 4.4 |
| 飞牛 NAS | FPK 应用包 | 4.5 |
| 群晖 / 威联通 / 1Panel / 宝塔 | 用 Docker 方式 | 4.2 / 4.6 |
| 需要 HTTPS / 域名 | 反向代理 | 4.7 |

通用前置：先出产物。

```bash
./deploy/build-release.sh          # dist/server、dist/web、dist/admin
./deploy/build-release.sh --docker # 再加镜像 durlinks-demo-api:<版本> 与 latest
```

### 4.1 Docker Compose（推荐，PostgreSQL）

```bash
# 1) 配置
cp deploy/appsettings.Production.example.json deploy/server/appsettings.Production.json
#    改 ConnectionStrings:Postgres 与 Jwt:Secret；StaticFiles 保持容器内路径
cp deploy/.env.example deploy/.env
#    改 PG_PASSWORD，API_IMAGE=durlinks-demo-api:1.8.26

# 2) 静态站与模板
mkdir -p deploy/web deploy/admin deploy/data
cp -r dist/web/. deploy/web/
cp -r dist/admin/. deploy/admin/
mkdir -p deploy/server && cp program-durlinks-demos-导入模板.xlsx deploy/server/导入模板.xlsx

# 3) 启动（db + api）
docker compose -f deploy/docker-compose.prod.yml --env-file deploy/.env up -d
```

- 对外只绑 `127.0.0.1:8788/8789`，公网入口交给反代（4.7）
- 升级：重建镜像 → 改 `.env` 里的 `API_IMAGE` → `up -d`
- 数据：命名卷 `durlinks_pgdata`；容器内 `/app/data` 挂 `deploy/data`

### 4.2 Docker 单容器（SQLite，最简单）

```bash
docker build -f deploy/Dockerfile -t durlinks-demo-api:1.8.26 .

mkdir -p deploy/data deploy/web deploy/admin
cp -r dist/web/. deploy/web/ && cp -r dist/admin/. deploy/admin/
cp deploy/appsettings.Production.example.json deploy/server/appsettings.Production.json
# 改 Database:Provider=sqlite、StaticFiles:UserDir=/opt/durlinks-demo/web、AdminDir=.../admin

docker run -d --name durlinks-demo-api --restart unless-stopped \
  -p 8789:8789 -p 8788:8788 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -v "$PWD/deploy/server/appsettings.Production.json:/app/appsettings.Production.json:ro" \
  -v "$PWD/deploy/data:/app/data" \
  -v "$PWD/deploy/web:/opt/durlinks-demo/web:ro" \
  -v "$PWD/deploy/admin:/opt/durlinks-demo/admin:ro" \
  -v "$PWD/deploy/server/导入模板.xlsx:/app/templates/导入模板.xlsx:ro" \
  durlinks-demo-api:1.8.26
```

### 4.3 Linux 二进制 + systemd

```bash
apt install -y aspnetcore-runtime-10.0     # 或安装 .NET 10 Runtime
useradd -r -s /sbin/nologin durlinks
mkdir -p /opt/durlinks-demo/{app,web,admin,data}

cp -r dist/server/.   /opt/durlinks-demo/app/
cp -r dist/web/.      /opt/durlinks-demo/web/
cp -r dist/admin/.    /opt/durlinks-demo/admin/
cp deploy/appsettings.Production.example.json /opt/durlinks-demo/app/appsettings.Production.json
cp deploy/durlinks.service /etc/systemd/system/durlinks.service
chown -R durlinks:durlinks /opt/durlinks-demo

systemctl daemon-reload && systemctl enable --now durlinks
```

`deploy/appsettings.Production.example.json` 已按 `/opt/durlinks-demo` 配好静态目录，只需改数据库口令与 JWT 密钥。

> 不想装运行时：`dotnet publish src/Durlinks.Server -c Release -r linux-x64 --self-contained true -o dist/server-sc`，把 `ExecStart` 指向自包含可执行文件。

### 4.4 Windows（二进制 / 服务）

```powershell
dotnet publish src/Durlinks.Server -c Release -r win-x64 --self-contained true -o C:\durlinks\app
dotnet publish src/Durlinks.Web -c Release -o C:\durlinks\_web
dotnet publish src/Durlinks.AdminWeb -c Release -o C:\durlinks\_admin
Copy-Item C:\durlinks\_web\wwwroot\*   C:\durlinks\web   -Recurse
Copy-Item C:\durlinks\_admin\wwwroot\* C:\durlinks\admin -Recurse
Copy-Item deploy\appsettings.Production.example.json C:\durlinks\app\appsettings.Production.json
```

把 `appsettings.Production.json` 改成 Windows 路径并允许局域网：

```json
{
  "Database": { "Provider": "sqlite", "Path": "C:\\durlinks\\data\\durlinks.db" },
  "Kestrel": { "Host": "0.0.0.0" },
  "StaticFiles": { "UserDir": "C:\\durlinks\\web", "AdminDir": "C:\\durlinks\\admin" },
  "Jwt": { "Secret": "换成长随机串" }
}
```

注册服务（管理员 PowerShell）：

```powershell
powershell -ExecutionPolicy Bypass -File deploy\windows\install-service.ps1 -InstallDir "C:\durlinks\app"
```

- 管理端 <http://localhost:8788>，用户端 <http://localhost:8789>
- 卸载：`-Uninstall`
- 只做桌面搜索：直接运行 `src/Durlinks.Desktop` 发布的可执行文件

### 4.5 飞牛 fnOS（FPK）

`deploy/fnos/` 是完整打包源（manifest / 图标 / docker-project / 生命周期脚本）：

```bash
./deploy/build-release.sh
./deploy/fnos/build-fpk.sh
# → dist/fnos/durlinks_<版本>_all.fpk + 离线镜像 tar.gz
```

NAS 上 `docker load` 镜像后，应用中心「手动安装」选 `.fpk`。数据在 `/vol*/@appdata/durlinks/data`，升级不丢。详见 [`deploy/fnos/README.md`](deploy/fnos/README.md)。

### 4.6 群晖 / 威联通 / 1Panel / 宝塔

本质都是 Docker：用 4.2 的命令，或把 4.1 的 compose 粘贴进面板的「Compose / 编排」功能。注意三点：

1. 静态站目录必须挂进容器，且与 `StaticFiles` 配置一致；
2. `Kestrel:Host` 必须是 `0.0.0.0`；
3. 两个端口都要映射（8789 公开、8788 管理），管理端口不要直接暴露公网。

### 4.7 反向代理与 HTTPS

两个域名分别指向两个端口（同机部署示例）：

```nginx
server {
    listen 443 ssl http2;
    server_name links.example.com;
    # ssl_certificate ...
    location / { proxy_pass http://127.0.0.1:8789; proxy_set_header Host $host; }
}
server {
    listen 443 ssl http2;
    server_name links-admin.example.com;
    # ssl_certificate ...
    location / { proxy_pass http://127.0.0.1:8788; proxy_set_header Host $host; }
}
```

- 单域名方案：`/` 转 8789，`/admin/` 转 8788（前端为 SPA，需 `try_files $uri $uri/ /index.html`）
- Caddy 两行即可：`links.example.com { reverse_proxy 127.0.0.1:8789 }`
- Lucky：Web 服务 → 子规则，分别填前端域名 → 内网 `IP:8789/8788`，管理端建议加 BasicAuth
- 管理端强烈建议加 IP 白名单 / VPN / BasicAuth，登录本身有验证码 + 可选 MFA

---

## 五、数据库

- **SQLite（默认）**：零依赖，适合个人/家用；备份就是拷 `durlinks.db`
- **PostgreSQL（推荐多人）**：`Database:Provider=postgres` + `ConnectionStrings:Postgres`

初始化 PostgreSQL：

```sql
CREATE USER durlinks WITH PASSWORD '换成强口令';
CREATE DATABASE durlinks OWNER durlinks;
```

切换后端不会自动搬数据；需要迁移时用管理端「导出数据」或备份脚本中的 `pg_dump`。表结构由 `SchemaUpgrader` 幂等升级，升级版本无需手工执行 SQL。

---

## 六、备份 / 恢复 / 换机

```bash
# 备份（数据库 + 运行配置 + 两套静态站，默认保留 14 份）
/opt/durlinks-demo/deploy/backup.sh

# 恢复到临时库演练（不碰生产）
./deploy/restore.sh backups/durlinks-xxx.tar.gz --yes --db-name durlinks_verify

# 新机初始化 / 整机迁移
PG_PASSWORD='新库口令' BACKUP_ARCHIVE=./durlinks-xxx.tar.gz ./deploy/bootstrap.sh
NEW_HOST=1.2.3.4 SSHPASS='新机密码' PG_PASSWORD='新库口令' ./deploy/migrate.sh
```

详细步骤与回滚见 [`docs/BACKUP-AND-MIGRATE.md`](docs/BACKUP-AND-MIGRATE.md)。

---

## 七、Excel 导入

模板：`program-durlinks-demos-导入模板.xlsx`（含「填写说明」sheet 与列校验）。

14 列：序号 / 链接形式 / 分类 / 名称 / 链接 / 提取码 / 网盘过期时间 / 平台 / 版本 / 附属链接 / 添加时间 / 标签 / 备注 / AI摘要。

- 序号、AI摘要自动生成，不用填
- 只有「网盘」才填提取码与过期时间
- 「下载直链」必填平台
- 大文件（数万行）后台导入，可关弹窗，进度不丢

---

## 八、安全清单（部署后务必做）

1. 登录管理端，**立刻修改 admin 密码**（账号设置 → 修改密码）
2. 换掉 `Jwt:Secret`，不要用示例值
3. 数据库使用强口令，且只监听本机/内网
4. 管理端口 8788 不要直接暴露公网；必须暴露时加反代鉴权或 IP 白名单
5. 建议给 admin 开启 MFA（账号设置 → 两步验证）
6. `secrets.env`、`appsettings.Production.json`、数据库文件不要提交到 Git

---

## 九、常见问题

**登录页白屏 / 404**：静态站没挂对，或 `StaticFiles:AdminDir` 指到了父目录。目录里应直接有 `index.html`、`login.html`。

**换静态站目录后仍是旧页面**：容器 bind mount 认 inode，`docker restart durlinks-demo-api` 即可。

**导入报 `Cannot write DateTime with Kind=Unspecified`**：旧版本问题，v1.8.24 起已修复（Excel 时间按 +08:00 转 UTC）。

**下载导入模板 401 / 浏览器提示无法下载**：v1.8.24 起改为带令牌下载，确认镜像与静态站版本一致。

**管理端能登录但接口 401**：前端与后端版本不匹配，重建镜像并整目录替换静态站。

---

## 十、开发与发布

```bash
dotnet build Durlinks.slnx
dotnet test                        # 如无测试项目则跳过
./deploy/build-release.sh --docker # 版本取 Directory.Build.props
```

版本记录见 [`docs/VERSION.md`](docs/VERSION.md)。提交前请确认 `git status` 里没有 `secrets.env`、数据库、书签导出表等本地数据。

## 许可

仓库暂未附带开源许可证文件；如需公开分发，请先补充 LICENSE（例如 MIT）。
