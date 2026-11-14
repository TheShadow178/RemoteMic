[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$CodeAnalysis
)

$ErrorActionPreference = "Stop"
$vswhere = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw "Visual Studio Installer vswhere.exe was not found. Install VS 2022 Build Tools first."
}

$installPath = & $vswhere -latest -products * `
    -requires Component.Microsoft.Windows.DriverKit.BuildTools `
    -property installationPath
if ([string]::IsNullOrWhiteSpace($installPath)) {
    throw "VS 2022 WDK Build Tools are not installed."
}

$msbuild = Join-Path $installPath "MSBuild\Current\Bin\amd64\MSBuild.exe"
if (-not (Test-Path -LiteralPath $msbuild)) {
    throw "64-bit MSBuild was not found at $msbuild"
}

$properties = @(
    "/p:Configuration=$Configuration",
    "/p:Platform=x64",
    "/p:SignMode=Off",
    "/p:Inf2CatUseLocalTime=true"
)
if ($CodeAnalysis) {
    $properties += "/p:RunCodeAnalysis=true"
}

$projects = @(
    (Join-Path $PSScriptRoot "Driver\RemoteMic.VirtualHid\RemoteMic.VirtualHid.vcxproj"),
    (Join-Path $PSScriptRoot "Driver\RemoteMic.HidFilter\RemoteMic.HidFilter.vcxproj")
)

foreach ($project in $projects) {
    & $msbuild $project /t:Rebuild @properties /m /v:minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Driver build failed: $project"
    }
}
