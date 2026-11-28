# Go 单二进制镜像：用户端 8789 + 管理端 8788 同进程
# 构建上下文 = 项目根目录
#   docker build -f deploy/Dockerfile -t durlinks-demo:latest .
FROM golang:1.27-alpine AS build
WORKDIR /src
COPY src/Durlinks.GoServer/go.mod src/Durlinks.GoServer/go.sum ./
RUN go mod download
COPY src/Durlinks.GoServer/ ./
RUN CGO_ENABLED=0 go build -trimpath -ldflags "-s -w" -o /durlinks-server .

FROM alpine:3.20
RUN apk add --no-cache ca-certificates tzdata && mkdir -p /app/data /app/templates
WORKDIR /app
COPY --from=build /durlinks-server /app/durlinks-server
COPY program-durlinks-demos-导入模板.xlsx /app/templates/导入模板.xlsx
EXPOSE 8789 8788
ENTRYPOINT ["/app/durlinks-server"]
