# NFG Hub release process

GitHub Actions is the only canonical publisher of NFG Hub binaries. Local
release builds are for development and verification; they must not be uploaded
beside the Actions-generated package for the same version and runtime.

## Continuous integration

`.github/workflows/ci.yml` runs on pull requests, pushes to `main`, and manual
dispatch. It restores the exact pinned SDK, verifies formatting, builds the
solution, runs the smoke checks, and performs a complete release-package dry
run.

## Publishing a release

1. Update `<Version>` in `src/Nfg.Store.App/Nfg.Store.App.csproj`.
2. Merge the reviewed change into `main` and ensure CI passes.
3. Create an annotated tag on that commit and push it:

   ```powershell
   git switch main
   git pull --ff-only
   git tag -a v0.3.0 -m "NFG Hub 0.3.0"
   git push origin v0.3.0
   ```

4. The release workflow verifies that the tag is annotated, belongs to `main`,
   and matches the project version. It then builds and tests the tagged source,
   creates the self-contained Windows x64 ZIP and SHA-256 file, and publishes a
   build-provenance attestation. The handoff job independently verifies both
   the checksum and attestation before it is allowed to upload either asset.
5. The workflow creates a draft GitHub Release. Review its run, assets, notes,
   and attestation before publishing the draft manually.

The workflow can be rerun from GitHub's run page, or dispatched explicitly for
an existing tag with `gh workflow run release.yml --ref v0.3.0`. A manual
dispatch from a branch is rejected. The workflow may reuse assets only while
the release is still a draft and only when their digests are identical. It
refuses to replace different bytes or any asset in a published release.
Corrections to a published build require a new patch version and tag.

Repository policy restricts workflows to GitHub-owned actions pinned to full
commit SHAs. Release tags are protected from update and deletion, and published
releases are immutable.

`v0.1.0` predates this automated release process and was built locally. GitHub
does not apply the new immutability policy retroactively, so that release is
kept as a legacy build. All subsequent official binaries must be produced by
the release workflow; the next one should become the current **Latest** release.

## Updating the bundled catalog snapshot

Product metadata is authored in the sibling `nfg-hub-catalog` repository. Its
root `catalog.json` is the legacy Product Manifest schema-v1 feed consumed by
Hub 0.1.1. Hub 0.2.0 and later consume `/v2/catalog.json` and use the separate
`catalog-v2/` bundled snapshot. Root-referenced product files must never be
converted to v2.

Commit and validate the authoritative change first. Then, from the NFG Hub
checkout, synchronize the exact committed blobs:

```powershell
dotnet restore .\Nfg.Store.slnx

.\scripts\catalog-snapshot.ps1 `
    -Sync `
    -Feed V2 `
    -AuthoritativeRoot ..\nfg-hub-catalog

.\scripts\catalog-snapshot.ps1 `
    -Check `
    -Feed V2 `
    -AuthoritativeRoot ..\nfg-hub-catalog

.\scripts\verify.ps1 -Configuration Release
```

`-Sync` does not fetch, checkout, commit, push, tag, or publish. It refuses
uncommitted authoritative runtime files and existing uncommitted snapshot
changes, exports the source repository's exact `HEAD` blobs, validates them with
the Hub contract, and writes `catalog-v2.snapshot.json` with the source path,
commit, and file hashes. Review and commit the Hub snapshot separately. Never edit bundled
product manifests as a second authoritative copy.

CI uses `-Check` without a sibling checkout or network access. This verifies the
pinned snapshot rather than moving `main`, so an older Hub commit remains
reproducible after the public catalog advances.

## Local verification

Run the same verification used by CI:

```powershell
.\scripts\verify.ps1 -Configuration Release
```

Build a local package for QA from a clean checkout using the version declared by
the project:

```powershell
[xml]$project = Get-Content .\src\Nfg.Store.App\Nfg.Store.App.csproj
$version = $project.Project.PropertyGroup.Version
.\scripts\build-release.ps1 `
    -Version $version `
    -OutputRoot .\artifacts\local-release
```

This package is not an official release asset.

The first public packages are not Authenticode-signed, so Windows can identify
the publisher as unknown even though GitHub provenance and the checksum verify
where the archive came from. Code signing can be added independently later.

After GitHub Actions creates an attestation, a downloaded official ZIP can be
verified with GitHub CLI:

```powershell
gh attestation verify .\NFG-Hub-v0.3.0-win-x64.zip `
    --repo Aneonfas/nfg-hub
```
