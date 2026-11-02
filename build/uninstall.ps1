<#
.SYNOPSIS
  卸载当前用户的 GlideShell，并确保 Windows 原生手势已还原。

.DESCRIPTION
  1. 正在运行的 GlideShell 正常退出（--quit，会还原系统手势）
  2. 再执行一次 --restore-native（崩溃残留的接管记录也会被还原；没有记录时什么也不做）
  3. 删除开机启动项（只删指向安装目录的那一项）、开始菜单快捷方式与安装目录：
     普通版 %LOCALAPPDATA%\Programs\GlideShell、UIAccess 版 %ProgramFiles%\GlideShell（删除要管理员权限，会弹一次 UAC）
  4. 指定 -Purge 时同时删除配置与日志

.EXAMPLE
  pwsh build/uninstall.ps1
  pwsh build/uninstall.ps1 -Purge
#>
param(
    [string]$Destination,
    [switch]$Purge,
    [switch]$RemoveOnly # 内部：提权后的子进程只删目录
)

$ErrorActionPreference = "Stop"

# 输出按系统界面语言：中文系统显示中文，其余显示英文（与 GlideShell 界面的「跟随系统」一致）。
# 不用 Get-UICulture：Windows PowerShell 在控制台代码页显示不了中文时（例如被别的程序用 UTF-8 管道启动）会退回 en-US。
# 环境变量 GLIDESHELL_LANG=zh / en 可以强制指定（测试用）。
Add-Type -Namespace GlideShellSetup -Name Lang -MemberDefinition '[DllImport("kernel32.dll")] public static extern ushort GetUserDefaultUILanguage();'
$chinese = if ($env:GLIDESHELL_LANG) { $env:GLIDESHELL_LANG -like "zh*" } else { ([GlideShellSetup.Lang]::GetUserDefaultUILanguage() -band 0x3FF) -eq 0x04 } # LANG_CHINESE
function Msg([string]$cn, [string]$en) { if ($script:chinese) { $cn } else { $en } }

if ($RemoveOnly) {
    Remove-Item -Recurse -Force $Destination
    return
}

$dirs = if ($Destination) { @($Destination) } else { @((Join-Path $env:LOCALAPPDATA "Programs\GlideShell"), (Join-Path $env:ProgramFiles "GlideShell")) }

# GlideShell.exe 是 GUI 子系统程序：PowerShell 的 & 不会等它结束，退出码要这样取
function Invoke-GlideShell([string]$exe, [string]$argument) {
    $p = Start-Process -FilePath $exe -ArgumentList $argument -PassThru
    $p.WaitForExit()
    return $p.ExitCode
}

# --quit 只等主进程；看门狗子进程（同一个 exe）随后自行退出，等它放开文件
function Wait-Released([string]$path) {
    $deadline = (Get-Date).AddSeconds(5)
    while ((Get-Process GlideShell -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $path }) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 100
    }
}

$targets = $dirs | ForEach-Object { Join-Path $_ "GlideShell.exe" } | Where-Object { Test-Path $_ }
if (-not $targets) { Write-Warning (Msg "没有找到已安装的 GlideShell，跳过退出与还原" "No installed GlideShell found; skipping exit and restore") }
foreach ($target in $targets) {
    if ((Invoke-GlideShell $target "--quit") -ne 0) { throw (Msg "运行中的 GlideShell 没有在 10 秒内退出，请从托盘退出后重试" "The running GlideShell didn't exit within 10 seconds. Exit it from the tray and try again") }
    if ((Invoke-GlideShell $target "--restore-native") -ne 0) { Write-Warning (Msg "系统手势还原未能校验通过（详见日志），可重启资源管理器或注销后再确认" "Couldn't verify that system gestures were restored (see the log); restart Explorer or sign out, then check again") }
    else { Msg "系统手势：已确认为原生设置" "System gestures: confirmed back to the native settings" }
    Wait-Released $target
}

$run = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$value = (Get-ItemProperty -Path $run -Name "GlideShell" -ErrorAction SilentlyContinue).GlideShell
if ($value -and ($dirs | Where-Object { $value -like "*$_*" })) {
    Remove-ItemProperty -Path $run -Name "GlideShell"
    Msg "开机启动：已删除" "Start at sign-in: removed"
}

# 只删指向要卸载的目录的快捷方式（另一处安装的不动）
$lnk = Join-Path ([Environment]::GetFolderPath("Programs")) "GlideShell.lnk"
$lnkTarget = if (Test-Path $lnk) { (New-Object -ComObject WScript.Shell).CreateShortcut($lnk).TargetPath } else { $null }
if ($lnkTarget -and ($dirs | Where-Object { $lnkTarget -like "$_\*" })) {
    Remove-Item $lnk
    Msg "开始菜单快捷方式：已删除" "Start menu shortcut: removed"
}

foreach ($dir in $dirs | Where-Object { Test-Path $_ }) {
    try {
        Remove-Item -Recurse -Force $dir
    } catch [UnauthorizedAccessException], [System.IO.IOException] {
        # Program Files 下的 UIAccess 版要管理员权限才能删
        $shell = (Get-Process -Id $PID).Path
        Start-Process -FilePath $shell -Verb RunAs -Wait -ArgumentList @(
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"", "-RemoveOnly", "-Destination", "`"$dir`"")
    }
    if (Test-Path $dir) { Write-Warning (Msg "没能删除 $dir" "Couldn't remove $dir") } else { Msg "安装目录：已删除 $dir" "Install folder removed: $dir" }
}

if ($Purge) {
    foreach ($dir in @((Join-Path $env:APPDATA "GlideShell"), (Join-Path $env:LOCALAPPDATA "GlideShell"))) {
        if (Test-Path $dir) {
            Remove-Item -Recurse -Force $dir
            Msg "已删除 $dir" "Removed $dir"
        }
    }
} else {
    Msg "配置与日志保留在 %APPDATA%\GlideShell 与 %LOCALAPPDATA%\GlideShell（加 -Purge 一并删除）" "Settings and logs are kept in %APPDATA%\GlideShell and %LOCALAPPDATA%\GlideShell (add -Purge to remove them too)"
}
