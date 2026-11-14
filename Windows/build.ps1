param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$workspaceDotnet = Join-Path $repositoryRoot ".dotnet\dotnet.exe"
$dotnet = if (Test-Path -LiteralPath $workspaceDotnet) { $workspaceDotnet } else { "dotnet" }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

& $dotnet restore (Join-Path $PSScriptRoot "RemoteMic.Windows.sln") `
    --configfile (Join-Path $PSScriptRoot "NuGet.Config") `
    --disable-parallel `
    --verbosity minimal `
    -m:1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet build (Join-Path $PSScriptRoot "RemoteMic.Windows.sln") `
    --configuration $Configuration `
    --no-restore `
    --maxcpucount:1 `
    --verbosity minimal
exit $LASTEXITCODE
