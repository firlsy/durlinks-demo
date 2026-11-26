@echo off
rem ============================================================
rem  Durlinks 本地启动（前台，能看到日志，Ctrl+C 停止）
rem  文件编码：GBK / ANSI，别用 UTF-8 保存，否则中文会乱码
rem ============================================================
setlocal
title Durlinks Server
cd /d "%~dp0..\..\run" 2>nul
if errorlevel 1 (
    echo [x] 找不到 run 目录，请确认脚本位置：deploy\windows\
    if not "%~1"=="-y" pause
    exit /b 1
)
if not exist "durlinks-server.exe" (
    echo [x] run\durlinks-server.exe 不存在
    echo     先运行 deploy\go\build-local.sh 构建，或把编译好的 exe 放进 run\ 目录
    if not "%~1"=="-y" pause
    exit /b 1
)

netstat -ano | findstr ":8788" | findstr "LISTENING" >nul 2>&1
if not errorlevel 1 echo [!] 8788 端口已被占用，可能已经有一个实例在跑
netstat -ano | findstr ":8789" | findstr "LISTENING" >nul 2>&1
if not errorlevel 1 echo [!] 8789 端口已被占用

echo ==============================================
echo   用户端：http://localhost:8789
echo   管理端：http://localhost:8788
echo   按 Ctrl+C 停止服务
echo ==============================================
durlinks-server.exe
echo.
echo [i] 服务已退出，退出码 %errorlevel%
if not "%~1"=="-y" pause
