[CmdletBinding()]
param(
    [switch]$ConfirmRisk
)

$ErrorActionPreference = "Stop"
if (-not $ConfirmRisk) {
    throw "Re-run with -ConfirmRisk after reading Windows/Driver/README.md. This installs development-only kernel drivers."
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated PowerShell window."
}

$bootState = (& bcdedit /enum "{current}" | Out-String)
if ($bootState -notmatch '(?im)^testsigning\s+(yes|on|true)\s*$') {
    throw "Windows is not running in test-signing mode. Run enable-driver-test-mode.ps1 and reboot first."
}

$packageRoot = Join-Path $PSScriptRoot "artifacts\driver-test"
$certificatePath = Join-Path $packageRoot "RemoteMic-Driver-Test.cer"
$virtualInf = Join-Path $packageRoot "VirtualHid\RemoteMic.VirtualHid.inf"
$filterInf = Join-Path $packageRoot "HidFilter\RemoteMic.HidFilter.inf"
foreach ($path in @($certificatePath, $virtualInf, $filterInf)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Missing test-driver artifact: $path"
    }
}

Import-Certificate -FilePath $certificatePath -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
Import-Certificate -FilePath $certificatePath -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null

$devcon = "C:\Program Files (x86)\Windows Kits\10\Tools\10.0.26100.0\x64\devcon.exe"
if (-not (Test-Path -LiteralPath $devcon)) {
    throw "DevCon was not found at $devcon"
}

& $devcon install $virtualInf 'Root\RemoteMicVirtualHid'
if ($LASTEXITCODE -ne 0) {
    throw "RemoteMic Virtual HID installation failed."
}

& pnputil /add-driver $filterInf /install
if ($LASTEXITCODE -ne 0) {
    throw "RemoteMic Chromecast HID Filter installation failed."
}

Write-Warning "Test drivers were installed. Reboot once more before hardware verification."
