# Proof that the Authenticode signature actually protects the binaries.
#
# A self-signed certificate is reported as an untrusted root, and Windows returns that
# trust verdict *before* it compares hashes -- so "HashMismatch" only becomes observable
# once the certificate is trusted. This installs the public certificate into the current
# user's root store for the length of the probe and removes it again afterwards; nothing
# is left changed on the machine.
param(
    [Parameter(Mandatory = $true)][string]$Target,
    [string]$KeyDir = ""
)

$ErrorActionPreference = "Stop"
$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $KeyDir) { $KeyDir = Join-Path $root "keys" }
$cerFile = Join-Path $KeyDir "astrbot-signing.cer"
if (-not (Test-Path $cerFile)) {
    Write-Output "NOTRUN  no public certificate at $cerFile"
    exit 2
}

$thumb = (Get-Content (Join-Path $KeyDir "signing-thumbprint.txt") -Raw).Trim()
$already = Get-ChildItem "Cert:\CurrentUser\Root\$thumb" -ErrorAction SilentlyContinue
$installed = $null
if (-not $already) {
    try {
        $installed = Import-Certificate -FilePath $cerFile -CertStoreLocation Cert:\CurrentUser\Root -ErrorAction Stop
    } catch {
        Write-Output "NOTRUN  could not add the certificate to the local trust store: $($_.Exception.Message)"
        exit 2
    }
}

try {
    $clean = Get-AuthenticodeSignature -FilePath $Target
    $bytes = [System.IO.File]::ReadAllBytes($Target)
    $copy = "$Target.tamper"
    $bytes[4096] = $bytes[4096] -bxor 0xFF
    [System.IO.File]::WriteAllBytes($copy, $bytes)
    $dirty = Get-AuthenticodeSignature -FilePath $copy
    Remove-Item $copy -Force

    Write-Output "clean: $($clean.Status)  signed-by: $(if ($clean.SignerCertificate) { $clean.SignerCertificate.Thumbprint.Substring(0,12) } else { '-' })  stamp: $(if ($clean.TimeStamperCertificate) { 'yes' } else { 'no' })"
    Write-Output "tampered (one byte inside the image): $($dirty.Status)"
    if ("$($clean.Status)" -ne "Valid") { Write-Output "FAILED: signed file did not verify as Valid"; exit 1 }
    if ("$($dirty.Status)" -ne "HashMismatch") { Write-Output "FAILED: tampering not detected"; exit 1 }
    Write-Output "OK: signature verifies, and changing one byte breaks it"
} finally {
    if ($installed) {
        Remove-Item "Cert:\CurrentUser\Root\$thumb" -Force -ErrorAction SilentlyContinue
        Write-Output "probe certificate removed from the trust store"
    }
}
