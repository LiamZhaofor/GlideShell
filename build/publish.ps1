<#
.SYNOPSIS
  发布 GlideShell：自包含单文件（裁剪 + ReadyToRun），装有 MSVC 时可改为 NativeAOT。

.DESCRIPTION
  产物在 artifacts/publish/<rid>/：GlideShell.exe（常驻程序，看门狗与 --restore-native 也是它）。
  代码已通过 NativeAOT / 裁剪分析（各项目 IsAotCompatible=true，警告即错误）。
  NativeAOT 需要 Visual Studio 的「使用 C++ 的桌面开发」工作负载（MSVC 链接器）。
  -UIAccess：用 uiAccess=true 的清单并用本机自签名证书签名（dev-cert.ps1 负责生成并让本机信任）；
  这样的 exe 只能装在 Program Files 等受保护目录里运行（install.ps1 -UIAccess）。

.EXAMPLE
  pwsh build/publish.ps1
  pwsh build/publish.ps1 -Aot          # 需要 MSVC
  pwsh build/publish.ps1 -Rid win-arm64
  pwsh build/publish.ps1 -Aot -UIAccess   # 正式自用版：NativeAOT + UIAccess + 签名
#>
param(
    [string]$Rid = "win-x64",
    [switch]$Aot,
    [switch]$UIAccess,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "artifacts/publish/$Rid"
$project = Join-Path $root "src/GlideShell/GlideShell.csproj"

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
# 普通版与 UIAccess 版只差嵌进程序集的清单，增量编译会沿用上一次的产物：每次发布前清掉宿主项目的中间文件
$hostObj = Join-Path $root "src/GlideShell/obj/$Configuration"
if (Test-Path $hostObj) { Remove-Item -Recurse -Force $hostObj }

$props = @(
    "-p:DebugType=none",
    "-p:InvariantGlobalization=false",
    "-p:SatelliteResourceLanguages=zh-Hans%3Ben"
)
if ($UIAccess) { $props += "-p:UIAccess=true" }
if ($Aot) {
    # NativeAOT 找 MSVC 的脚本按名字调用 vswhere.exe，它所在的 Visual Studio Installer 目录默认不在 PATH 里
    $installer = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer"
    if ((Test-Path (Join-Path $installer "vswhere.exe")) -and ($env:PATH -notlike "*$installer*")) { $env:PATH = "$installer;$env:PATH" }
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
if ($UIAccess) {
    & (Join-Path $PSScriptRoot "dev-cert.ps1") | Out-Host
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object Subject -eq "CN=GlideShell 本机签名" |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    $sig = Set-AuthenticodeSignature -FilePath $exe -Certificate $cert -HashAlgorithm SHA256
    if ($sig.Status -ne "Valid") { throw "签名失败：$($sig.StatusMessage)" }
    "已签名（$($cert.Subject)）"
}
"{0}  {1:N1} MB" -f $exe, ((Get-Item $exe).Length / 1MB)
