@echo off
rem ============================================================
rem  把 durlinks-server.exe 注册成 Windows 服务（开机自启、崩溃自动重启）
rem  用法：右键 -> 以管理员身份运行
rem  文件编码：GBK / ANSI
rem ============================================================
setlocal
set "SERVICE=DurlinksDemo"
set "BASE=%~dp0..\..\run"
for %%I in ("%BASE%") do set "BASE=%%~fI"
set "EXE=%BASE%\durlinks-server.exe"

net session >nul 2>&1
if errorlevel 1 (
    echo [x] 需要管理员权限：右键本文件 -^> 以管理员身份运行
    if not "%~1"=="-y" pause
    exit /b 1
)
if not exist "%EXE%" (
    echo [x] 未找到 %EXE%
    echo     先运行 deploy\go\build-local.sh 构建，或把 exe 放进 run\ 目录
    if not "%~1"=="-y" pause
    exit /b 1
)

sc query %SERVICE% >nul 2>&1
if not errorlevel 1 (
    echo [i] 已存在同名服务，先停止并删除旧服务
    sc stop %SERVICE% >nul 2>&1
    %SystemRoot%\System32\timeout.exe /t 3 >nul
    sc delete %SERVICE% >nul 2>&1
    %SystemRoot%\System32\timeout.exe /t 2 >nul
)

rem -base 显式指定工作目录：服务默认 cwd 是 System32，不指定会找不到 appsettings.json
sc create %SERVICE% binPath= "\"%EXE%\" -base \"%BASE%\"" start= auto DisplayName= "Durlinks Demo" >nul
if errorlevel 1 (
    echo [x] 创建服务失败
    if not "%~1"=="-y" pause
    exit /b 1
)
sc description %SERVICE% "Durlinks 链接仓库（用户端 8789 / 管理端 8788）" >nul
rem 崩溃后自动重启：5s / 10s / 30s，一天后重置计数
sc failure %SERVICE% reset= 86400 actions= restart/5000/restart/10000/restart/30000 >nul
sc start %SERVICE% >nul

echo [ok] 服务已安装并启动：%SERVICE%
echo      管理端 http://localhost:8788   用户端 http://localhost:8789
echo      卸载请运行 uninstall-service.bat
if not "%~1"=="-y" pause
