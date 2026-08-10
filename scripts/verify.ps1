[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repositoryRoot 'Nfg.Store.slnx'
$smokeProject = Join-Path $repositoryRoot 'tests\Nfg.Store.Core.Smoke\Nfg.Store.Core.Smoke.csproj'
$globalJsonPath = Join-Path $repositoryRoot 'global.json'

$sdkConfiguration = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
$expectedSdkVersion = $sdkConfiguration.sdk.version
$actualSdkVersion = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not determine the active .NET SDK.' }
if ($actualSdkVersion -cne $expectedSdkVersion) {
    throw "Expected .NET SDK $expectedSdkVersion, but dotnet selected $actualSdkVersion."
}

Push-Location $repositoryRoot
try {
    & (Join-Path $PSScriptRoot 'catalog-snapshot.ps1') -Check
    if ($LASTEXITCODE -ne 0) { throw 'Legacy bundled catalog snapshot verification failed.' }

    & (Join-Path $PSScriptRoot 'catalog-snapshot.ps1') -Check -Feed V2
    if ($LASTEXITCODE -ne 0) { throw 'V2 bundled catalog snapshot verification failed.' }

    dotnet restore $solutionPath
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

    dotnet format $solutionPath --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet format verification failed.' }

    dotnet build $solutionPath `
        --configuration $Configuration `
        --no-restore `
        -p:ContinuousIntegrationBuild=true
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }

    dotnet run `
        --project $smokeProject `
        --configuration $Configuration `
        --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Smoke checks failed.' }
}
finally {
    Pop-Location
}
