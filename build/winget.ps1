<#
.SYNOPSIS
  生成 GlideShell 的 winget 清单（microsoft/winget-pkgs 用）并本地校验。

.DESCRIPTION
  从 GitHub Release 读取发布包的地址、大小与 SHA-256（GitHub 给每个附件算好的摘要），生成四个清单文件：
    LiamZhaofor.GlideShell.yaml                  版本清单
    LiamZhaofor.GlideShell.installer.yaml        安装清单（zip 里的 GlideShell.exe 按便携应用装，命令别名 glideshell）
    LiamZhaofor.GlideShell.locale.en-US.yaml     默认语言
    LiamZhaofor.GlideShell.locale.zh-CN.yaml     中文
  输出到 artifacts/winget/<版本>/，再运行 winget validate。提交到 winget-pkgs 时，这个目录放到
  manifests/l/LiamZhaofor/GlideShell/<版本>/ 下。

  winget 按「便携应用」装：解压到 winget 自己的目录、注册 glideshell 命令，不跑 install.ps1，
  所以没有开始菜单快捷方式；开机启动在 GlideShell 设置里打开。

.EXAMPLE
  pwsh build/winget.ps1                # 版本取 Directory.Build.props
  pwsh build/winget.ps1 -Version 1.6.2
#>
param([string]$Version)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
$id = "LiamZhaofor.GlideShell"
$repo = "LiamZhaofor/GlideShell"
$tag = "v$Version"
$asset = "GlideShell-$Version-win-x64.zip"

$release = gh api "repos/$repo/releases/tags/$tag" | ConvertFrom-Json
$a = $release.assets | Where-Object name -eq $asset
if (-not $a) { throw "$tag 里没有 $asset（先发布）" }
if ($a.digest -notmatch '^sha256:([0-9a-fA-F]{64})$') { throw "$asset 没有 SHA-256 摘要" }
$sha = $Matches[1].ToUpperInvariant()
$date = ([datetime]$release.published_at).ToString("yyyy-MM-dd")
$schema = "1.12.0"

$out = Join-Path $root "artifacts/winget/$Version"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $out | Out-Null

function Write-Manifest([string]$name, [string]$kind, [string]$body) {
    $text = "# yaml-language-server: `$schema=https://aka.ms/winget-manifest.$kind.$schema.schema.json`n`n" + $body.Trim() + "`n"
    [IO.File]::WriteAllText((Join-Path $out $name), $text, [Text.UTF8Encoding]::new($false))
}

Write-Manifest "$id.yaml" "version" @"
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $schema
"@

Write-Manifest "$id.installer.yaml" "installer" @"
PackageIdentifier: $id
PackageVersion: $Version
Platform:
- Windows.Desktop
MinimumOSVersion: 10.0.22000.0
InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
- RelativeFilePath: GlideShell.exe
  PortableCommandAlias: glideshell
UpgradeBehavior: uninstallPrevious
ReleaseDate: $date
Installers:
- Architecture: x64
  InstallerUrl: $($a.browser_download_url)
  InstallerSha256: $sha
ManifestType: installer
ManifestVersion: $schema
"@

Write-Manifest "$id.locale.en-US.yaml" "defaultLocale" @"
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: LiamZhaofor
PublisherUrl: https://github.com/LiamZhaofor
PublisherSupportUrl: https://github.com/$repo/issues
Author: LiamZhaofor
PackageName: GlideShell
PackageUrl: https://liamzhaofor.github.io/GlideShell/
License: MIT
LicenseUrl: https://github.com/$repo/blob/main/LICENSE
Copyright: Copyright (c) 2026 LiamZhaofor
ShortDescription: Continuous, finger-following touchpad gestures for Windows 11 (overview, window switching, virtual desktops).
Description: |-
  GlideShell turns Windows 11 precision-touchpad gestures into continuous, finger-following interactions in the spirit of macOS Mission Control:
  the screen moves exactly as far as your fingers, stops when they stop, reverses when they reverse, and only decides on release.
  Three-finger swipe up for an overview of all windows, sideways to switch windows, four fingers to switch virtual desktops or show the desktop.
  Also replaces Alt+Tab with the same switcher, adds mouse-button drag gestures and hot corners. Native, about 6 MB, no driver.
Moniker: glideshell
Tags:
- gestures
- mission-control
- multitasking
- precision-touchpad
- touchpad
- virtual-desktop
ReleaseNotesUrl: https://github.com/$repo/releases/tag/$tag
Documentations:
- DocumentLabel: User guide
  DocumentUrl: https://liamzhaofor.github.io/GlideShell/guide.html
ManifestType: defaultLocale
ManifestVersion: $schema
"@

Write-Manifest "$id.locale.zh-CN.yaml" "locale" @"
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: zh-CN
Publisher: LiamZhaofor
PackageName: GlideShell
License: MIT
ShortDescription: 让 Windows 11 触摸板手势真正跟手：窗口总览、切换窗口、切换虚拟桌面。
Description: |-
  手指走多少，画面走多少；停住就停住，往回滑就倒退，松手才决定去留。为 Windows 11 精确式触摸板打造的 macOS 式多任务体验：
  三指上滑窗口总览、三指横滑切换窗口、四指切换虚拟桌面与显示桌面；Alt+Tab 也换成同一个切换条，还有鼠标按键手势与触发角。原生程序约 6MB，不装驱动。
Tags:
- 多任务
- 手势
- 触摸板
- 虚拟桌面
ReleaseNotesUrl: https://github.com/$repo/releases/tag/$tag
Documentations:
- DocumentLabel: 使用教程
  DocumentUrl: https://liamzhaofor.github.io/GlideShell/guide.html
ManifestType: locale
ManifestVersion: $schema
"@

"清单：$out（$asset，SHA-256 $sha）"
winget validate --manifest $out
if ($LASTEXITCODE -ne 0) { throw "winget validate 没有通过" }
