# 双数据库配置说明

后端通过配置 `Database:Provider` 在 SQLite 与 PostgreSQL 之间切换，共用同一套实体/DbContext/建表逻辑。

## 1. SQLite（本地开发 / Win/Mac 自用）

`appsettings.json`（默认即是）：

```json
{
  "Database": {
    "Provider": "sqlite",
    "Path": "data/durlinks.db",
    "SeedXlsx": null
  }
}
```

- 数据库文件相对程序基目录解析，自动建目录。
- `SeedXlsx` 可指向一份 xlsx，首次建库时导入样例（相对路径按内容根解析）。

## 2. PostgreSQL（服务器部署）

通过环境变量或 `appsettings.Production.json` 覆盖：

```json
{
  "Database": { "Provider": "postgres", "SeedXlsx": null },
  "ConnectionStrings": {
    "Postgres": "Host=127.0.0.1;Port=5432;Database=durlinks;Username=durlinks;Password=CHANGE_ME"
  },
  "Kestrel": { "Host": "0.0.0.0" }
}
```

Docker 运行时用环境变量（冒号在 Linux 环境变量里用 `__` 代替）：

```bash
Database__Provider=postgres
ConnectionStrings__Postgres="Host=db;Port=5432;Database=durlinks;Username=durlinks;Password=xxx"
Kestrel__Host=0.0.0.0
```

### 初始化 PostgreSQL 数据库

```sql
CREATE USER durlinks WITH PASSWORD 'CHANGE_ME';
CREATE DATABASE durlinks OWNER durlinks;
```

程序首次启动会 `EnsureCreated` 自动建表并写入默认 admin 账号。

## 3. 注意

- 切换 Provider 后是**两套独立数据**，不会自动迁移。若要把 SQLite 数据搬到 PostgreSQL，用管理网页的「导入 Excel」通道导出再导入。
- 默认 admin 初始密码在启动日志打印，登录后应尽快改（后续版本会加改密功能）。
- `Jwt:Secret` 部署时务必改成随机长字符串，否则令牌可被伪造。
