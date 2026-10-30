# Builds (or reuses) the self-signed code-signing certificate for the desktop app.
#
# What this certificate is: an integrity attestation from this build machine. It is
# self-signed, so no authority vouches for the name -- SmartScreen and other machines
# will still warn. What it does buy you: any byte changed after the build shows up as
# HashMismatch, and the embedded timestamp keeps the signature meaningful past expiry.
# To actually remove the download warning, swap in a purchased OV/EV certificate or
# Azure Trusted Signing (README section 十一).
#
# The private key is never written to the repo: it stays in the CurrentUser\My store on
# this box. Moving the identity to another build machine is a manual
#   Export-PfxCertificate -Cert <thumbprint> -FilePath ... -Password ...
# which is deliberately not automated here, so no key material lands next to the source.
param(
    [string]$KeyDir = "",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

if (-not $KeyDir) {
    $KeyDir = Join-Path (Split-Path -Parent $PSScriptRoot) "keys"
}
New-Item -ItemType Directory -Force -Path $KeyDir | Out-Null

$subject = "CN=AstrBot Desktop Build (self-signed, no authority)"
$thumbFile = Join-Path $KeyDir "signing-thumbprint.txt"
$cerFile = Join-Path $KeyDir "astrbot-signing.cer"

# One stamp for the whole build history: re-signing later with a fresh certificate would
# make already-shipped packages unverifiable against the .cer that travels with them.
$thumb = $null
if (Test-Path $thumbFile) {
    $candidate = (Get-Content $thumbFile -Raw).Trim()
    $found = Get-ChildItem "Cert:\CurrentUser\My\$candidate" -ErrorAction SilentlyContinue
    if ($found -and $found.NotAfter -gt (Get-Date) -and -not $Force) {
        $thumb = $candidate
        Write-Output "reuse cert $thumb"
    }
}

if (-not $thumb) {
    $cert = New-SelfSignedCertificate `
        -Subject $subject `
        -Type CodeSigningCert `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyLength 3072 `
        -HashAlgorithm SHA256 `
        -KeyExportPolicy Exportable `
        -NotAfter (Get-Date).AddYears(3)
    $thumb = $cert.Thumbprint
    Set-Content -Path $thumbFile -Value $thumb -NoNewline -Encoding ascii
    Write-Output "created cert $thumb (expires $($cert.NotAfter.ToString('yyyy-MM-dd')))"
}

$cert = Get-ChildItem "Cert:\CurrentUser\My\$thumb"
if (-not $cert) {
    Write-Output "FAILED: certificate $thumb is not in CurrentUser\My"
    exit 1
}

Export-Certificate -Cert $cert -FilePath $cerFile | Out-Null
Write-Output "public certificate: $cerFile"
Write-Output "thumbprint: $thumb"
