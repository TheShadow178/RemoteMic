[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
& (Join-Path $PSScriptRoot "build-drivers.ps1") -Configuration Release -CodeAnalysis

$output = Join-Path $PSScriptRoot "artifacts\driver-test"
New-Item -ItemType Directory -Force -Path $output | Out-Null

$packages = @(
    @{
        Name = "VirtualHid"
        Source = Join-Path $PSScriptRoot "Driver\RemoteMic.VirtualHid\x64\Release\RemoteMic.VirtualHid"
    },
    @{
        Name = "HidFilter"
        Source = Join-Path $PSScriptRoot "Driver\RemoteMic.HidFilter\x64\Release\RemoteMic.HidFilter"
    }
)

foreach ($package in $packages) {
    $target = Join-Path $output $package.Name
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item -Path (Join-Path $package.Source "*.inf") -Destination $target -Force
    Copy-Item -Path (Join-Path $package.Source "*.sys") -Destination $target -Force
    Copy-Item -Path (Join-Path $package.Source "*.cat") -Destination $target -Force
}

Write-Output "Unsigned test-driver package: $output"
