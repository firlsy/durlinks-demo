@echo off
rem ============================================================
rem  Durlinks 后台启动（最小化窗口，日志写 run\logs\server.log）
rem  文件编码：GBK / ANSI，别改成 UTF-8 保存
rem ============================================================
setlocal
title Durlinks Server (background)
cd /d "%~dp0..\..\run" 2>nul
if errorlevel 1 (
    echo [x] 找不到 run 目录，请确认脚本位置：deploy\windows\
    if not "%~1"=="-y" pause
    exit /b 1
)
if not exist "durlinks-server.exe" (
    echo [x] run\durlinks-server.exe 不存在，请先构建
    if not "%~1"=="-y" pause
    exit /b 1
)
if not exist "logs" mkdir "logs"

echo [i] 正在后台启动，日志：run\logs\server.log
start "durlinks-server" /min cmd /c "durlinks-server.exe >> logs\server.log 2>&1"

rem 轮询等待端口就绪（最多 20 秒），避免刚 start 就判失败
rem 用 %SystemRoot%\System32\timeout.exe 全路径，防止被其它同名程序劫持
set /a TRY=0
:waitloop
set /a TRY+=1
netstat -ano | findstr ":8788" | findstr "LISTENING" >nul 2>&1
if not errorlevel 1 goto started
if %TRY% GEQ 20 goto failed
%SystemRoot%\System32\timeout.exe /t 1 >nul 2>&1
goto waitloop

:started
echo [ok] 已启动
echo      管理端 http://localhost:8788   用户端 http://localhost:8789
echo      停止服务请运行 stop.bat
goto done

:failed
echo [x] 启动失败，请查看 run\logs\server.log

:done
if not "%~1"=="-y" pause
