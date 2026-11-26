# 飞牛 fnOS（FPK）部署

这是把 Durlinks 装进飞牛 NAS「应用中心」的打包源目录。核心思路：**应用本身跑 Docker，FPK 只负责安装、入口与生命周期**。

## 目录结构

```text
deploy/fnos/
├── manifest                 # 应用元数据（名称/版本/端口）
├── ICON.PNG / ICON_256.PNG  # 64 / 256 图标
├── config/
│   ├── privilege            # 运行身份（package + docker 用户）
│   └── resource             # 声明 docker-project
├── app/
│   ├── docker/docker-compose.yaml
│   └── ui/config            # 桌面入口 → 8789
├── cmd/
│   ├── main                 # start/stop/status
│   └── install_callback     # 生成随机 JWT 密钥
└── Dockerfile               # 基础镜像 + 内置静态站
```

## 打包

```bash
# 1) 产物（dist/web、dist/admin、dist/server）
./deploy/build-release.sh

# 2) 安装飞牛官方 fnpack 后打包
./deploy/fnos/build-fpk.sh
```

产物：
- `dist/fnos/durlinks_<版本>_all.fpk`
- `dist/fnos/durlinks-demo-api_<版本>-fnos.tar.gz`（离线镜像）

## 安装到 NAS

**在线**：镜像需推到你自己的仓库（把 compose 里的 image 改成 `docker.io/你的账号/durlinks-demo-api:<版本>-fnos`），然后在应用中心「设置 → 手动安装应用」上传 `.fpk`。

**离线**（家里 NAS 更常见）：

```bash
# 电脑上导出镜像 → 上传 NAS
docker save durlinks-demo-api:1.8.26-fnos | gzip > durlinks-fnos.tar.gz
scp durlinks-fnos.tar.gz 用户@NAS:/tmp/

# NAS 上导入
gunzip -c /tmp/durlinks-fnos.tar.gz | docker load
```

再手动安装 `.fpk`。

## 访问与数据

- 用户端：`http://NAS_IP:8789`（应用中心图标直接打开）
- 管理端：`http://NAS_IP:8788`（默认 `admin / admin123`，首次登录请改密码）
- 数据：`/vol*/@appdata/durlinks/data/durlinks.db`，升级/重装不丢；备份直接拷这个文件
- 运行配置与 JWT 密钥：`/vol*/@appconf/durlinks/appsettings.Production.json`

## 注意事项

- fnOS 的应用版本号必须是 `X.Y.Z`，且与镜像 tag 对齐（脚本已自动注入）。
- 管理端口 8788 默认对局域网开放，建议只在内网使用，或在路由器/防火墙层限制。
- 想固定给某个存储卷，可在安装向导里扩展 `wizard/install`，把 `${TRIM_PKGVAR}` 换成用户选择路径。
