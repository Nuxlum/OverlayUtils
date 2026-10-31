# Creates (once) the self-signed code-signing certificate that build.ps1 uses
# to sign OverlayUtils.UIAccess.exe. No admin rights needed.
#
# Crea (una volta sola) il certificato autofirmato con cui build.ps1 firma
# OverlayUtils.UIAccess.exe. Non servono i permessi di amministratore.
#
# Why: Windows starts an exe with uiAccess="true" only if it is signed with a
# trusted certificate and installed in Program Files. This script only makes
# the key; making it trusted on a PC is done by OverlayUtils itself, when the
# user turns on "Overlay over fullscreen games" (and undone when turned off).
#
# The private key stays in your CurrentUser\My store, is not exportable and is
# protected by a password (ProtectHigh) that Windows asks for at every signing.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\certificate.ps1
#   ... -Renew   deletes the existing certificate and creates a new one
#                (anyone using full mode must re-enable it with the new build)

param([switch]$Renew)

$ErrorActionPreference = 'Stop'
$subject = 'CN=OverlayUtils Local Code Signing'
$it = $PSUICulture -like 'it*'
function Say([string]$en, [string]$itText) { if ($it) { Write-Output $itText } else { Write-Output $en } }

if ($Renew) {
    Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject } | ForEach-Object {
        Remove-Item -Path $_.PSPath -DeleteKey
        Say "Removed $($_.Thumbprint)" "Rimosso $($_.Thumbprint)"
    }
}

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) } |
    Select-Object -First 1

if ($cert) {
    Say "Certificate already present: $($cert.Thumbprint) (use -Renew to replace it)" `
        "Certificato gia' presente: $($cert.Thumbprint) (per sostituirlo: -Renew)"
    exit 0
}

Say 'Windows will now ask you to choose a password for the key: you will need it at every build.' `
    "Windows ora chiede di scegliere una password per la chiave: servira' a ogni build."
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
    -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy NonExportable `
    -KeyProtection ProtectHigh `
    -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
    -NotAfter (Get-Date).AddYears(5)
Say "Certificate created: $($cert.Thumbprint)" "Certificato creato: $($cert.Thumbprint)"
