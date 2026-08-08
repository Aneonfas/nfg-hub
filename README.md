# NFG Hub

NFG Hub is a lightweight Windows catalog, installer, and library for NFG products.
The first planned product is the Russian localization for Anvil Empires.

The current repository contains a working Windows Hub shell and versioned catalog/package contracts. The app loads the official catalog over HTTPS, keeps a validated local cache, downloads ZIP-only packages, detects Steam installations, and applies declared managed files without launching external installers.

Official catalog: <https://github.com/Aneonfas/nfg-hub-catalog>

## Run

```powershell
dotnet run --project .\src\Nfg.Store.App\Nfg.Store.App.csproj
```

## Verify

```powershell
dotnet build .\Nfg.Store.slnx
```

## Structure

- `src/Nfg.Store.App` — WPF desktop shell and presentation.
- `src/Nfg.Store.Core` — remote/local catalog loading, validation, cache, and fallback.
- `src/Nfg.Store.Contracts` — product and catalog manifest contracts.
- `src/Nfg.Store.Installation` — hash-guarded managed-file installation, activation state, and removal.
- `src/Nfg.Store.Platform.Windows` — Windows registry, Steam library, and app-manifest discovery.
- `catalog` — bundled fallback catalog copied into the application output.
- `docs/architecture.md` — current boundaries and installation lifecycle.

## Current installation scope

The Hub supports first installation, idempotent reinstall, adoption of an already present byte-identical managed file, reversible enable/disable, verified removal, and version switching in either direction. In a product manifest, the required `release` remains the current/default version for legacy clients, while optional `releases` entries expose additional historical versions. Each release can target its own Steam BuildID.

For the detected game build, the Hub recommends the newest exact match. If no release was verified against that build, it recommends the newest published release with a warning and still lets the user install it. The user can also choose any published version manually. Upgrades and downgrades use the same staged, hash-checked transaction, preserve the enabled state, and use a persisted journal to roll back an uncommitted change or finish cleanup after a committed change. Installation state records the Steam BuildID detected when the version was applied. Disabling preserves the verified file under a non-loadable `.nfg-disabled` name. Unknown files are never overwritten.

All product mutations use a per-product cross-process lock, so two Hub windows cannot modify the same installation concurrently.
