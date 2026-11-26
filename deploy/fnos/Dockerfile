# 飞牛 FPK 专用镜像：在基础镜像上内置两套静态站
# 构建上下文必须是仓库根目录（需要 dist/web、dist/admin）
ARG BASE_IMAGE=durlinks-demo-api:1.8.26
FROM ${BASE_IMAGE}

COPY dist/web /app/wwwroot-user
COPY dist/admin /app/wwwroot-admin

ENV ASPNETCORE_ENVIRONMENT=Production
