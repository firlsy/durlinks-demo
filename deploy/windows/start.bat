@echo off
rem ============================================================
rem  Durlinks 前台启动（带端口占用检查，Ctrl+C 停止）
rem  文件编码：GBK / ANSI；请勿保存为 UTF-8，否则 cmd 中文乱码
rem ============================================================
setlocal
title Durlinks Server
cd /d "%~dp0..\..\run" 2>nul
if errorlevel 1 (
    echo [x] 找不到 run 目录，请确认脚本位置：deploy\windows\
    pause
    exit /b 1
)
if not exist "durlinks-server.exe" (
    echo [x] run\durlinks-server.exe 不存在
    echo     请先运行 deploy\go\build-local.sh 编译，并把 exe 放进 run\ 目录
    pause
    exit /b 1
)

rem 参数：-force 结束本项目的旧实例后重启；-y 执行完不暂停
set "FORCE="
set "NO_PAUSE="
:parse_args
if "%~1"=="" goto args_done
if /i "%~1"=="-force" set "FORCE=1"
if /i "%~1"=="-y" set "NO_PAUSE=1"
shift
goto parse_args
:args_done

call :getpid 8788 PID_ADMIN
call :getpid 8789 PID_PUBLIC
set "ADMIN_OURS="
set "PUBLIC_OURS="
if defined PID_ADMIN call :is_ours %PID_ADMIN% ADMIN_OURS
if defined PID_PUBLIC call :is_ours %PID_PUBLIC% PUBLIC_OURS

if defined FORCE (
    if defined ADMIN_OURS goto force_stop
    if defined PUBLIC_OURS goto force_stop
)

if defined PID_ADMIN if defined PID_PUBLIC if "%PID_ADMIN%"=="%PID_PUBLIC%" if defined ADMIN_OURS (
    echo [i] 服务已在运行：PID %PID_ADMIN% (durlinks-server.exe^)
    echo     用户端 http://localhost:8789
    echo     管理端 http://localhost:8788
    echo     若要重启，请先运行 stop.bat，或使用 start.bat -force
    if not defined NO_PAUSE pause
    exit /b 0
)

if defined PID_ADMIN goto port_conflict
if defined PID_PUBLIC goto port_conflict
goto ready

:force_stop
echo [i] 正在结束旧实例 durlinks-server.exe ...
taskkill /f /im durlinks-server.exe >nul 2>&1
%SystemRoot%\System32\timeout.exe /t 2 >nul 2>&1
call :getpid 8788 PID_ADMIN
call :getpid 8789 PID_PUBLIC
if defined PID_ADMIN goto port_conflict
if defined PID_PUBLIC goto port_conflict
goto ready

:port_conflict
echo [x] 端口已被占用，无法启动：
if defined PID_ADMIN echo     8788 管理端 PID=%PID_ADMIN%
if defined PID_PUBLIC echo     8789 用户端 PID=%PID_PUBLIC%
echo     若是本项目旧实例，请运行 stop.bat，或使用 start.bat -force
echo     若是其他程序，请结束对应进程，或修改 run\appsettings.json 的 Ports 配置。
if not defined NO_PAUSE pause
exit /b 1

:ready
echo ==============================================
echo   用户端：http://localhost:8789
echo   管理端：http://localhost:8788
echo   按 Ctrl+C 停止服务
echo ==============================================
durlinks-server.exe
echo.
echo [i] 服务已退出，退出码 %errorlevel%
if not defined NO_PAUSE pause
exit /b %errorlevel%

:getpid
set "%2="
for /f "tokens=5" %%P in ('netstat -ano -p tcp ^| findstr /R /C:":%1 .*LISTENING"') do set "%2=%%P"
exit /b

:is_ours
set "%2="
for /f "tokens=1" %%N in ('tasklist /nh /fi "PID eq %~1" 2^>nul ^| findstr /i "durlinks-server.exe"') do set "%2=1"
exit /b
