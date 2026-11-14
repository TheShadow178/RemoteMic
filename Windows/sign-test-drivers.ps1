[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$packageRoot = Join-Path $PSScriptRoot "artifacts\driver-test"
if (-not (Test-Path -LiteralPath $packageRoot)) {
    & (Join-Path $PSScriptRoot "package-test-drivers.ps1")
}

$certificate = Get-ChildItem Cert:\CurrentUser\My | Where-Object {
    $_.Subject -eq "CN=RemoteMic Driver Test" -and $_.HasPrivateKey
} | Sort-Object NotAfter -Descending | Select-Object -First 1

if ($null -eq $certificate -or $certificate.NotAfter -lt (Get-Date).AddDays(30)) {
    $certificate = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject "CN=RemoteMic Driver Test" `
        -CertStoreLocation Cert:\CurrentUser\My `
        -HashAlgorithm SHA256 `
        -KeyLength 3072 `
        -NotAfter (Get-Date).AddYears(2)
}

$certificatePath = Join-Path $packageRoot "RemoteMic-Driver-Test.cer"
Export-Certificate -Cert $certificate -FilePath $certificatePath -Force | Out-Null

$signTool = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"
if (-not (Test-Path -LiteralPath $signTool)) {
    throw "SignTool was not found at $signTool"
}

$files = Get-ChildItem $packageRoot -Recurse -File | Where-Object {
    $_.Extension -in ".sys", ".cat"
}
foreach ($file in $files) {
    & $signTool sign /fd SHA256 /sha1 $certificate.Thumbprint /s My $file.FullName
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to sign $($file.FullName)"
    }
    $signature = Get-AuthenticodeSignature -FilePath $file.FullName
    if ($null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
        throw "Expected test signature was not found: $($file.FullName)"
    }
}

Write-Output "Signed test-driver package: $packageRoot"
Write-Output "Public test certificate: $certificatePath"
