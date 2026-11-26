@echo off
rem ============================================================
rem  卸载 Durlinks Windows 服务
rem  用法：右键 -> 以管理员身份运行
rem  文件编码：GBK / ANSI
rem ============================================================
setlocal
set "SERVICE=DurlinksDemo"

net session >nul 2>&1
if errorlevel 1 (
    echo [x] 需要管理员权限：右键本文件 -^> 以管理员身份运行
    if not "%~1"=="-y" pause
    exit /b 1
)

sc query %SERVICE% >nul 2>&1
if errorlevel 1 (
    echo [i] 服务不存在：%SERVICE%
    if not "%~1"=="-y" pause
    exit /b 0
)

sc stop %SERVICE% >nul 2>&1
%SystemRoot%\System32\timeout.exe /t 3 >nul
sc delete %SERVICE% >nul 2>&1
if errorlevel 1 (
    echo [x] 删除失败，服务可能仍在停止中，稍后重试
) else (
    echo [ok] 已删除服务：%SERVICE%
)
if not "%~1"=="-y" pause
