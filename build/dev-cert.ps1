<#
.SYNOPSIS
  生成（或复用）本机自签名代码签名证书，并让本机信任它（UIAccess 版 GlideShell 需要）。

.DESCRIPTION
  1. 在「当前用户\个人」证书存储里找 CN=GlideShell 本机签名；没有就用 New-SelfSignedCertificate 生成（RSA 3072，5 年）。
     私钥只在本机、不可导出。
  2. 把公钥证书（不含私钥）导入「本地计算机\受信任的根证书颁发机构」与「受信任的发布者」：
     UIAccess 程序启动时由系统服务校验签名链，必须是本机级别的信任。这一步需要管理员权限，会弹一次 UAC。
  只对这台电脑有效；分发给别人需要正式的代码签名证书。

.EXAMPLE
  pwsh build/dev-cert.ps1
#>
param([switch]$TrustOnly, [string]$CerPath)

$ErrorActionPreference = "Stop"
$subject = "CN=GlideShell 本机签名"

if ($TrustOnly) {
    # 提权后的子进程：只做导入
    foreach ($store in "Root", "TrustedPublisher") {
        Import-Certificate -FilePath $CerPath -CertStoreLocation "Cert:\LocalMachine\$store" | Out-Null
    }
    return
}

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -KeyAlgorithm RSA -KeyLength 3072 `
        -HashAlgorithm SHA256 -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(5) -CertStoreLocation Cert:\CurrentUser\My
    "已生成证书 $($cert.Thumbprint)"
} else {
    "复用证书 $($cert.Thumbprint)（有效期到 $($cert.NotAfter.ToString('yyyy-MM-dd'))）"
}

$trusted = Get-ChildItem Cert:\LocalMachine\Root | Where-Object Thumbprint -eq $cert.Thumbprint
if ($trusted) {
    "本机已信任该证书"
    return
}

$cer = Join-Path ([IO.Path]::GetTempPath()) "glideshell-dev-$($cert.Thumbprint).cer"
Export-Certificate -Cert $cert -FilePath $cer | Out-Null
"正在请求管理员权限，把证书（仅公钥）加入本机信任……"
$shell = (Get-Process -Id $PID).Path
$p = Start-Process -FilePath $shell -Verb RunAs -Wait -PassThru -ArgumentList @(
    "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"", "-TrustOnly", "-CerPath", "`"$cer`"")
Remove-Item $cer -ErrorAction SilentlyContinue
if ($p.ExitCode -ne 0 -or -not (Get-ChildItem Cert:\LocalMachine\Root | Where-Object Thumbprint -eq $cert.Thumbprint)) {
    throw "没有把证书加入本机信任（UAC 被取消或导入失败）"
}
"本机已信任该证书"
