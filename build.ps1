<#
.SYNOPSIS
    Builds and runs sc-outfitter against the user-local .NET SDK.

.DESCRIPTION
    The SDK lives in %USERPROFILE%\.dotnet (installed without admin rights). The machine
    wide muxer in C:\Program Files\dotnet comes first on PATH and only looks for SDKs next
    to itself, so a plain "dotnet build" reports "No .NET SDKs were found". This script
    puts the local SDK in front for the current session.

.EXAMPLE
    .\build.ps1                 # build everything (Debug)
    .\build.ps1 -Release        # build everything (Release)
    .\build.ps1 -Test           # build, then run the test suite
    .\build.ps1 -Run            # build, then start the app
    .\build.ps1 -Publish        # self-contained single exe in publish\
#>
[CmdletBinding()]
param(
    [switch]$Release,
    [switch]$Run,
    [switch]$Test,
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'

$localSdk = Join-Path $env:USERPROFILE '.dotnet'
if (-not (Test-Path (Join-Path $localSdk 'sdk'))) {
    Write-Error @"
Kein .NET SDK unter $localSdk gefunden. Installieren mit:

  & ([scriptblock]::Create((Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -UseBasicParsing).Content)) -Channel 8.0 -InstallDir "`$env:USERPROFILE\.dotnet" -NoPath
"@
}

$env:DOTNET_ROOT = $localSdk
$env:PATH = "$localSdk;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$configuration = if ($Release -or $Publish) { 'Release' } else { 'Debug' }
$solution = Join-Path $PSScriptRoot 'ScOutfitter.sln'

if ($Publish) {
    $project = Join-Path $PSScriptRoot 'src\ScOutfitter.App\ScOutfitter.App.csproj'
    dotnet publish $project -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -o (Join-Path $PSScriptRoot 'publish') -v minimal --nologo
    exit $LASTEXITCODE
}

dotnet build $solution -c $configuration -v minimal --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($Test) {
    $tests = Join-Path $PSScriptRoot "tests\ScOutfitter.Tests\bin\$configuration\net8.0-windows\sc-outfitter-tests.exe"
    & $tests
    exit $LASTEXITCODE
}

if ($Run) {
    $exe = Join-Path $PSScriptRoot "src\ScOutfitter.App\bin\$configuration\net8.0-windows\sc-outfitter.exe"
    & $exe
    exit $LASTEXITCODE
}
