# 以管理员身份运行：把已发布的 Durlinks.Server 注册成 Windows 服务
# 用法：
#   powershell -ExecutionPolicy Bypass -File install-service.ps1 -InstallDir "C:\durlinks\app"
#   powershell -ExecutionPolicy Bypass -File install-service.ps1 -InstallDir "C:\durlinks\app" -Uninstall
param(
    [Parameter(Mandatory = $true)][string]$InstallDir,
    [string]$ServiceName = "DurlinksDemo",
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$exe = Join-Path $InstallDir "Durlinks.Server.exe"

if ($Uninstall) {
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        sc.exe delete $ServiceName | Out-Null
        Write-Host "服务已删除：$ServiceName"
    }
    else { Write-Host "服务不存在：$ServiceName" }
    exit 0
}

if (-not (Test-Path $exe)) { throw "未找到 $exe，请先 dotnet publish 到该目录" }

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $ServiceName -Force
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

# binPath 用引号包住，避免路径含空格；服务以 LocalSystem 运行，数据目录需可写
sc.exe create $ServiceName binPath= "`"$exe`" --contentRoot `"$InstallDir`"" start= auto DisplayName= "Durlinks Demo" | Out-Null
sc.exe description $ServiceName "Durlinks 链接仓库（用户端 8789 / 管理端 8788）" | Out-Null
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
Start-Service -Name $ServiceName
Get-Service -Name $ServiceName | Format-Table Name, Status, StartType
Write-Host "管理端：http://localhost:8788  用户端：http://localhost:8789"
