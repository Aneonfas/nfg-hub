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

The catalog points to product manifest paths relative to the catalog root.
Product manifest schema v1 remains fully compatible with the identifier rules
accepted by NFG Hub 0.1.1. Schema v2 uses strict safe ASCII reverse-DNS product
and dependency identifiers and adds three optional variant fields, all of which
are required for a `localization` product:

- `familyId` is the stable product family used for UI grouping.
- `locale` is the variant's structurally valid BCP-47 language tag.
- `exclusiveGroup` is the stable id of a shared installation slot.

Locales are unique without regard to case inside a `familyId`, independently of
the installation slot. Every language variant in one family must declare the
same `exclusiveGroup`. Products sharing an `exclusiveGroup` must also target the
same Steam AppID and use the same installation strategy. This cross-validation
is performed only after the complete catalog has loaded and before a remote
copy can replace the cache.

The Hub derives `installationKey = exclusiveGroup ?? productId`. The key names
the installation state JSON, transaction journal, and cross-process lock;
`productId` in schema-v2 installation state identifies the active variant.
`familyId` and `locale` remain catalog data and are not duplicated in device
state. Ungrouped products retain `installationKey = productId` and therefore do
not share mutation state with a language family.

The required `release` field is the current/default release and keeps schema-v1
catalogs and older clients working. The optional `releases` array contains
additional historical releases. Version strings are unique across both fields
and use strict Semantic Versioning 2.0.0.

A release becomes installable only when all of the following are present:

1. A non-development version.
2. A supported Windows platform.
3. A payload URL, byte size, and SHA-256 digest.
4. A known installation strategy.

Each release can declare its own `gameVersion` in `steam-build-<BuildID>` form. When it is omitted, the Hub uses the legacy product-level `compatibility.gameVersion`. Missing or mismatched game compatibility does not disable installation: it marks the release as unverified for the detected game build and requires an explicit user confirmation. Missing payload information still keeps the install action disabled.

Product-facing information is delivered by the catalog rather than inferred from the ZIP or scraped from a hosting page. `display.features` describes durable capabilities; each release's `highlights`, `knownIssues`, and `notesUrl` describe that specific published version. These fields are optional for backward compatibility, but list entries must be meaningful non-empty text and external links must use HTTPS. The `nfg-package.json` manifest remains the unchanged `nfg-package/1` machine-only installation contract; family, locale, and installation-slot metadata belong to the product catalog, not the package ZIP.

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

Installing, switching language variants, and switching versions use one
transaction. The Hub downloads and validates the complete selected package
before touching the game. Upgrade, downgrade, and a cross-product switch use the
same transaction, including when two variants have the same semantic version:
the Hub writes a persisted update journal, stages every changed file beside its
destination, moves verified old files to transaction-specific backups, activates
the staged files, verifies the new state, and atomically commits the installation
JSON. The previous enabled or disabled state is preserved, and
`DetectedSteamBuildId` records the game build observed when the selected release
was applied. Before the commit point any failure restores the old files; after it,
recovery completes cleanup without reverting the installed version. Pending
journals under `state/transactions` are recovered before installation state is
loaded at Hub startup. Every mutation holds an installation-slot lock under
`state/locks`, so separate Hub processes cannot interleave installation,
variant or version switching, activation, removal, or recovery. Callers also
provide the expected active `productId`; an inactive family member cannot alter
or remove its active sibling.

Schema-v1 journals are recovered before state migration. Startup then unions
library ids with every discovered installation state's `productId` before
loading the catalog. Schema-v1 state is migrated under the destination slot
lock, with an exact byte-for-byte source backup in
`state/migrations/installations-v1`. Migration is atomic and idempotent. A
legacy/current conflict, two legacy members mapping to one slot, malformed or
ambiguous state, or an unrecognized recovery state fails closed without
rewriting the original evidence or managed game files.

## Catalog and library state

The main navigation contains Catalog, Library, and Settings. Catalog cards are
discovery surfaces and open a dedicated product page; they do not install a
package directly. A new product is installed from its page, while Library can
retry or reinstall a product that is already a library member.

After a successful installation the active product id is persisted in
`%LOCALAPPDATA%\NFG\Hub\state\library.json`. Catalog, product details, and
Library observe one long-lived view model per family (or per standalone
product), so language selection, progress, errors, and installed state stay in
sync across surfaces. A family presents language selection before version
selection and distinguishes the installed variant from the selected target.
Existing managed installations are added to library state during startup
migration.

Catalog loading treats the union of local library ids and all product ids found
in installation state as required. A remote catalog that omits one is rejected
before it can replace the cache, and the Hub falls back to the last validated
catalog. Published products therefore cannot silently disappear while a user
still needs the Hub to switch, disable, or remove them.

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

Hub 0.2.0 reads
`https://raw.githubusercontent.com/Aneonfas/nfg-hub-catalog/main/v2/catalog.json`.
This endpoint references Russian and Spanish Product Manifest v2 variants and
the existing Forge Helper. The legacy root endpoint remains schema v1 for Hub
0.1.1 and its product files are not repurposed as v2 manifests.

At startup the app downloads the index and every referenced product manifest,
validates the complete set, and only then replaces cached files. The catalog
load order is remote, last validated cache, then the catalog bundled with the
application. A failed or partial remote update never replaces the usable
fallback index.

The automatic-update setting controls whether startup contacts the remote
catalog. When it is disabled, startup performs no catalog network request and
loads the last validated cache, then the bundled catalog. The setting is stored
atomically in `%LOCALAPPDATA%\NFG\Hub\state\settings.json`.

The Hub 0.2.0 cache lives under `%LOCALAPPDATA%\NFG\Hub\cache\catalog-v2`.
The old `%LOCALAPPDATA%\NFG\Hub\cache\catalog` feed cache remains separate. On the first Hub
startup, an existing `%LOCALAPPDATA%\NFG\Store` tree is copied atomically into
the new location. The legacy tree remains unchanged as a migration backup.

## Bundled catalog state

[`Aneonfas/nfg-hub-catalog`](https://github.com/Aneonfas/nfg-hub-catalog) is
the authoritative catalog. Hub 0.2.0 uses `catalog-v2/` as its bundled fallback;
`catalog-v2.snapshot.json` pins the source path, authoritative commit, and
SHA-256 of every runtime file. The legacy `catalog/` snapshot and lock remain
available for reproducibility of Hub 0.1.1.

`scripts/catalog-snapshot.ps1 -Sync -Feed V2` reads committed Git blobs from the local
authoritative checkout, validates the complete staged catalog through
`CatalogService`, and promotes only those runtime files and their lock with
rollback on failure. `-Check` is offline and verifies the exact file set and
hashes; `scripts/verify.ps1` checks both legacy and v2 snapshots, so CI and release builds
reject uncontrolled drift. A later authoritative `main` update does not
invalidate an older Hub tag because each Hub commit remains pinned to its exact
source commit.

Each current `nfg-package/1` manifest declares one managed PAK under
`Anvil/Content/Paks`. The Hub validates and installs those files directly; no
EXE, MSI, PowerShell, or package-provided code is executed.
