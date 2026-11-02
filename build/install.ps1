<#
.SYNOPSIS
  为当前用户安装 GlideShell。

.DESCRIPTION
  1. 没有现成的发布产物时先调用 publish.ps1
  2. 正在运行的 GlideShell 先正常退出（--quit，会还原系统手势）
  3. 复制到安装目录：
       普通版   %LOCALAPPDATA%\Programs\GlideShell\（无需管理员）
       -UIAccess  %ProgramFiles%\GlideShell\（UIAccess 程序必须在受保护目录；只有复制这一步提权，会弹一次 UAC）
  4. 开始菜单快捷方式、开机启动（HKCU Run，与托盘里的「开机启动」是同一项）
  5. 以普通权限启动（UIAccess 版也不以管理员身份运行）
  配置（%APPDATA%\GlideShell）与日志（%LOCALAPPDATA%\GlideShell）不在安装目录里，重装 / 升级不会丢。

.EXAMPLE
  pwsh build/install.ps1
  pwsh build/install.ps1 -UIAccess        # 先 publish.ps1 -Aot -UIAccess
  pwsh build/install.ps1 -NoAutostart -NoStart
#>
param(
    [string]$Source,
    [string]$Destination,
    [switch]$UIAccess,
    [switch]$NoShortcut,
    [switch]$NoAutostart,
    [switch]$NoStart,
    [switch]$CopyOnly # 内部：提权后的子进程只做复制
)

$ErrorActionPreference = "Stop"

# 输出按系统界面语言：中文系统显示中文，其余显示英文（与 GlideShell 界面的「跟随系统」一致）。
# 不用 Get-UICulture：Windows PowerShell 在控制台代码页显示不了中文时（例如被别的程序用 UTF-8 管道启动）会退回 en-US。
# 环境变量 GLIDESHELL_LANG=zh / en 可以强制指定（测试用）。
Add-Type -Namespace GlideShellSetup -Name Lang -MemberDefinition '[DllImport("kernel32.dll")] public static extern ushort GetUserDefaultUILanguage();'
$chinese = if ($env:GLIDESHELL_LANG) { $env:GLIDESHELL_LANG -like "zh*" } else { ([GlideShellSetup.Lang]::GetUserDefaultUILanguage() -band 0x3FF) -eq 0x04 } # LANG_CHINESE
function Msg([string]$cn, [string]$en) { if ($script:chinese) { $cn } else { $en } }
$root = Split-Path -Parent $PSScriptRoot
$userDir = Join-Path $env:LOCALAPPDATA "Programs\GlideShell"
$protectedDir = Join-Path $env:ProgramFiles "GlideShell"
if (-not $Destination) { $Destination = if ($UIAccess) { $protectedDir } else { $userDir } }
$target = Join-Path $Destination "GlideShell.exe"

if ($CopyOnly) {
    New-Item -ItemType Directory -Force $Destination | Out-Null
    Copy-Item $Source $target -Force
    return
}

if (-not $Source) {
    # 发布包（package.ps1 打的 zip）里 exe 就在脚本旁边；在仓库里则用发布产物，没有就先发布
    $beside = Join-Path $PSScriptRoot "GlideShell.exe"
    if (Test-Path $beside) {
        $Source = $beside
    } else {
        $Source = Join-Path $root "artifacts/publish/win-x64/GlideShell.exe"
        if (-not (Test-Path $Source)) {
            if ($UIAccess) { & (Join-Path $PSScriptRoot "publish.ps1") -UIAccess } else { & (Join-Path $PSScriptRoot "publish.ps1") }
        }
    }
}
if (-not (Test-Path $Source)) { throw (Msg "找不到 $Source" "Can't find $Source") }
if ($UIAccess -and (Get-AuthenticodeSignature $Source).Status -ne "Valid") {
    throw (Msg "$Source 没有有效签名：UIAccess 版要先运行 publish.ps1 -UIAccess（会生成并信任本机证书）" "$Source has no valid signature: run publish.ps1 -UIAccess first for the UIAccess build (it creates and trusts a local certificate)")
}

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

# 升级 / 换安装位置：先让运行中的实例正常退出（它会还原系统手势），否则 exe 被占用无法覆盖。
# 用已安装的 exe 发 --quit（UIAccess 版只能在受保护目录里运行，不能直接拿发布产物来发）
$installed = @((Join-Path $userDir "GlideShell.exe"), (Join-Path $protectedDir "GlideShell.exe")) | Where-Object { Test-Path $_ }
$running = Get-Process GlideShell -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Path -Unique
foreach ($exe in @($installed) + @($running) | Select-Object -Unique) {
    if ((Invoke-GlideShell $exe "--quit") -ne 0) { throw (Msg "运行中的 GlideShell 没有在 10 秒内退出，请从托盘退出后重试" "The running GlideShell didn't exit within 10 seconds. Exit it from the tray and try again") }
    Wait-Released $exe
    break
}
# 要覆盖的那个 exe 也要等它的进程（含看门狗）都退出：发 --quit 用的可能是另一处安装的 exe（例如一键升级装回自定义目录时）
Wait-Released $target

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($UIAccess -and -not $isAdmin) {
    Msg "正在请求管理员权限，把程序复制到 $Destination ……" "Requesting administrator rights to copy the program to $Destination…"
    $shell = (Get-Process -Id $PID).Path
    $p = Start-Process -FilePath $shell -Verb RunAs -Wait -PassThru -ArgumentList @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"", "-CopyOnly", "-Source", "`"$Source`"", "-Destination", "`"$Destination`"")
    if ($p.ExitCode -ne 0 -or -not (Test-Path $target)) { throw (Msg "复制到 $Destination 失败（UAC 被取消？）" "Copying to $Destination failed (was the UAC prompt cancelled?)") }
} else {
    New-Item -ItemType Directory -Force $Destination | Out-Null
    Copy-Item $Source $target -Force
}
Msg "已安装：$target" "Installed: $target"

# 从普通版换成 UIAccess 版：删掉旧的用户目录副本，免得开始菜单 / 开机启动指向两份
if ($UIAccess -and (Test-Path $userDir)) {
    Remove-Item -Recurse -Force $userDir
    Msg "已删除旧的普通版：$userDir" "Removed the old standard build: $userDir"
}

if (-not $NoShortcut) {
    $lnk = Join-Path ([Environment]::GetFolderPath("Programs")) "GlideShell.lnk"
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($lnk)
    $shortcut.TargetPath = $target
    $shortcut.WorkingDirectory = $Destination
    $shortcut.Description = Msg "跟手的触摸板多任务手势" "Touchpad multitasking gestures that follow your fingers"
    $shortcut.Save()
    Msg "开始菜单：$lnk" "Start menu: $lnk"
}

if (-not $NoAutostart) {
    # 与 GlideShell.Platform.Shell.Autostart 写的是同一项（值的格式也必须一致，托盘里才显示为已勾选）
    $run = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
    Set-ItemProperty -Path $run -Name "GlideShell" -Value "`"$target`" --autostart"
    Msg "开机启动：已开启（可在托盘菜单里关闭）" "Start at sign-in: on (you can turn it off from the tray menu)"
}

if (-not $NoStart) {
    Start-Process $target
    Msg "已启动，图标在任务栏通知区域" "Started. The icon is in the notification area of the taskbar"
}
