<#
.SYNOPSIS
  卸载当前用户的 GlideShell，并确保 Windows 原生手势已还原。

.DESCRIPTION
  1. 正在运行的 GlideShell 正常退出（--quit，会还原系统手势）
  2. 再执行一次 --restore-native（崩溃残留的接管记录也会被还原；没有记录时什么也不做）
  3. 删除开机启动项（只删指向本安装目录的那一项）、开始菜单快捷方式与安装目录
  4. 指定 -Purge 时同时删除配置与日志

.EXAMPLE
  pwsh build/uninstall.ps1
  pwsh build/uninstall.ps1 -Purge
#>
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA "Programs\GlideShell"),
    [switch]$Purge
)

$ErrorActionPreference = "Stop"
$target = Join-Path $Destination "GlideShell.exe"

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

if (Test-Path $target) {
    if ((Invoke-GlideShell $target "--quit") -ne 0) { throw "运行中的 GlideShell 没有在 10 秒内退出，请从托盘退出后重试" }
    if ((Invoke-GlideShell $target "--restore-native") -ne 0) { Write-Warning "系统手势还原未能校验通过（详见日志），可重启资源管理器或注销后再确认" }
    else { "系统手势：已确认为原生设置" }
    Wait-Released $target
} else {
    Write-Warning "$target 不存在，跳过退出与还原"
}

$run = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$value = (Get-ItemProperty -Path $run -Name "GlideShell" -ErrorAction SilentlyContinue).GlideShell
if ($value -and $value -like "*$target*") {
    Remove-ItemProperty -Path $run -Name "GlideShell"
    "开机启动：已删除"
}

$lnk = Join-Path ([Environment]::GetFolderPath("Programs")) "GlideShell.lnk"
if (Test-Path $lnk) {
    Remove-Item $lnk
    "开始菜单快捷方式：已删除"
}

if (Test-Path $Destination) {
    Remove-Item -Recurse -Force $Destination
    "安装目录：已删除 $Destination"
}

if ($Purge) {
    foreach ($dir in @((Join-Path $env:APPDATA "GlideShell"), (Join-Path $env:LOCALAPPDATA "GlideShell"))) {
        if (Test-Path $dir) {
            Remove-Item -Recurse -Force $dir
            "已删除 $dir"
        }
    }
} else {
    "配置与日志保留在 %APPDATA%\GlideShell 与 %LOCALAPPDATA%\GlideShell（加 -Purge 一并删除）"
}
