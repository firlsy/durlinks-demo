@echo off
rem 本地启动 Go 服务端（Windows）
setlocal
cd /d "%~dp0\..\..\run"
if not exist durlinks-server.exe (
  echo [x] 未找到 durlinks-server.exe，请先执行 deploy\go\build-local.sh
  pause
  exit /b 1
)
echo 用户端 http://localhost:8789
echo 管理端 http://localhost:8788
echo 关闭此窗口不会停止服务；如需停止请任务管理器结束 durlinks-server.exe
start "durlinks-server" durlinks-server.exe
