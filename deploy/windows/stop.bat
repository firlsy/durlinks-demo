@echo off
rem ============================================================
rem  Durlinks 停止服务
rem  文件编码：GBK / ANSI
rem ============================================================
setlocal
tasklist /fi "imagename eq durlinks-server.exe" 2>nul | findstr /i "durlinks-server.exe" >nul
if errorlevel 1 (
    echo [i] 没有正在运行的 durlinks-server.exe
    if not "%~1"=="-y" pause
    exit /b 0
)
taskkill /f /im durlinks-server.exe >nul 2>&1
if errorlevel 1 (
    echo [x] 停止失败，可能需要在任务管理器手动结束，或以管理员身份运行本脚本
) else (
    echo [ok] 已停止 durlinks-server.exe
)
if not "%~1"=="-y" pause
