# NFG Hub

NFG Hub is a lightweight Windows catalog, installer, and library for NFG products.
Hub 0.2.0 adds Russian and Spanish Anvil Empires localization variants.

The current repository contains a working Windows Hub shell and versioned catalog/package contracts. The app loads the official catalog over HTTPS, keeps a validated local cache, downloads ZIP-only packages, detects Steam installations, and applies declared managed files without launching external installers.

The current development branch offers the Hub interface in English, Russian,
Spanish, German, French, Brazilian Portuguese, Simplified Chinese, Japanese,
Korean, and Turkish. Localization families are reconciled with their actual
managed files at startup. Their single Remove action can delete the language
selected in the list or, when other languages are present, every language in
the family.

Official catalog: <https://github.com/Aneonfas/nfg-hub-catalog>

## Run

```powershell
dotnet run --project .\src\Nfg.Store.App\Nfg.Store.App.csproj
```

## Verify

```powershell
.\scripts\verify.ps1 -Configuration Release
```

## Releases

Official release packages are built from annotated version tags by GitHub
Actions. The workflow runs the same verification script, creates the portable
self-contained Windows x64 ZIP and SHA-256 file, records build provenance, and
prepares a draft GitHub Release for approval. Local packages are QA artifacts,
not an alternative public distribution.

See [docs/releasing.md](docs/releasing.md) for the release policy and commands.

## Structure

- `src/Nfg.Store.App` — WPF desktop shell and presentation.
- `src/Nfg.Store.Core` — remote/local catalog loading, validation, cache, and fallback.
- `src/Nfg.Store.Contracts` — product and catalog manifest contracts.
- `src/Nfg.Store.Installation` — hash-guarded managed-file installation, activation state, and removal.
- `src/Nfg.Store.Platform.Windows` — Windows registry, Steam library, and app-manifest discovery.
- `catalog-v2` — pinned bundled fallback used by Hub 0.2.0.
- `catalog-v2.snapshot.json` — authoritative commit and SHA-256 lock for that snapshot.
- `catalog` and `catalog.snapshot.json` — retained legacy snapshot for Hub 0.1.1.
- `docs/architecture.md` — current boundaries and installation lifecycle.

## Current installation scope

The Hub supports first installation, idempotent reinstall, adoption of an already present byte-identical managed file, reversible enable/disable, verified removal, and version switching in either direction. In a product manifest, the required `release` remains the current/default version for legacy clients, while optional `releases` entries expose additional historical versions. Each release can target its own Steam BuildID.

For the detected game build, the Hub recommends the newest exact match. If no release was verified against that build, it recommends the newest published release with a warning and still lets the user install it. The user can also choose any published version manually. Upgrades and downgrades use the same staged, hash-checked transaction, preserve the enabled state, and use a persisted journal to roll back an uncommitted change or finish cleanup after a committed change. Installation state records the Steam BuildID detected when the version was applied. Disabling preserves the verified file under a non-loadable `.nfg-disabled` name. Unknown files are never overwritten.

All product mutations use a per-installation-slot cross-process lock, so two Hub windows cannot modify the same game slot concurrently.

The public catalog is authored in
[`Aneonfas/nfg-hub-catalog`](https://github.com/Aneonfas/nfg-hub-catalog).
Bundled files are synchronized from a committed state of that repository with
`scripts/catalog-snapshot.ps1`; `scripts/verify.ps1` rejects manual drift from
the pinned commit and hashes. The public root catalog remains the Product
Manifest schema-v1 feed for Hub 0.1.1. Hub 0.2.0 uses the separate
`/v2/catalog.json` feed and `catalog-v2` cache/bundle namespace.
