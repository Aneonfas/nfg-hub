# NFG Hub architecture

## Product boundary

NFG Hub owns discovery, download, installation state, updates, rollback, removal, and launch. A product owns its payload and product-specific installation rules. NFG Player is not a Hub dependency; it can be declared as a product dependency later if a package needs it.

## Projects

- `Nfg.Store.Contracts` contains JSON-facing records and no platform code.
- `Nfg.Store.Core` downloads and validates catalogs, maintains a local cache, and loads local fallback catalogs without referencing WPF.
- `Nfg.Store.App` renders state and will host Windows-specific composition roots.
- `Nfg.Store.Installation` owns managed-file state, staging, adoption, and verified removal.
- `Nfg.Store.Platform.Windows` discovers Steam libraries and parses app manifests.

Package interpretation stays in Core, file mutation stays in Installation, and Steam/registry access stays in Platform.Windows. The catalog and ZIP contain declarative data only; packages cannot execute scripts or arbitrary commands.

## Manifest rules

Every product has a stable reverse-DNS identifier. The catalog points to product manifest paths relative to the catalog root. The required `release` field is the current/default release and keeps schema-v1 catalogs and older clients working. The optional `releases` array contains additional historical releases. Version strings are unique across both fields and use strict Semantic Versioning 2.0.0.

A release becomes installable only when all of the following are present:

1. A non-development version.
2. A supported Windows platform.
3. A payload URL, byte size, and SHA-256 digest.
4. A known installation strategy.

Each release can declare its own `gameVersion` in `steam-build-<BuildID>` form. When it is omitted, the Hub uses the legacy product-level `compatibility.gameVersion`. Missing or mismatched game compatibility does not disable installation: it marks the release as unverified for the detected game build and requires an explicit user confirmation. Missing payload information still keeps the install action disabled.

Product-facing information is delivered by the catalog rather than inferred from the ZIP or scraped from a hosting page. `display.features` describes durable capabilities; each release's `highlights`, `knownIssues`, and `notesUrl` describe that specific published version. These fields are optional for backward compatibility, but list entries must be meaningful non-empty text and external links must use HTTPS. The `nfg-package.json` manifest remains a machine-only installation contract.

## Installation lifecycle

```text
discover Steam game -> select exact-build release or newest published fallback
-> warn and confirm when the selected release is unverified -> download ZIP
-> verify outer SHA-256 -> validate nfg-package/1 -> stream declared files to sibling staging
-> verify inner SHA-256 -> atomically move new files -> record installed state
```

Unknown existing files are never overwritten. A byte-identical existing PAK can
be adopted without rewriting the game. An installed product can be disabled
without removal: after verifying its recorded hash, the Hub atomically renames
each managed file with a `.nfg-disabled` suffix and reverses that rename when the
product is enabled. Removal verifies and deletes the active or disabled
Hub-owned copy.

For the Steam BuildID detected on the device, the Hub recommends the highest
published release with an exact `gameVersion` match. If there is no exact match,
it recommends the newest published release and labels compatibility as unverified;
the user may confirm that installation or select another published version from
the version list. An exact match is a recommendation, not a hard installation
requirement.

When switching versions, the Hub downloads and validates the complete selected
package before touching the game. Upgrade and downgrade use the same transaction:
the Hub writes a persisted update journal, stages every changed file beside its
destination, moves verified old files to transaction-specific backups, activates
the staged files, verifies the new state, and atomically commits the installation
JSON. The previous enabled or disabled state is preserved, and
`DetectedSteamBuildId` records the game build observed when the selected release
was applied. Before the commit point any failure restores the old files; after it,
recovery completes cleanup without reverting the installed version. Pending
journals under `state/transactions` are recovered before installation state is
loaded at Hub startup. Every product mutation also holds a per-product lock file
under `state/locks`, so separate Hub processes cannot interleave installation,
version switching, activation, removal, or recovery.

## Catalog and library state

The main navigation contains Catalog, Library, and Settings. Catalog cards are
discovery surfaces and open a dedicated product page; they do not install a
package directly. A new product is installed from its page, while Library can
retry or reinstall a product that is already a library member.

Starting an installation first persists the product id in
`%LOCALAPPDATA%\NFG\Hub\state\library.json`. Catalog, product details, and
Library then observe the same long-lived per-product view model, so download
progress and errors survive page navigation. Existing managed installations
are added to the library state during startup migration.

Catalog loading also treats every local library product id as required. A
remote catalog that omits one is rejected before it can replace the cache, and
the Hub falls back to the last validated catalog. Published products therefore
cannot silently disappear while a user still needs the Hub to disable or
remove them.

Library membership and device installation are separate states. Removing a
product from the device verifies and removes its managed files but keeps the
library entry. Removing it from the library first performs the same verified
device removal when necessary and deletes the library entry only after that
succeeds. This prevents Hub-managed files from being orphaned. The former
Downloads page was a static placeholder and is no longer part of the navigation;
download execution remains an internal Hub service.

Product details and Library show the recommended action for the currently
detected Steam BuildID: upgrade, downgrade, or keep the installed version. A
version selector exposes all published releases, identifies the recommended
entry, and labels releases that were not verified against the
current game build. Manual selection follows the same confirmation and
transaction rules as the recommended action.

## Catalog delivery

The primary index is published at
`https://raw.githubusercontent.com/Aneonfas/nfg-hub-catalog/main/catalog.json`.
At startup the app downloads the index and every referenced product manifest,
validates the complete set, and only then replaces cached files. The catalog
load order is remote, last validated cache, then the catalog bundled with the
application. A failed or partial remote update never replaces the usable
fallback index.

The automatic-update setting controls whether startup contacts the remote
catalog. When it is disabled, startup performs no catalog network request and
loads the last validated cache, then the bundled catalog. The setting is stored
atomically in `%LOCALAPPDATA%\NFG\Hub\state\settings.json`.

The cache lives under `%LOCALAPPDATA%\NFG\Hub\cache\catalog`. On the first Hub
startup, an existing `%LOCALAPPDATA%\NFG\Store` tree is copied atomically into
the new location. The legacy tree remains unchanged as a migration backup.

## Bundled catalog state

The bundled Anvil Empires catalog contains the Russian localization v1.0.0 for
Steam build `24378492` and Anvil Forge Helper v1.0.0 for Steam build `24619810`.
Each `nfg-package/1` manifest declares one managed PAK under
`Anvil/Content/Paks`. The Hub validates and installs those files directly; no
EXE, MSI, PowerShell, or package-provided code is executed.
