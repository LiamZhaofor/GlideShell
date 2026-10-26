<#
.SYNOPSIS
  为当前用户安装 GlideShell（无需管理员权限）。

.DESCRIPTION
  1. 没有现成的发布产物时先调用 publish.ps1
  2. 正在运行的 GlideShell 先正常退出（--quit，会还原系统手势）
  3. 复制到 %LOCALAPPDATA%\Programs\GlideShell\
  4. 开始菜单快捷方式、开机启动（HKCU Run，与托盘里的「开机启动」是同一项）
  5. 启动
  配置（%APPDATA%\GlideShell）与日志（%LOCALAPPDATA%\GlideShell）不在安装目录里，重装 / 升级不会丢。

.EXAMPLE
  pwsh build/install.ps1
  pwsh build/install.ps1 -NoAutostart -NoStart
#>
param(
    [string]$Source,
    [string]$Destination = (Join-Path $env:LOCALAPPDATA "Programs\GlideShell"),
    [switch]$NoShortcut,
    [switch]$NoAutostart,
    [switch]$NoStart
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not $Source) {
    # 发布包（package.ps1 打的 zip）里 exe 就在脚本旁边；在仓库里则用发布产物，没有就先发布
    $beside = Join-Path $PSScriptRoot "GlideShell.exe"
    if (Test-Path $beside) {
        $Source = $beside
    } else {
        $Source = Join-Path $root "artifacts/publish/win-x64/GlideShell.exe"
        if (-not (Test-Path $Source)) { & (Join-Path $PSScriptRoot "publish.ps1") }
    }
}
if (-not (Test-Path $Source)) { throw "找不到 $Source" }

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

# 升级：先让运行中的实例正常退出（它会还原系统手势），否则 exe 被占用无法覆盖
if (Test-Path $target) {
    if ((Invoke-GlideShell $target "--quit") -ne 0) { throw "运行中的 GlideShell 没有在 10 秒内退出，请从托盘退出后重试" }
    Wait-Released $target
}

New-Item -ItemType Directory -Force $Destination | Out-Null
Copy-Item $Source $target -Force
"已安装：$target"

if (-not $NoShortcut) {
    $lnk = Join-Path ([Environment]::GetFolderPath("Programs")) "GlideShell.lnk"
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($lnk)
    $shortcut.TargetPath = $target
    $shortcut.WorkingDirectory = $Destination
    $shortcut.Description = "跟手的触摸板多任务手势"
    $shortcut.Save()
    "开始菜单：$lnk"
}

if (-not $NoAutostart) {
    # 与 GlideShell.Platform.Shell.Autostart 写的是同一项（值的格式也必须一致，托盘里才显示为已勾选）
    $run = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
    Set-ItemProperty -Path $run -Name "GlideShell" -Value "`"$target`" --autostart"
    "开机启动：已开启（可在托盘菜单里关闭）"
}

if (-not $NoStart) {
    Start-Process $target
    "已启动，图标在任务栏通知区域"
}
