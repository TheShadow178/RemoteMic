[CmdletBinding()]
param(
    [switch]$ConfirmRisk
)

$ErrorActionPreference = "Stop"
if (-not $ConfirmRisk) {
    throw "Re-run with -ConfirmRisk after reading Windows/Driver/README.md. This changes Windows boot security and requires a reboot."
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated PowerShell window."
}

try {
    if (Confirm-SecureBootUEFI) {
        throw "Secure Boot is enabled. Do not disable it automatically. Test-signing cannot be enabled until the tester explicitly changes firmware policy."
    }
}
catch [System.PlatformNotSupportedException] {
    # Legacy BIOS has no Secure Boot state.
}

& bcdedit /set testsigning on
if ($LASTEXITCODE -ne 0) {
    throw "BCDEdit could not enable test-signing."
}

Write-Warning "Windows test-signing was enabled. Reboot before installing the RemoteMic test drivers."
