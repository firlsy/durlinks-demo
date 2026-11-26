# 后端 API 镜像（用户端 8789 + 管理端 8788 单进程双端口）
# 构建上下文 = 项目根目录
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Durlinks.slnx ./
COPY src/Durlinks.Shared/ src/Durlinks.Shared/
COPY src/Durlinks.Server/ src/Durlinks.Server/
RUN dotnet restore src/Durlinks.Server/Durlinks.Server.csproj
RUN dotnet publish src/Durlinks.Server/Durlinks.Server.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
RUN mkdir -p /app/templates
COPY program-durlinks-demos-导入模板.xlsx /app/templates/导入模板.xlsx

# Kestrel 由 appsettings 的 Kestrel:Host=0.0.0.0 绑定，监听 8789/8788
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8789 8788
ENTRYPOINT ["dotnet", "Durlinks.Server.dll"]
