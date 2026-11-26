# Durlinks Go 服务端

原 `Durlinks.Server`（ASP.NET Core）的 Go 重写版。单进程监听两个端口，行为、路由、JSON 契约与原服务端保持一致：

- 用户端 `:8789` —— 只读，仅放行公开搜索的 GET 接口
- 管理端 `:8788` —— 全权限，除登录/验证码外均需 JWT

## 构建

```bash
cd src/Durlinks.GoServer
go build -trimpath -ldflags "-s -w" -o durlinks-server.exe .
```

依赖：`modernc.org/sqlite`（纯 Go，无需 CGO）、`github.com/jackc/pgx/v5`、`github.com/xuri/excelize/v2`。

## 运行

```bash
# 本地调试（未配置 JWT 密钥时自动生成临时密钥）
DURLINKS_ENV=development ./durlinks-server.exe -base ./run

# 生产
DURLINKS_JWT_SECRET="<至少32位随机串>" ./durlinks-server.exe -base /opt/durlinks
```

首次启动自动建库、建 `admin` 账号（默认 `admin/admin123`，可用 `DURLINKS_ADMIN_PASSWORD` 覆盖）、建默认发布库与 LLM 配置占位行。

### 目录结构（`-base` 指向的目录）

```
appsettings.json     配置（也支持 config.json）
data/durlinks.db     SQLite 数据库（provider=postgres 时不使用）
templates/导入模板.xlsx
wwwroot-user/        用户端静态站（Blazor Web 发布产物）
wwwroot-admin/       管理端静态站（含 login.html）
```

## 配置

与 `appsettings.json` 同结构，键名大小写不敏感：

```json
{
  "Database": { "Provider": "sqlite", "Path": "data/durlinks.db" },
  "ConnectionStrings": { "Postgres": "Host=...;Database=durlinks;Username=...;Password=..." },
  "Kestrel": { "Host": "localhost" },
  "Ports": { "Public": 8789, "Admin": 8788 },
  "StaticFiles": { "UserDir": "wwwroot-user", "AdminDir": "wwwroot-admin" },
  "Web": { "Mode": "go" },
  "Jwt": { "Secret": "至少 32 位随机串" }
}
```

`Web.Mode` / `Web.AdminMode` 决定页面怎么来：

| 键 | 值 | 行为 | 依赖 |
| --- | --- | --- | --- |
| `Web.Mode` | `go`（默认） | Go 用 `html/template` 渲染用户端首页，模板与 CSS 用 `embed` 打进二进制 | **零前端产物**，首屏约 36KB |
| | `static` | 回退到托管 `wwwroot-user` 里的 Blazor WASM 产物 | 需要跑过 `dotnet publish`，首屏要拉 27MB |
| `Web.AdminMode` | `go`（默认） | Go 渲染管理控制台 `/console`（模板 + `embed`），页面鉴权走登录时下发的 HttpOnly cookie | **零前端产物** |
| | `static` | 回退到托管 `wwwroot-admin` 的 Blazor 版控制台 | 需要 `wwwroot-admin` 产物 |

交互用普通 GET 表单 + 链接（筛选、翻页、库切换都体现在 URL 上，可分享可后退），行操作（隐藏/删除/AI 补料）与编辑保存由一小段内联 JS 调既有 API。

> 管理端 Go 版已覆盖：数据看板、发布管理（发布库增删改与渠道切换、搜索、来源筛选、分页、行内编辑、
> 隐藏/删除/AI 补料）、分组管理（分组/平台/标签/链接形式四类字典的增删改）、用户、大模型（提供商 CRUD、
> 设默认、连通性测试）、账号设置（MFA 开关、导出数据包、改密码）。
> 导入（粘贴 + AI 识别、Excel 上传与后台进度/取消）、批量勾选后的隐藏/显示/删除、移动/复制、去重、
> 导出 xlsx 均已迁完。`Web.AdminMode` 保持 `go` 即可，已无需要回退的功能缺口。

环境变量优先：`DURLINKS_DB_PROVIDER`、`DURLINKS_DB_PATH`、`DURLINKS_POSTGRES`、`DURLINKS_PORT_PUBLIC`、`DURLINKS_PORT_ADMIN`、`DURLINKS_JWT_SECRET`、`DURLINKS_HOST`、`DURLINKS_STATIC_USER`、`DURLINKS_STATIC_ADMIN`、`DURLINKS_ADMIN_PASSWORD`、`DURLINKS_ENV`。

也兼容 ASP.NET 风格的 `Ports__Public`、`Jwt__Secret` 等写法。

## 实现进度

已实现（与 C# 版行为对齐）：

| 模块 | 接口 |
| --- | --- |
| 鉴权 | 验证码（SVG / 图片直出）、登录、JWT、MFA（TOTP 绑定/校验/关闭）、改密、用户列表与新建 |
| 公开 | `/api/links` 搜索分页、`/api/filters`、`/api/libraries` |
| 链接 | 列表/新建/编辑/软删除/隐藏、批量隐藏删除、移动、复制（深拷贝）、去重、粘贴导入 |
| 发布库 | 列表、新建、编辑、密码二次确认删除（连带软删除链接） |
| 分类 | 分组/平台/标签/链接形式 的增删改、批量删除 |
| 看板 | `/api/admin/stats` |
| LLM | 配置多站点 CRUD、设默认、切模型、连通性测试、模型列表 |
| 用户端页面 | `GET /` 由 Go 直接渲染（`html/template` + `embed`），筛选/翻页/库切换走普通 GET，URL 可分享 |
| Excel | 模板下载、同步导入（≤10MB）、后台任务导入（进度/取消）、导出 |
| AI 补料 | 单条生成标签+摘要、批量任务（仅未补料/全部、并发 1-8、进度轮询、取消） |
| 数据包 | 密码二次确认后导出 zip（links.xlsx + libraries.json + stats.json + README） |

全部接口均已实现，不再有返回 501 的占位端点。

### AI 补料的前置条件

必须先在「LLM 设置」里保存一条可用配置（接口地址 + 密钥 + 模型），否则单条/批量补料会返回
`400 尚未配置可用的 LLM`。协议按 OpenAI 兼容实现（`POST {endpoint}/v1/chat/completions`），
本地 Ollama、中转站、官方 OpenAI 都能直接用；填 `/v1`、`/v1/chat/completions` 或只填域名都会被自动补全。

补料提示词与 C# 版一致：标签最多 5 个、摘要不超过 50 字，结果以 JSON 返回；
写回时**摘要覆盖、标签只增不删**（不会抹掉人工打的标签）。

## 与 C# 版的兼容性

- 密码哈希沿用 PBKDF2-SHA256（100000 次，`base64(salt):base64(hash)`），可直接复用旧库的 `Users.PasswordHash`
- 表名/列名为 EF 生成的 PascalCase，旧 SQLite/PostgreSQL 库可直接打开（启动时自动补列与字典回填）
- JWT 为 HS256，`iss=durlinks`、`aud=durlinks-admin`，claims 用 `nameid`/`unique_name`/`role`
