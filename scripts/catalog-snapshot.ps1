[CmdletBinding(DefaultParameterSetName = 'Check')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Sync')]
    [switch]$Sync,

    [Parameter(Mandatory, ParameterSetName = 'Check')]
    [switch]$Check,

    [Parameter(ParameterSetName = 'Sync')]
    [Parameter(ParameterSetName = 'Check')]
    [string]$AuthoritativeRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$catalogRoot = Join-Path $repositoryRoot 'catalog'
$snapshotManifestPath = Join-Path $repositoryRoot 'catalog.snapshot.json'
$smokeProject = Join-Path $repositoryRoot 'tests\Nfg.Store.Core.Smoke\Nfg.Store.Core.Smoke.csproj'
$sourceRepository = 'https://github.com/Aneonfas/nfg-hub-catalog'
$utf8WithoutBom = [System.Text.UTF8Encoding]::new($false)

function Invoke-Git {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $output = @(& git -C $Root @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        $details = ($output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
        throw "Git command failed in '$Root': git $($Arguments -join ' ')`n$details"
    }

    return @($output | ForEach-Object { $_.ToString() })
}

function Assert-GitRepositoryRoot {
    param([Parameter(Mandatory)][string]$Root)

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        throw "Catalog repository does not exist: $Root"
    }

    $resolvedRoot = [System.IO.Path]::GetFullPath($Root)
    $gitRoot = @(Invoke-Git -Root $resolvedRoot -Arguments @('rev-parse', '--show-toplevel'))[0]
    $resolvedGitRoot = [System.IO.Path]::GetFullPath($gitRoot)
    if (-not $resolvedGitRoot.Equals($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "AuthoritativeRoot must be the Git repository root: $resolvedGitRoot"
    }

    $originUrl = @(Invoke-Git -Root $resolvedRoot -Arguments @('remote', 'get-url', 'origin'))[0]
    $normalizedOrigin = $originUrl.Trim().Replace('\', '/').TrimEnd('/') -replace '\.git$', ''
    if ($normalizedOrigin -notmatch '(?i)github\.com[/:]Aneonfas/nfg-hub-catalog$') {
        throw "AuthoritativeRoot origin is not $sourceRepository."
    }

    return $resolvedRoot
}

function Assert-SafeCatalogPath {
    param([Parameter(Mandatory)][string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or
        $Path -cne $Path.Trim() -or
        [System.IO.Path]::IsPathRooted($Path) -or
        $Path.Contains('\', [System.StringComparison]::Ordinal) -or
        $Path.Contains(':', [System.StringComparison]::Ordinal) -or
        $Path -notmatch '^[A-Za-z0-9._/-]+$' -or
        -not $Path.EndsWith('.json', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Catalog path is unsafe: '$Path'."
    }

    $segments = $Path.Split('/', [System.StringSplitOptions]::None)
    if ($segments.Count -eq 0 -or
        @($segments | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0) {
        throw "Catalog path is unsafe: '$Path'."
    }
}

function Get-OrdinalSortedPaths {
    param([Parameter(Mandatory)][object[]]$Paths)

    $sortedPaths = [string[]]@($Paths)
    [System.Array]::Sort($sortedPaths, [System.StringComparer]::Ordinal)
    return $sortedPaths
}

function Get-ManagedCatalogPaths {
    param([Parameter(Mandatory)][string]$IndexPath)

    try {
        $index = Get-Content -LiteralPath $IndexPath -Raw | ConvertFrom-Json
    }
    catch {
        throw "Catalog index is not valid JSON: $IndexPath"
    }

    if ($index.schemaVersion -ne 1 -or
        $null -eq $index.products -or
        $index.products -is [string]) {
        throw 'Catalog index has an unsupported schema or product list.'
    }

    $products = @($index.products)
    if ($products.Count -eq 0) {
        throw 'Catalog index does not reference any product manifests.'
    }

    $paths = [System.Collections.Generic.List[string]]::new()
    $paths.Add('catalog.json')
    $seenPaths = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    $null = $seenPaths.Add('catalog.json')

    foreach ($productPath in $products) {
        if ($productPath -isnot [string]) {
            throw 'Catalog contains a non-string product path.'
        }

        Assert-SafeCatalogPath -Path $productPath
        if (-not $seenPaths.Add($productPath)) {
            throw "Catalog references '$productPath' more than once."
        }

        $paths.Add($productPath)
    }

    return @(Get-OrdinalSortedPaths -Paths $paths)
}

function Export-CatalogCommit {
    param(
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][string]$Commit,
        [Parameter(Mandatory)][string]$StagingRoot
    )

    $indexArchivePath = Join-Path $StagingRoot 'index.zip'
    $indexRoot = Join-Path $StagingRoot 'index'
    $archivePath = Join-Path $StagingRoot 'catalog.zip'
    $exportRoot = Join-Path $StagingRoot 'source'

    $null = Invoke-Git -Root $SourceRoot -Arguments @(
        'archive',
        '--format=zip',
        "--output=$indexArchivePath",
        $Commit,
        '--',
        'catalog.json')
    [System.IO.Compression.ZipFile]::ExtractToDirectory($indexArchivePath, $indexRoot)
    $managedPaths = Get-ManagedCatalogPaths -IndexPath (Join-Path $indexRoot 'catalog.json')

    $archiveArguments = @(
        'archive',
        '--format=zip',
        "--output=$archivePath",
        $Commit,
        '--') + $managedPaths
    $null = Invoke-Git -Root $SourceRoot -Arguments $archiveArguments
    [System.IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $exportRoot)

    $actualPaths = @(Get-OrdinalSortedPaths -Paths @(
            Get-ChildItem -LiteralPath $exportRoot -Recurse -Force -File | ForEach-Object {
                [System.IO.Path]::GetRelativePath($exportRoot, $_.FullName).Replace('\', '/')
            }))
    if ([System.Linq.Enumerable]::SequenceEqual(
            [string[]]$actualPaths,
            [string[]]$managedPaths,
            [System.StringComparer]::Ordinal) -ne $true) {
        throw 'Committed catalog export does not contain exactly the indexed runtime files.'
    }

    return [pscustomobject]@{
        Root = $exportRoot
        Paths = [string[]]$managedPaths
    }
}

function Get-CatalogFileRecords {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string[]]$Paths
    )

    return @($Paths | ForEach-Object {
            $filePath = Join-Path $Root $_
            if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
                throw "Catalog runtime file is missing: $_"
            }

            [pscustomobject][ordered]@{
                path = $_
                sha256 = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        })
}

function Read-SnapshotManifest {
    if (-not (Test-Path -LiteralPath $snapshotManifestPath -PathType Leaf)) {
        throw "Bundled catalog snapshot manifest is missing: $snapshotManifestPath"
    }

    try {
        return Get-Content -LiteralPath $snapshotManifestPath -Raw | ConvertFrom-Json
    }
    catch {
        throw "Bundled catalog snapshot manifest is not valid JSON: $snapshotManifestPath"
    }
}

function Assert-SnapshotManifest {
    param([Parameter(Mandatory)]$Manifest)

    if ($Manifest.schemaVersion -ne 1 -or
        $null -eq $Manifest.source -or
        $Manifest.source.repository -cne $sourceRepository -or
        $Manifest.source.commit -isnot [string] -or
        $Manifest.source.commit -cnotmatch '^[0-9a-f]{40}$' -or
        $null -eq $Manifest.files -or
        $Manifest.files -is [string]) {
        throw 'Bundled catalog snapshot manifest has invalid metadata.'
    }

    $files = @($Manifest.files)
    if ($files.Count -eq 0) {
        throw 'Bundled catalog snapshot manifest does not contain any files.'
    }

    $paths = [System.Collections.Generic.List[string]]::new()
    $seenPaths = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $files) {
        if ($null -eq $file -or
            $file.path -isnot [string] -or
            $file.sha256 -isnot [string] -or
            $file.sha256 -cnotmatch '^[0-9a-f]{64}$') {
            throw 'Bundled catalog snapshot manifest contains an invalid file record.'
        }

        Assert-SafeCatalogPath -Path $file.path
        if (-not $seenPaths.Add($file.path)) {
            throw "Bundled catalog snapshot manifest contains duplicate path '$($file.path)'."
        }

        $paths.Add($file.path)
    }

    $sortedPaths = @(Get-OrdinalSortedPaths -Paths $paths)
    if ([System.Linq.Enumerable]::SequenceEqual(
            [string[]]$paths,
            [string[]]$sortedPaths,
            [System.StringComparer]::Ordinal) -ne $true) {
        throw 'Bundled catalog snapshot file records must be sorted by ordinal path.'
    }

    return [string[]]$paths
}

function Assert-CatalogMatchesManifest {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)]$Manifest
    )

    $expectedPaths = Assert-SnapshotManifest -Manifest $Manifest
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        throw "Bundled catalog directory is missing: $Root"
    }

    $rootItem = Get-Item -LiteralPath $Root -Force
    if ($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        throw "Catalog snapshot root cannot be a reparse point: $Root"
    }

    $indexedPaths = Get-ManagedCatalogPaths -IndexPath (Join-Path $Root 'catalog.json')
    if ([System.Linq.Enumerable]::SequenceEqual(
            [string[]]$indexedPaths,
            [string[]]$expectedPaths,
            [System.StringComparer]::Ordinal) -ne $true) {
        throw 'Bundled catalog index references do not match catalog.snapshot.json.'
    }

    $reparsePoint = Get-ChildItem -LiteralPath $Root -Recurse -Force |
        Where-Object { $_.Attributes -band [System.IO.FileAttributes]::ReparsePoint } |
        Select-Object -First 1
    if ($null -ne $reparsePoint) {
        throw "Catalog snapshot contains a reparse point: $($reparsePoint.FullName)"
    }

    $actualPaths = @(Get-OrdinalSortedPaths -Paths @(
            Get-ChildItem -LiteralPath $Root -Recurse -Force -File | ForEach-Object {
                [System.IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/')
            }))
    if ([System.Linq.Enumerable]::SequenceEqual(
            [string[]]$actualPaths,
            [string[]]$expectedPaths,
            [System.StringComparer]::Ordinal) -ne $true) {
        throw 'Bundled catalog file set does not match catalog.snapshot.json.'
    }

    $expectedHashes = @{}
    foreach ($file in @($Manifest.files)) {
        $expectedHashes[$file.path] = $file.sha256
    }

    foreach ($path in $expectedPaths) {
        $actualHash = (Get-FileHash `
                -LiteralPath (Join-Path $Root $path) `
                -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -cne $expectedHashes[$path]) {
            throw "Bundled catalog file '$path' does not match its pinned SHA-256."
        }
    }
}

function New-StagingRoot {
    param([Parameter(Mandatory)][string]$BaseRoot)

    $path = Join-Path $BaseRoot ".catalog-snapshot-staging-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $path | Out-Null
    return $path
}

function Remove-StagingRoot {
    param(
        [Parameter(Mandatory)][string]$BaseRoot,
        [Parameter(Mandatory)][string]$StagingRoot
    )

    if (-not (Test-Path -LiteralPath $StagingRoot)) {
        return
    }

    $basePrefix = [System.IO.Path]::GetFullPath($BaseRoot).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $resolvedStagingRoot = [System.IO.Path]::GetFullPath($StagingRoot)
    if (-not $resolvedStagingRoot.StartsWith(
            $basePrefix,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not (Split-Path -Leaf $resolvedStagingRoot).StartsWith(
            '.catalog-snapshot-staging-',
            [System.StringComparison]::Ordinal)) {
        throw "Refusing to clean an unsafe catalog staging path: $resolvedStagingRoot"
    }

    Remove-Item -LiteralPath $resolvedStagingRoot -Recurse -Force
}

if ($Sync) {
    if ([string]::IsNullOrWhiteSpace($AuthoritativeRoot)) {
        $AuthoritativeRoot = Join-Path $repositoryRoot '..\nfg-hub-catalog'
    }

    $sourceRoot = Assert-GitRepositoryRoot -Root $AuthoritativeRoot
    $hubSnapshotChanges = @(Invoke-Git -Root $repositoryRoot -Arguments @(
            'status', '--porcelain=v1', '--', 'catalog', 'catalog.snapshot.json'))
    if ($hubSnapshotChanges.Count -ne 0) {
        throw 'Commit or restore the current bundled catalog snapshot before synchronizing it.'
    }

    $currentSnapshotManifest = Read-SnapshotManifest
    Assert-CatalogMatchesManifest -Root $catalogRoot -Manifest $currentSnapshotManifest

    $sourceCommit = @(Invoke-Git -Root $sourceRoot -Arguments @('rev-parse', 'HEAD'))[0].Trim()
    if ($sourceCommit -cnotmatch '^[0-9a-f]{40}$') {
        throw 'Could not resolve the authoritative catalog commit.'
    }

    $stagingRoot = New-StagingRoot -BaseRoot $repositoryRoot
    try {
        $export = Export-CatalogCommit `
            -SourceRoot $sourceRoot `
            -Commit $sourceCommit `
            -StagingRoot $stagingRoot

        $sourceStatusArguments = @('status', '--porcelain=v1', '--') + $export.Paths
        $sourceChanges = @(Invoke-Git `
                -Root $sourceRoot `
                -Arguments $sourceStatusArguments)
        if ($sourceChanges.Count -ne 0) {
            throw 'Commit authoritative catalog runtime files before creating a bundled snapshot.'
        }

        & dotnet run `
            --project $smokeProject `
            --configuration Release `
            --no-restore `
            -- `
            --validate-catalog $export.Root
        if ($LASTEXITCODE -ne 0) {
            throw 'The authoritative catalog failed NFG Hub contract validation.'
        }

        $records = Get-CatalogFileRecords -Root $export.Root -Paths $export.Paths
        $manifest = [ordered]@{
            schemaVersion = 1
            source = [ordered]@{
                repository = $sourceRepository
                commit = $sourceCommit
            }
            files = @($records)
        }
        $manifestJson = ($manifest | ConvertTo-Json -Depth 5).Replace("`r`n", "`n") + "`n"
        $stagedManifestPath = Join-Path $stagingRoot 'catalog.snapshot.json'
        [System.IO.File]::WriteAllText($stagedManifestPath, $manifestJson, $utf8WithoutBom)

        $validatedManifest = Get-Content -LiteralPath $stagedManifestPath -Raw | ConvertFrom-Json
        Assert-CatalogMatchesManifest -Root $export.Root -Manifest $validatedManifest

        $currentCatalogRecords = [string[]]@($currentSnapshotManifest.files | ForEach-Object {
                "$($_.path)`0$($_.sha256)"
            })
        $stagedCatalogRecords = [string[]]@($validatedManifest.files | ForEach-Object {
                "$($_.path)`0$($_.sha256)"
            })
        $catalogAlreadyCurrent = [System.Linq.Enumerable]::SequenceEqual(
            $currentCatalogRecords,
            $stagedCatalogRecords,
            [System.StringComparer]::Ordinal)

        $manifestAlreadyCurrent = $false
        if (Test-Path -LiteralPath $snapshotManifestPath -PathType Leaf) {
            $currentManifestHash = (Get-FileHash -LiteralPath $snapshotManifestPath -Algorithm SHA256).Hash
            $stagedManifestHash = (Get-FileHash -LiteralPath $stagedManifestPath -Algorithm SHA256).Hash
            $manifestAlreadyCurrent = $currentManifestHash -ceq $stagedManifestHash
        }

        $backupCatalogRoot = Join-Path $stagingRoot 'previous-catalog'
        $backupManifestPath = Join-Path $stagingRoot 'previous-catalog.snapshot.json'
        $newCatalogPromoted = $false
        $oldCatalogBackedUp = $false
        $newManifestPromoted = $false
        $oldManifestBackedUp = $false
        try {
            if (-not $catalogAlreadyCurrent) {
                Move-Item -LiteralPath $catalogRoot -Destination $backupCatalogRoot
                $oldCatalogBackedUp = $true
                Move-Item -LiteralPath $export.Root -Destination $catalogRoot
                $newCatalogPromoted = $true
            }

            if (-not $manifestAlreadyCurrent) {
                if (Test-Path -LiteralPath $snapshotManifestPath -PathType Leaf) {
                    Move-Item -LiteralPath $snapshotManifestPath -Destination $backupManifestPath
                    $oldManifestBackedUp = $true
                }
                Move-Item -LiteralPath $stagedManifestPath -Destination $snapshotManifestPath
                $newManifestPromoted = $true
            }

            Assert-CatalogMatchesManifest `
                -Root $catalogRoot `
                -Manifest (Read-SnapshotManifest)
        }
        catch {
            if ($newManifestPromoted -and (Test-Path -LiteralPath $snapshotManifestPath)) {
                Move-Item -LiteralPath $snapshotManifestPath `
                    -Destination (Join-Path $stagingRoot 'failed-catalog.snapshot.json')
            }
            if ($oldManifestBackedUp) {
                Move-Item -LiteralPath $backupManifestPath -Destination $snapshotManifestPath
            }
            if ($newCatalogPromoted -and (Test-Path -LiteralPath $catalogRoot)) {
                Move-Item -LiteralPath $catalogRoot `
                    -Destination (Join-Path $stagingRoot 'failed-catalog')
            }
            if ($oldCatalogBackedUp) {
                Move-Item -LiteralPath $backupCatalogRoot -Destination $catalogRoot
            }
            throw
        }

        [pscustomobject]@{
            Mode = 'Sync'
            SourceCommit = $sourceCommit
            FileCount = $records.Count
        }
    }
    finally {
        Remove-StagingRoot -BaseRoot $repositoryRoot -StagingRoot $stagingRoot
    }

    return
}

$snapshotManifest = Read-SnapshotManifest
Assert-CatalogMatchesManifest -Root $catalogRoot -Manifest $snapshotManifest

if (-not [string]::IsNullOrWhiteSpace($AuthoritativeRoot)) {
    $sourceRoot = Assert-GitRepositoryRoot -Root $AuthoritativeRoot
    $sourceCommit = $snapshotManifest.source.commit
    $null = Invoke-Git -Root $sourceRoot -Arguments @(
        'cat-file', '-e', "$sourceCommit^{commit}")

    $stagingRoot = New-StagingRoot -BaseRoot ([System.IO.Path]::GetTempPath())
    try {
        $export = Export-CatalogCommit `
            -SourceRoot $sourceRoot `
            -Commit $sourceCommit `
            -StagingRoot $stagingRoot
        Assert-CatalogMatchesManifest -Root $export.Root -Manifest $snapshotManifest
    }
    finally {
        Remove-StagingRoot -BaseRoot ([System.IO.Path]::GetTempPath()) -StagingRoot $stagingRoot
    }
}

[pscustomobject]@{
    Mode = 'Check'
    SourceCommit = $snapshotManifest.source.commit
    FileCount = @($snapshotManifest.files).Count
}
