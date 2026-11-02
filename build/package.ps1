<#
.SYNOPSIS
  打发布包：artifacts/GlideShell-<版本>-<rid>.zip，解压后双击 Install.cmd 即可安装（无需管理员）。

.DESCRIPTION
  包内容：
    GlideShell.exe   publish.ps1 的产物（-Aot 时为 NativeAOT，约 4.6MB；否则为自包含单文件）
    Install.cmd / Uninstall.cmd   双击入口（用 Windows PowerShell 5.1 绕过执行策略运行旁边的 ps1）。
                                  文件名用 ASCII：有的解压工具按系统代码页解中文文件名会乱码，英文名中英文用户都认得
    install.ps1 / uninstall.ps1   输出按系统界面语言（中文 / 英文）
    README.txt                    中英双语说明
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
[IO.File]::WriteAllText((Join-Path $stage "Install.cmd"),
    "@echo off`r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0install.ps1`"`r`npause`r`n", [Text.Encoding]::ASCII)
[IO.File]::WriteAllText((Join-Path $stage "Uninstall.cmd"),
    "@echo off`r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0uninstall.ps1`"`r`npause`r`n", [Text.Encoding]::ASCII)
[IO.File]::WriteAllText((Join-Path $stage "README.txt"), @"
GlideShell $version

== 中文 ==

让 Windows 触摸板手势真正跟手：手指走多少，画面走多少；停住就停住，往回滑就倒退，松手才决定去留。

安装：双击 Install.cmd。程序装到 %LOCALAPPDATA%\Programs\GlideShell，加开始菜单与开机启动，不需要管理员权限。
      已装旧版的直接安装即可升级，设置会保留。
卸载：双击 Uninstall.cmd。会先退出程序、确认 Windows 原生手势已还原，再删除程序；
      配置与日志保留（彻底删除：在这个目录运行 uninstall.ps1 -Purge）。

默认手势：
  三指上滑：窗口总览；三指下滑：只看当前程序的窗口
  三指左右滑：切换窗口（Alt+Tab 也用同一个切换条）
  四指下滑：显示桌面；四指上滑：把窗口拉回来
  四指左右滑：切换虚拟桌面
  三指轻点：鼠标中键
其他手势照旧由 Windows 处理。右键任务栏通知区域的图标可以打开设置、暂停（立即退回 Windows 原生手势）或退出。
退出、暂停或程序崩溃时，被接管的系统手势都会自动还原；万一没有还原，运行 GlideShell.exe --restore-native。

系统要求：Windows 11、精确式触摸板。程序没有正式签名，第一次运行时 Windows 可能提示「无法验证发布者」。
使用教程：https://liamzhaofor.github.io/GlideShell/guide.html

== English ==

Touchpad gestures on Windows that truly follow your fingers: the screen moves as far as your fingers do,
stops when they stop, rewinds when they reverse, and nothing is decided until you lift.

Install: double-click Install.cmd. GlideShell is installed to %LOCALAPPDATA%\Programs\GlideShell with a Start menu
         entry and startup at sign-in. No administrator rights needed. Installing over an older version upgrades it
         and keeps your settings.
Uninstall: double-click Uninstall.cmd. It exits GlideShell, confirms the native Windows gestures are restored, then
           removes the app. Settings and logs are kept (to remove them too, run uninstall.ps1 -Purge from this folder).

Default gestures:
  Three-finger swipe up: window overview; down: only the current app's windows
  Three-finger left / right: switch windows (Alt+Tab uses the same switcher)
  Four-finger swipe down: show desktop; up: bring the windows back
  Four-finger left / right: switch virtual desktops
  Three-finger tap: middle click
Other gestures are still handled by Windows. Right-click the icon in the notification area for settings, to pause
(switches straight back to native Windows gestures) or to exit. The interface follows your Windows display language
(English or Simplified Chinese) and can be changed in Settings.
On exit, pause or crash, the system gestures GlideShell took over are restored automatically; if they ever aren't,
run GlideShell.exe --restore-native.

Requirements: Windows 11 and a precision touchpad. The app isn't code-signed, so Windows may say it
"can't verify the publisher" the first time it runs.
User guide (Chinese): https://liamzhaofor.github.io/GlideShell/guide.html
"@, $utf8Bom)

$zip = Join-Path $root "artifacts/$name.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
"{0}  {1:N1} MB" -f $zip, ((Get-Item $zip).Length / 1MB)
