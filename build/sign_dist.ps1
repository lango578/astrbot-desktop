# Authenticode-signs the shipped binaries and proves the signature actually protects them.
#
#   powershell -File build\sign_dist.ps1 -App dist-new\AstrBot
#
# Signs only what this build produces: the launcher and the frozen core. Vendored
# third-party binaries (ffmpeg.exe, runtime\python.exe and its DLLs) are left as-is --
# putting our name on someone else's binary claims authorship we do not have, and their
# upstream bytes are already pinned by the packaging step.
param(
    [string]$App = "",
    [string]$KeyDir = "",
    [string]$TimestampServer = "http://timestamp.digicert.com",
    [switch]$NoTimestamp,
    # 安装程序是我们自己 ISCC 编出来的二进制，用户真正双击的就是它，所以也在签名范围内：
    #   -ExtraFile installer\AstrBot-Setup-4.29.3.exe
    [string[]]$ExtraFile = @(),
    # 已经签过一遍成品、只想补签安装包时用这个（否则每次都会重新向 TSA 签一遍那两个 exe，
    # 字节会变，刚编好的安装包里带的就是上一份签名）
    [switch]$SkipApp
)

$ErrorActionPreference = "Stop"
$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$root = Split-Path -Parent $PSScriptRoot
if (-not $KeyDir) { $KeyDir = Join-Path $root "keys" }

$thumbFile = Join-Path $KeyDir "signing-thumbprint.txt"
if (-not (Test-Path $thumbFile)) {
    Write-Output "FAILED: no signing certificate recorded; run build\make_signing_cert.ps1 first"
    exit 1
}
$thumb = (Get-Content $thumbFile -Raw).Trim()
$cert = Get-ChildItem "Cert:\CurrentUser\My\$thumb" -ErrorAction SilentlyContinue
if (-not $cert) {
    Write-Output "FAILED: certificate $thumb missing from CurrentUser\My"
    exit 1
}

$targets = @()
if (-not $SkipApp) {
    if (-not $App) { $App = Join-Path $root "dist-new\AstrBot" }
    $targets += (Join-Path $App "AstrBot.exe")
    $targets += (Join-Path $App "core\AstrBotCore.exe")
}
foreach ($extra in $ExtraFile) { $targets += (Get-Item $extra).FullName }
if (-not $targets) { Write-Output "FAILED: 没有要签的目标（-SkipApp 和空的 -ExtraFile）"; exit 1 }

function Signed-Status([string]$path) {
    $sig = Get-AuthenticodeSignature -FilePath $path
    [pscustomobject]@{
        Path     = $path
        Status   = [string]$sig.Status
        Signer   = if ($sig.SignerCertificate) { $sig.SignerCertificate.Thumbprint.Substring(0, 12) } else { "-" }
        Stamp    = if ($sig.TimeStamperCertificate) { $sig.TimeStamperCertificate.Subject.Split(',')[0] } else { "-" }
        Message  = ($sig.StatusMessage -replace "`r|`n", " ")
    }
}

foreach ($path in $targets) {
    if (-not (Test-Path $path)) {
        Write-Output "FAILED: target missing: $path"
        exit 1
    }
    $signArgs = @{ FilePath = $path; Certificate = $cert }
    if (-not $NoTimestamp -and $TimestampServer) { $signArgs.TimestampServer = $TimestampServer }

    # 时间戳必须验，不能只当「附加信息」看：实测过一次 TSA 那次请求没成，
    # 结果 Set-AuthenticodeSignature 照样返回签名成功、文件也带了我的名字，
    # 但时间戳是空的——证书一过期（或将来被撤）这个签名就整体作废，而当时没有任何一步报错。
    $stamped = $false
    for ($try = 1; $try -le 3; $try++) {
        $res = Set-AuthenticodeSignature @signArgs
        $status = Signed-Status $path
        if ($NoTimestamp -or -not $TimestampServer) { $stamped = $true; $status | Format-List | Out-String | Write-Output; break }
        if ($res.Status -eq 'Valid' -or $res.Status -eq 'UnknownError') {
            if ($status.Stamp -ne '-') { $stamped = $true; $status | Format-List | Out-String | Write-Output; break }
        }
        Write-Output "[retry $try/3] $path 没有时间戳（TSA 没给回计数器签名），重签"
        Start-Sleep -Seconds (3 * $try)
    }
    if (-not $stamped) {
        Write-Output "FAILED: $path 签了三次都没有时间戳。没有时间的 Authenticode 在证书过期/撤销后整体作废，不能当成品发。"
        exit 1
    }
}

# 篡改检测不在这里做：自签证书未被信任时，Windows 先报「根不受信」而不是哈希不符，
# 只有 build\signature_proof.ps1（临时把公钥装进当前用户信任库再撤掉）才能真验出 HashMismatch。
Write-Output "certificate: $($cert.Thumbprint)  expires $($cert.NotAfter.ToString('yyyy-MM-dd'))"
