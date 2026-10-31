# Builds OverlayUtils into dist\OverlayUtils and dist\OverlayUtils-<version>.zip
# Compila OverlayUtils in dist\OverlayUtils e dist\OverlayUtils-<versione>.zip
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1
#   ... -NoSign      skip signing (standard mode only; full mode will refuse)
#   ... -NoZip       skip the zip
#
# Output: one self-contained folder (no .NET install needed on the target PC)
# with both launchers side by side:
#   OverlayUtils.exe           standard mode, runs from anywhere
#   OverlayUtils.UIAccess.exe  full mode, signed; started by the app itself
#                              once the user enables full mode
# Not a single-file exe on purpose: single-file extracts native libraries to
# a user-writable temp folder at every start.

param([switch]$NoSign, [switch]$NoZip)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$it = $PSUICulture -like 'it*'
function Say([string]$en, [string]$itText) { if ($it) { Write-Host $itText } else { Write-Host $en } }
function Fail([string]$en, [string]$itText) { Say "ERROR: $en" "ERRORE: $itText"; exit 1 }

$out = Join-Path $root 'dist\OverlayUtils'
$subject = 'CN=OverlayUtils Local Code Signing'

Say '[1/4] Self-tests...' '[1/4] Verifiche...'
dotnet run --project tests\OverlayUtils.SelfTest -c Release
if ($LASTEXITCODE -ne 0) { Fail 'self-tests failed' 'verifiche non superate' }

Say '[2/4] Publishing...' '[2/4] Pubblicazione...'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
foreach ($project in 'src\OverlayUtils', 'src\OverlayUtils.UIAccess') {
    dotnet publish $project -c Release -r win-x64 --self-contained true -o $out
    if ($LASTEXITCODE -ne 0) { Fail "publish of $project failed" "pubblicazione di $project non riuscita" }
}

if ($NoSign) {
    Say '[3/4] Signing skipped (-NoSign): full mode will not be available.' `
        '[3/4] Firma saltata (-NoSign): la modalita'' completa non sara'' disponibile.'
} else {
    Say '[3/4] Signing (Windows asks for the key password)...' '[3/4] Firma (Windows chiede la password della chiave)...'
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
        Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) } |
        Select-Object -First 1
    if (-not $cert) { Fail 'no certificate: run scripts\certificate.ps1 first' 'nessun certificato: lancia prima scripts\certificate.ps1' }
    foreach ($exe in 'OverlayUtils.UIAccess.exe', 'OverlayUtils.exe') {
        $path = Join-Path $out $exe
        # The timestamp keeps the signature valid after the certificate expires.
        try {
            Set-AuthenticodeSignature -FilePath $path -Certificate $cert -HashAlgorithm SHA256 `
                -TimestampServer 'http://timestamp.digicert.com' | Out-Null
        } catch { }
        $s = Get-AuthenticodeSignature $path
        if (-not $s.TimeStamperCertificate) {
            Say "      $exe - timestamp server not reachable, signing without it" `
                "      $exe - server del timestamp non raggiungibile, firmo senza"
            Set-AuthenticodeSignature -FilePath $path -Certificate $cert -HashAlgorithm SHA256 | Out-Null
            $s = Get-AuthenticodeSignature $path
        }
        # Status is "UnknownError" (untrusted root) until a PC trusts the self-signed
        # certificate; what matters here is that the file is signed by our key.
        if ($s.SignerCertificate.Thumbprint -ne $cert.Thumbprint) {
            Fail "signing $exe : $($s.StatusMessage)" "firma di $exe : $($s.StatusMessage)"
        }
        Say "      $exe signed" "      $exe firmato"
    }
}

Copy-Item README.txt $out

if (-not $NoZip) {
    Say '[4/4] Zip...' '[4/4] Zip...'
    [xml]$props = Get-Content Directory.Build.props
    $version = $props.Project.PropertyGroup.Version
    $zip = Join-Path $root "dist\OverlayUtils-$version.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path $out -DestinationPath $zip
    Say "Done: $zip" "Fatto: $zip"
} else {
    Say "Done: $out" "Fatto: $out"
}
