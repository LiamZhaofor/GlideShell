<#
.SYNOPSIS
  打发布包：artifacts/GlideShell-<版本>-<rid>.zip，解压后双击「安装.cmd」即可安装（无需管理员）。

.DESCRIPTION
  包内容：
    GlideShell.exe   publish.ps1 的产物（-Aot 时为 NativeAOT，约 4.6MB；否则为自包含单文件）
    安装.cmd / 卸载.cmd   双击入口（用 Windows PowerShell 5.1 绕过执行策略运行旁边的 ps1）
    install.ps1 / uninstall.ps1
    使用说明.txt
  程序尚未签名：下载的 zip 解压后，Windows 可能提示「无法验证发布者」，需要用户确认。

.EXAMPLE
  pwsh build/package.ps1 -Aot
#>
param(
    [string]$Rid = "win-x64",
    [switch]$Aot
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot "publish.ps1") -Rid $Rid -Aot:$Aot

$version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version
$name = "GlideShell-$version-$Rid"
$stage = Join-Path $root "artifacts/package/$name"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage | Out-Null

Copy-Item (Join-Path $root "artifacts/publish/$Rid/GlideShell.exe") $stage
Copy-Item (Join-Path $PSScriptRoot "install.ps1"), (Join-Path $PSScriptRoot "uninstall.ps1") $stage

# .cmd 由 cmd.exe 按系统代码页读取：里面只放 ASCII 命令，说明文字放在 txt 里
$utf8Bom = New-Object System.Text.UTF8Encoding $true
[IO.File]::WriteAllText((Join-Path $stage "安装.cmd"),
    "@echo off`r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0install.ps1`"`r`npause`r`n", [Text.Encoding]::ASCII)
[IO.File]::WriteAllText((Join-Path $stage "卸载.cmd"),
    "@echo off`r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0uninstall.ps1`"`r`npause`r`n", [Text.Encoding]::ASCII)
[IO.File]::WriteAllText((Join-Path $stage "使用说明.txt"), @"
GlideShell $version — 让 Windows 触摸板手势真正跟手

安装：双击「安装.cmd」。程序装到 %LOCALAPPDATA%\Programs\GlideShell，加开始菜单与开机启动，不需要管理员权限。
卸载：双击「卸载.cmd」。会先退出程序、确认 Windows 原生手势已还原，再删除程序；配置与日志保留（彻底删除见 uninstall.ps1 -Purge）。

默认手势：
  三指上下滑：窗口总览（跟手展开 / 收起）
  三指左右滑：切换窗口
  四指左右滑：切换虚拟桌面
  三指轻点等其他手势照旧由 Windows 处理

右键任务栏通知区域的图标：设置、暂停（立即退回 Windows 原生手势）、开机启动、退出。
退出、暂停或程序崩溃时，被接管的系统手势都会自动还原成你原来的设置；
万一没有还原，运行 GlideShell.exe --restore-native。

系统要求：Windows 11、精确式触摸板（Precision Touchpad）。
程序尚未签名，Windows 可能提示「无法验证发布者」。
"@, $utf8Bom)

$zip = Join-Path $root "artifacts/$name.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
"{0}  {1:N1} MB" -f $zip, ((Get-Item $zip).Length / 1MB)
