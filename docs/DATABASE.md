# 双数据库配置说明（Go 版）

Go 服务端通过配置 `Database:Provider` 在 SQLite 与 PostgreSQL 之间切换，表结构由服务端启动时幂等升级，无需手工执行 SQL。

## 1. SQLite（默认，零依赖）

`appsettings.json` 默认即是：

```json
{
  "Database": { "Provider": "sqlite", "Path": "data/durlinks.db" },
  "Web": { "Mode": "go", "AdminMode": "go" }
}
```

- 数据库文件相对程序工作目录解析，首次启动自动建目录与建表。
- 适合个人 / 家用 / 单机，备份就是拷 `durlinks.db`。

## 2. PostgreSQL（服务器 / 多人）

配置文件写法：

```json
{
  "Database": { "Provider": "postgres" },
  "ConnectionStrings": {
    "Postgres": "Host=127.0.0.1;Port=5432;Database=durlinks;Username=durlinks;Password=CHANGE_ME"
  },
  "Kestrel": { "Host": "0.0.0.0" }
}
```

Docker / 无配置文件场景用环境变量（Go 同时支持 `DURLINKS_*` 与 `Section__Key` 两种写法）：

```bash
DURLINKS_DB_PROVIDER=postgres
DURLINKS_POSTGRES="Host=db;Port=5432;Database=durlinks;Username=durlinks;Password=xxx"
DURLINKS_HOST=0.0.0.0
```

初始化 PostgreSQL：

```sql
CREATE USER durlinks WITH PASSWORD 'CHANGE_ME';
CREATE DATABASE durlinks OWNER durlinks;
```

程序首次启动自动建表并写入默认管理员 `admin / admin123`（可用 `DURLINKS_ADMIN_PASSWORD` 覆盖初始口令）。

## 3. 注意

- 切换 Provider 后是**两套独立数据**，不会自动迁移。SQLite → PostgreSQL 请用管理端「导出数据包 / 导出 Excel」再导入。
- 默认 admin 初始密码务必登录后立刻修改。
- `Jwt:Secret` 部署时改成随机长字符串（`openssl rand -base64 48`），否则令牌可被伪造。
- Go 服务端直出用户端与管理端，不需要 `StaticFiles` 中的 Blazor 静态站目录；只有 `Web.Mode=static` 回退模式才用得到。
