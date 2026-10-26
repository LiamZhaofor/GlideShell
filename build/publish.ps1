<#
.SYNOPSIS
  发布 GlideShell：自包含单文件（裁剪 + ReadyToRun），装有 MSVC 时可改为 NativeAOT。

.DESCRIPTION
  产物在 artifacts/publish/<rid>/：GlideShell.exe（常驻程序，看门狗与 --restore-native 也是它）。
  代码已通过 NativeAOT / 裁剪分析（各项目 IsAotCompatible=true，警告即错误）。
  NativeAOT 需要 Visual Studio 的「使用 C++ 的桌面开发」工作负载（MSVC 链接器）。

.EXAMPLE
  pwsh build/publish.ps1
  pwsh build/publish.ps1 -Aot          # 需要 MSVC
  pwsh build/publish.ps1 -Rid win-arm64
#>
param(
    [string]$Rid = "win-x64",
    [switch]$Aot,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "artifacts/publish/$Rid"
$project = Join-Path $root "src/GlideShell/GlideShell.csproj"

if (Test-Path $out) { Remove-Item -Recurse -Force $out }

$props = @(
    "-p:DebugType=none",
    "-p:InvariantGlobalization=false",
    "-p:SatelliteResourceLanguages=zh-Hans%3Ben"
)
if ($Aot) {
    $props += "-p:PublishAot=true"
} else {
    $props += @(
        "--self-contained", "true",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:PublishTrimmed=true",
        "-p:PublishReadyToRun=true",
        "-p:EnableCompressionInSingleFile=true"
    )
}

dotnet publish $project -c $Configuration -r $Rid -o $out @props
if ($LASTEXITCODE -ne 0) { throw "发布失败" }

$exe = Join-Path $out "GlideShell.exe"
"{0}  {1:N1} MB" -f $exe, ((Get-Item $exe).Length / 1MB)
