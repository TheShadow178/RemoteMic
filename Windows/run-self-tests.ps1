param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$workspaceDotnet = Join-Path $repositoryRoot ".dotnet\dotnet.exe"
$dotnet = if (Test-Path -LiteralPath $workspaceDotnet) { $workspaceDotnet } else { "dotnet" }
$testProject = Join-Path $PSScriptRoot "RemoteMic.Core.Tests\RemoteMic.Core.Tests.csproj"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

& $dotnet restore $testProject `
    --configfile (Join-Path $PSScriptRoot "NuGet.Config") `
    --disable-parallel `
    --verbosity minimal `
    -m:1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet run --project $testProject --configuration $Configuration --no-restore
exit $LASTEXITCODE
