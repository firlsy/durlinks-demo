Durlinks Windows 部署脚本
================================================

文件编码说明（重要）
  本目录所有 .bat 均为 GBK / ANSI 编码，不是 UTF-8。
  用编辑器改完记得存成 ANSI/GBK，否则中文提示会变乱码；
  如果存成 UTF-8，cmd 还会把开头的 @echo off 解析失败并打印 "锘緻cho"。
  在 VS Code 里打开显示乱码是正常的（它默认按 UTF-8 解码），改完用记事本另存为 ANSI 即可。

脚本一览
  start.bat              前台启动，能看到实时日志，Ctrl+C 停止（推荐日常调试）
  start-background.bat   后台启动（最小化窗口），日志写 run\logs\server.log
  stop.bat               停止服务
  install-service.bat    注册为 Windows 服务（开机自启 + 崩溃自动重启），需管理员
  uninstall-service.bat  卸载服务，需管理员

目录前提
  脚本按 deploy\windows\ 位置定位到 <项目根>\run\ 目录，需要里面有：
    durlinks-server.exe     Go 编译产物（deploy/go/build-local.sh 生成）
    appsettings.json        配置（端口、数据库、JWT 密钥）
    data\                   数据库目录
    wwwroot-user\           用户端静态站
    wwwroot-admin\          管理端静态站

端口
  8789  用户端（只读）
  8788  管理端（全权限）

静默模式
  所有脚本支持 -y 参数跳过结尾的 pause，例如：start-background.bat -y
  便于写进计划任务或其它自动化流程。

服务安装注意
  binPath 里带了 -base 参数指向 run 目录：Windows 服务的默认工作目录是
  C:/Windows/System32，不显式指定会读不到 appsettings.json 而在 System32 下新建空库。
