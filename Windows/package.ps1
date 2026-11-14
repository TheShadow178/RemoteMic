param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$workspaceDotnet = Join-Path $repositoryRoot ".dotnet\dotnet.exe"
$dotnet = if (Test-Path -LiteralPath $workspaceDotnet) { $workspaceDotnet } else { "dotnet" }
$project = Join-Path $PSScriptRoot "RemoteMic.Windows\RemoteMic.Windows.csproj"
$artifacts = Join-Path $PSScriptRoot "artifacts"
$publishDirectory = Join-Path $artifacts "publish-$Runtime"
$buildOutput = Join-Path $artifacts "build-$Runtime"
$archive = Join-Path $artifacts "RemoteMic-Windows-$Runtime.zip"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$artifactsFull = [System.IO.Path]::GetFullPath($artifacts).TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar)
$publishFull = [System.IO.Path]::GetFullPath($publishDirectory)
$expectedPrefix = $artifactsFull + [System.IO.Path]::DirectorySeparatorChar
if (-not $publishFull.StartsWith($expectedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean publish directory outside Windows/artifacts: $publishFull"
}
if (Test-Path -LiteralPath $publishFull) {
    Remove-Item -LiteralPath $publishFull -Recurse -Force
}

& $dotnet restore $project `
    --runtime $Runtime `
    --configfile (Join-Path $PSScriptRoot "NuGet.Config") `
    --disable-parallel `
    --verbosity minimal `
    -m:1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet publish $project `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    --no-restore `
    --output $publishDirectory `
    --maxcpucount:1 `
    --verbosity minimal `
    -p:PublishReadyToRun=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:BaseOutputPath="$buildOutput\"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Copy-Item -LiteralPath (Join-Path $repositoryRoot "LICENSE") `
    -Destination (Join-Path $publishDirectory "LICENSE.txt")
Copy-Item -LiteralPath (Join-Path $repositoryRoot "THIRD_PARTY_NOTICES.md") `
    -Destination (Join-Path $publishDirectory "THIRD-PARTY-NOTICES.md")
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "README.md") `
    -Destination (Join-Path $publishDirectory "README-Windows.md")
New-Item -ItemType Directory -Force -Path (Join-Path $publishDirectory "Driver") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $publishDirectory "docs") | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Driver\README.md") `
    -Destination (Join-Path $publishDirectory "Driver\README.md")
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "docs\VERIFY-CHECKLIST.md") `
    -Destination (Join-Path $publishDirectory "docs\VERIFY-CHECKLIST.md")

$dotnetCommand = Get-Command $dotnet -ErrorAction Stop
$dotnetRoot = Split-Path -Parent $dotnetCommand.Source
$dotnetLicense = Join-Path $dotnetRoot "LICENSE.txt"
$dotnetNotices = Join-Path $dotnetRoot "ThirdPartyNotices.txt"
if (-not (Test-Path -LiteralPath $dotnetLicense) -or
    -not (Test-Path -LiteralPath $dotnetNotices)) {
    throw "The selected .NET installation does not expose its license notices: $dotnetRoot"
}
Copy-Item -LiteralPath $dotnetLicense `
    -Destination (Join-Path $publishDirectory "DOTNET-LICENSE.txt")
Copy-Item -LiteralPath $dotnetNotices `
    -Destination (Join-Path $publishDirectory "DOTNET-THIRD-PARTY-NOTICES.txt")

if (Test-Path -LiteralPath $archive) {
    Remove-Item -LiteralPath $archive -Force
}
Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $archive -CompressionLevel Optimal
Write-Host "Windows package: $archive"
