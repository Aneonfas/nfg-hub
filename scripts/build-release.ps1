[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [ValidateSet('win-x64')]
    [string]$RuntimeIdentifier = 'win-x64',

    [string]$OutputRoot,

    [switch]$SkipVerification,

    [switch]$AllowDirty,

    [switch]$TestStartup,

    [switch]$RemovePublishDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'src\Nfg.Store.App\Nfg.Store.App.csproj'
. (Join-Path $PSScriptRoot 'release-version.ps1')
$Version = ConvertTo-NfgReleaseVersion -Version $Version

if (-not $AllowDirty) {
    $workingTreeChanges = @(git -C $repositoryRoot status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the Git working tree.' }
    if ($workingTreeChanges.Count -ne 0) {
        throw 'Release packages must be built from a clean Git working tree.'
    }
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot 'artifacts\release'
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)

[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$versionNode = $project.SelectSingleNode('/Project/PropertyGroup/Version')
$projectVersion = if ($null -eq $versionNode) { '<missing>' } else { $versionNode.InnerText }
if ($projectVersion -ne $Version) {
    throw "Requested release version '$Version' does not match the Hub project version '$projectVersion'."
}

$publishDirectory = Join-Path $OutputRoot "NFG-Hub-v$Version-$RuntimeIdentifier"
$archivePath = "$publishDirectory.zip"
$checksumPath = "$archivePath.sha256"
foreach ($path in @($publishDirectory, $archivePath, $checksumPath)) {
    if (Test-Path -LiteralPath $path) {
        throw "Release output already exists: $path"
    }
}

if (-not $SkipVerification) {
    & (Join-Path $PSScriptRoot 'verify.ps1') -Configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Release verification failed.' }
}

New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$stagingRoot = Join-Path $OutputRoot ".nfg-hub-release-$([Guid]::NewGuid().ToString('N'))"
$stagedPublishDirectory = Join-Path $stagingRoot 'publish'
$stagedArchivePath = Join-Path $stagingRoot (Split-Path -Leaf $archivePath)
$stagedChecksumPath = Join-Path $stagingRoot (Split-Path -Leaf $checksumPath)
$promotedPaths = [System.Collections.Generic.List[string]]::new()
$archiveHash = $null

try {
    New-Item -ItemType Directory -Path $stagingRoot | Out-Null

    Push-Location $repositoryRoot
    try {
        dotnet restore $projectPath --runtime $RuntimeIdentifier
        if ($LASTEXITCODE -ne 0) { throw 'Runtime-specific restore failed.' }

        dotnet publish $projectPath `
            --configuration Release `
            --framework net10.0-windows `
            --runtime $RuntimeIdentifier `
            --self-contained true `
            --no-restore `
            -p:Version=$Version `
            -p:ContinuousIntegrationBuild=true `
            -p:DebugType=None `
            -p:DebugSymbols=false `
            -p:PublishTrimmed=false `
            -p:PublishReadyToRun=false `
            --output $stagedPublishDirectory
        if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
    }
    finally {
        Pop-Location
    }

    $executablePath = Join-Path $stagedPublishDirectory 'NFG.Hub.exe'
    $catalogRoot = Join-Path $stagedPublishDirectory 'catalog'
    $catalogIndexPath = Join-Path $catalogRoot 'catalog.json'
    foreach ($requiredPath in @(
        $executablePath,
        $catalogIndexPath,
        (Join-Path $stagedPublishDirectory 'coreclr.dll'),
        (Join-Path $stagedPublishDirectory 'hostfxr.dll'),
        (Join-Path $stagedPublishDirectory 'NFG.Hub.runtimeconfig.json'))) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Published package is incomplete: $requiredPath"
        }
    }

    $executableStream = [System.IO.File]::OpenRead($executablePath)
    try {
        $reader = [System.IO.BinaryReader]::new($executableStream)
        try {
            $executableStream.Position = 0x3c
            $peOffset = $reader.ReadInt32()
            $executableStream.Position = $peOffset
            if ($reader.ReadUInt32() -ne 0x00004550) {
                throw 'Published executable does not contain a valid PE signature.'
            }
            if ($reader.ReadUInt16() -ne 0x8664) {
                throw 'Published executable is not Windows x64.'
            }
        }
        finally { $reader.Dispose() }
    }
    finally { $executableStream.Dispose() }

    $catalogIndex = Get-Content -LiteralPath $catalogIndexPath -Raw | ConvertFrom-Json
    $catalogProductRelativePaths = @($catalogIndex.products)
    if ($catalogProductRelativePaths.Count -eq 0) {
        throw 'Bundled catalog does not reference any product manifests.'
    }

    $catalogRootFullPath = [System.IO.Path]::GetFullPath($catalogRoot)
    $catalogRootPrefix = $catalogRootFullPath.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    foreach ($relativeProductPath in $catalogProductRelativePaths) {
        if ($relativeProductPath -isnot [string] -or [string]::IsNullOrWhiteSpace($relativeProductPath)) {
            throw 'Bundled catalog contains an invalid product manifest path.'
        }

        $productPath = [System.IO.Path]::GetFullPath(
            (Join-Path $catalogRootFullPath $relativeProductPath))
        if (-not $productPath.StartsWith($catalogRootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Catalog manifest path escapes the catalog directory: $relativeProductPath"
        }
        if (-not (Test-Path -LiteralPath $productPath -PathType Leaf)) {
            throw "Catalog manifest is missing from the package: $relativeProductPath"
        }

        try { $null = Get-Content -LiteralPath $productPath -Raw | ConvertFrom-Json }
        catch { throw "Catalog manifest is not valid JSON: $relativeProductPath" }
    }

    foreach ($jsonFile in Get-ChildItem -LiteralPath $catalogRoot -Recurse -Filter '*.json' -File) {
        try { $null = Get-Content -LiteralPath $jsonFile.FullName -Raw | ConvertFrom-Json }
        catch { throw "Bundled catalog file is not valid JSON: $($jsonFile.FullName)" }
    }

    $commitEpochText = (git -C $repositoryRoot show -s --format=%ct HEAD).Trim()
    $commitEpoch = 0L
    if ($LASTEXITCODE -ne 0 -or -not [long]::TryParse($commitEpochText, [ref]$commitEpoch)) {
        throw 'Could not determine the source commit timestamp.'
    }
    $archiveTimestamp = [DateTimeOffset]::FromUnixTimeSeconds($commitEpoch)
    $minimumZipTimestamp = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
    if ($archiveTimestamp -lt $minimumZipTimestamp) {
        $archiveTimestamp = $minimumZipTimestamp
    }

    $archiveStream = [System.IO.File]::Open(
        $stagedArchivePath,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new(
            $archiveStream,
            [System.IO.Compression.ZipArchiveMode]::Create,
            $false)
        try {
            $publishedFiles = Get-ChildItem -LiteralPath $stagedPublishDirectory -Recurse -File |
                Sort-Object { [System.IO.Path]::GetRelativePath($stagedPublishDirectory, $_.FullName) }

            foreach ($file in $publishedFiles) {
                $relativePath = [System.IO.Path]::GetRelativePath(
                    $stagedPublishDirectory,
                    $file.FullName).Replace('\', '/')
                $entry = $archive.CreateEntry(
                    $relativePath,
                    [System.IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $archiveTimestamp

                $source = [System.IO.File]::OpenRead($file.FullName)
                try {
                    $destination = $entry.Open()
                    try { $source.CopyTo($destination) }
                    finally { $destination.Dispose() }
                }
                finally { $source.Dispose() }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $archiveStream.Dispose() }

    $archiveReadStream = [System.IO.File]::OpenRead($stagedArchivePath)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new(
            $archiveReadStream,
            [System.IO.Compression.ZipArchiveMode]::Read,
            $false)
        try {
            $entryNames = [System.Collections.Generic.HashSet[string]]::new(
                [System.StringComparer]::Ordinal)
            foreach ($entry in $archive.Entries) {
                $entryName = $entry.FullName
                $segments = $entryName.Split('/', [System.StringSplitOptions]::RemoveEmptyEntries)
                if ([string]::IsNullOrWhiteSpace($entryName) -or
                    $entryName.Contains('\') -or
                    $entryName.StartsWith('/', [System.StringComparison]::Ordinal) -or
                    $segments -contains '..' -or
                    -not $entryNames.Add($entryName)) {
                    throw "Release archive contains an unsafe or duplicate entry: $entryName"
                }
            }

            $requiredEntries = @('NFG.Hub.exe', 'catalog/catalog.json') + @(
                $catalogProductRelativePaths | ForEach-Object { "catalog/$($_.Replace('\', '/'))" })
            foreach ($requiredEntry in $requiredEntries) {
                if (-not $entryNames.Contains($requiredEntry)) {
                    throw "Release archive is missing required entry: $requiredEntry"
                }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $archiveReadStream.Dispose() }

    $extractedDirectory = Join-Path $stagingRoot 'extracted'
    [System.IO.Compression.ZipFile]::ExtractToDirectory(
        $stagedArchivePath,
        $extractedDirectory)

    if ($TestStartup) {
        $extractedExecutablePath = Join-Path $extractedDirectory 'NFG.Hub.exe'
        $startupErrorPath = Join-Path $stagingRoot 'startup-smoke.stderr.txt'
        $startupProcess = Start-Process `
            -FilePath $extractedExecutablePath `
            -ArgumentList '--startup-smoke' `
            -WorkingDirectory $extractedDirectory `
            -RedirectStandardError $startupErrorPath `
            -PassThru
        $startupSmokeRoot = Join-Path `
            ([System.IO.Path]::GetTempPath()) `
            "NFG-Hub-startup-smoke-$($startupProcess.Id)"
        try {
            $startupCompleted = $startupProcess.WaitForExit(30000)
            $startupExitCode = if ($startupCompleted) { $startupProcess.ExitCode } else { $null }
        }
        finally {
            if (-not $startupProcess.HasExited) {
                $startupProcess.Kill($true)
                $startupProcess.WaitForExit()
            }
            $startupProcess.Dispose()

            if (Test-Path -LiteralPath $startupSmokeRoot) {
                $tempRootPrefix = [System.IO.Path]::GetFullPath(
                    [System.IO.Path]::GetTempPath()).TrimEnd(
                        [System.IO.Path]::DirectorySeparatorChar,
                        [System.IO.Path]::AltDirectorySeparatorChar) +
                    [System.IO.Path]::DirectorySeparatorChar
                $startupSmokeRootFullPath = [System.IO.Path]::GetFullPath($startupSmokeRoot)
                if (-not $startupSmokeRootFullPath.StartsWith(
                    $tempRootPrefix,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Refusing to clean a startup-smoke directory outside the temporary root.'
                }
                Remove-Item -LiteralPath $startupSmokeRootFullPath -Recurse -Force
            }
        }

        if (-not $startupCompleted) {
            throw 'Published NFG Hub did not complete its startup smoke check within 30 seconds.'
        }
        if ($startupExitCode -ne 0) {
            $startupError = if (Test-Path -LiteralPath $startupErrorPath) {
                (Get-Content -LiteralPath $startupErrorPath -Raw).Trim()
            }
            else {
                '<no error output>'
            }
            throw "Published NFG Hub failed its startup smoke check with exit code $startupExitCode. $startupError"
        }
    }

    $archiveHash = (Get-FileHash -LiteralPath $stagedArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $archiveName = Split-Path -Leaf $archivePath
    [System.IO.File]::WriteAllText(
        $stagedChecksumPath,
        "$archiveHash  $archiveName`n",
        [System.Text.UTF8Encoding]::new($false))

    if (-not $RemovePublishDirectory) {
        Move-Item -LiteralPath $stagedPublishDirectory -Destination $publishDirectory
        $promotedPaths.Add($publishDirectory)
    }
    Move-Item -LiteralPath $stagedArchivePath -Destination $archivePath
    $promotedPaths.Add($archivePath)
    Move-Item -LiteralPath $stagedChecksumPath -Destination $checksumPath
    $promotedPaths.Add($checksumPath)
}
catch {
    foreach ($promotedPath in $promotedPaths) {
        if (Test-Path -LiteralPath $promotedPath) {
            Remove-Item -LiteralPath $promotedPath -Recurse -Force
        }
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}

[pscustomobject]@{
    Version = $Version
    RuntimeIdentifier = $RuntimeIdentifier
    PublishDirectory = $publishDirectory
    ArchivePath = $archivePath
    ChecksumPath = $checksumPath
    Sha256 = $archiveHash
    PublishDirectoryRemoved = $RemovePublishDirectory.IsPresent
}
