using System.IO.Compression;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nfg.Store.App.Services;
using Nfg.Store.App.Localization;
using Nfg.Store.App.ViewModels;
using Nfg.Store.Contracts;
using Nfg.Store.Core;
using Nfg.Store.Installation;
using Nfg.Store.Platform.Windows;

LocalizationService.Instance.SetLanguage("ru");

if (args.Length > 0)
{
    if (args is ["--validate-catalog", var catalogRoot])
    {
        await new CatalogService().LoadAsync(catalogRoot);
        Console.WriteLine($"Catalog '{Path.GetFullPath(catalogRoot)}' is valid.");
        return;
    }

    if (args is
        [
            "--hold-installation-lock",
            var lockDataRoot,
            var lockInstallationKey,
            var lockReadyPath,
            var lockReleasePath
        ])
    {
        await ManagedStateMutationSmoke.HoldInstallationLockAsync(
            lockDataRoot,
            lockInstallationKey,
            lockReadyPath,
            lockReleasePath);
        return;
    }

    if (args is ["--managed-state-mutation"])
    {
        var focusedRoot = Path.Combine(
            Path.GetTempPath(),
            "nfg-store-mutation-smoke",
            Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(focusedRoot);
            await ManagedStateMutationSmoke.RunAsync(focusedRoot);
            Console.WriteLine("NFG Hub managed-state mutation smoke checks passed.");
        }
        finally
        {
            if (Directory.Exists(focusedRoot))
            {
                Directory.Delete(focusedRoot, recursive: true);
            }
        }

        return;
    }

    if (args is ["--product-family-view-model"])
    {
        var focusedRoot = Path.Combine(
            Path.GetTempPath(),
            "nfg-store-family-vm-smoke",
            Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(focusedRoot);
            await ProductFamilyViewModelSmoke.RunAsync(focusedRoot);
            Console.WriteLine("NFG Hub product-family view-model smoke checks passed.");
        }
        finally
        {
            if (Directory.Exists(focusedRoot))
            {
                Directory.Delete(focusedRoot, recursive: true);
            }
        }

        return;
    }

    throw new ArgumentException(
        "Usage: Nfg.Store.Core.Smoke " +
        "[--validate-catalog <catalog-root> | --product-family-view-model | " +
        "--managed-state-mutation]");
}

var catalogUri = new Uri("https://catalog.test/catalog.json");
var bundledRoot = Path.Combine(AppContext.BaseDirectory, "catalog");
var testRoot = Path.Combine(
    Path.GetTempPath(),
    "nfg-store-smoke",
    Guid.NewGuid().ToString("N"));
var cacheRoot = Path.Combine(testRoot, "cache");
var emptyCacheRoot = Path.Combine(testRoot, "empty-cache");

try
{
    Directory.CreateDirectory(testRoot);
    await CheckSemanticVersionContractAsync();
    CheckLocalization();
    CheckMultiReleaseSelection();
    CheckRefreshGameCompatibility();
    CheckDetectionWithoutSingleSteamAppId();
    await CheckMultipleSteamInstallationsUseExactBuildAsync();
    CheckFallbackReleasePresentation();
    CheckProductUpdatePresentation();
    await ProductFamilyViewModelSmoke.RunAsync(testRoot);
    CheckAppSettingsPersistence();
    CheckAppDataMigration();
    await CheckCatalogSourcesAsync();
    await CheckRichCatalogValidationAsync();
    await CheckProductSchemaV2ValidationAsync();
    await InstallationStateV2Smoke.RunAsync(testRoot);
    await ManagedUpdateJournalV2Smoke.RunAsync(testRoot);
    await ManagedVariantInstallerSmoke.RunAsync(testRoot);
    await ManagedStateMutationSmoke.RunAsync(testRoot);
    CheckInstallationCatalogRequirementUnion();
    await CheckProductLibraryStoreAsync();
    await CheckPackageDownloadAndValidationAsync();
    await CheckManagedUpdateAsync();
    await CheckCoordinatorAllowsUnverifiedBuildAsync();
    CheckSteamDiscovery();
    Console.WriteLine("NFG Hub smoke checks passed.");
}
finally
{
    if (Directory.Exists(testRoot))
    {
        Directory.Delete(testRoot, recursive: true);
    }
}

void CheckInstallationCatalogRequirementUnion()
{
    var prototype = new InstalledProductState
    {
        SchemaVersion = 2,
        InstallationKey = "nfg.anvil-empires.ru",
        ProductId = "nfg.anvil-empires.es",
        Version = "1.0.0",
        PackageSizeBytes = 1,
        PackageSha256 = new string('a', 64),
        SteamAppId = "2383950",
        SteamBuildId = "24619810",
        GameRoot = Path.Combine(testRoot, "required-products-game"),
        InstalledAt = DateTimeOffset.UnixEpoch,
        Files =
        [
            new InstalledFileState
            {
                Destination = "Anvil/Content/Paks/test.pak",
                SizeBytes = 1,
                Sha256 = new string('b', 64)
            }
        ]
    };
    var required = InstallationCatalogRequirements.UnionProductIds(
        ["nfg.anvil-empires.ru"],
        [
            prototype,
            prototype with
            {
                InstallationKey = "nfg.anvil-empires.forge-helper",
                ProductId = "nfg.anvil-empires.forge-helper"
            }
        ]);

    Assert(
        required.Count == 3 &&
        required.Contains("nfg.anvil-empires.ru") &&
        required.Contains("nfg.anvil-empires.es") &&
        required.Contains("nfg.anvil-empires.forge-helper"),
        "Startup catalog requirements did not union library and installed product ids.");
}

async Task CheckSemanticVersionContractAsync()
{
    string[] validVersions =
    [
        "0.0.0",
        "1.0.0",
        "1.0.0-alpha",
        "1.0.0-alpha.1",
        "1.0.0-0.3.7",
        "1.0.0-x.7.z.92",
        "1.0.0-x-y-z.--",
        "1.0.0+20130313144700",
        "1.0.0-beta+exp.sha.5114f85",
        "999999999999999999999999999999.0.0"
    ];
    foreach (var version in validVersions)
    {
        Assert(
            SemanticVersionComparer.IsValid(version),
            $"Valid SemVer '{version}' was rejected.");
    }

    string[] invalidVersions =
    [
        "",
        "1",
        "1.0",
        "1.0.0.0",
        "01.0.0",
        "1.01.0",
        "1.0.01",
        "1.0.0-",
        "1.0.0-01",
        "1.0.0-alpha..1",
        "1.0.0+",
        "1.0.0+build..1",
        "1.0.0-alpha_1",
        "1.0.0 alpha",
        "v1.0.0",
        "1.0.0-альфа"
    ];
    foreach (var version in invalidVersions)
    {
        Assert(
            !SemanticVersionComparer.IsValid(version),
            $"Invalid SemVer '{version}' was accepted.");
    }

    string[] precedence =
    [
        "1.0.0-alpha",
        "1.0.0-alpha.1",
        "1.0.0-alpha.beta",
        "1.0.0-beta",
        "1.0.0-beta.2",
        "1.0.0-beta.11",
        "1.0.0-rc.1",
        "1.0.0"
    ];
    for (var index = 0; index < precedence.Length - 1; index++)
    {
        var lower = precedence[index];
        var higher = precedence[index + 1];
        Assert(
            SemanticVersionComparer.TryCompare(lower, higher, out var ascending) && ascending < 0,
            $"SemVer precedence should place '{lower}' before '{higher}'.");
        Assert(
            SemanticVersionComparer.TryCompare(higher, lower, out var descending) && descending > 0,
            $"SemVer precedence should place '{higher}' after '{lower}'.");
    }

    Assert(
        SemanticVersionComparer.TryCompare(
            "999999999999999999999999999999.0.0",
            "1000000000000000000000000000000.0.0",
            out var largeNumericComparison) && largeNumericComparison < 0,
        "Arbitrarily large numeric SemVer identifiers were not compared numerically.");
    Assert(
        SemanticVersionComparer.TryCompare(
            "1.0.0+build.1",
            "1.0.0+build.2",
            out var metadataComparison) && metadataComparison == 0,
        "Build metadata must not affect SemVer precedence.");
    Assert(
        !SemanticVersionComparer.TryCompare("1.0", "1.0.0", out var invalidComparison) &&
        invalidComparison == 0,
        "TryCompare should reject invalid input and reset its result.");

    var product = CreateProduct(1, new string('a', 64));
    await AssertCatalogRejectedAsync(
        "invalid-semver",
        product with { Release = product.Release with { Version = "01.0.0" } },
        "A product with an invalid release version should be rejected.");
}

void CheckMultiReleaseSelection()
{
    var current = CreateProduct(1, new string('a', 64), "2.0.0");
    current = current with
    {
        Release = current.Release with { GameVersion = "steam-build-200" },
        Releases =
        [
            current.Release with
            {
                Version = "1.5.0",
                GameVersion = "steam-build-100",
                NotesUrl = new Uri("https://catalog.test/releases/1.5.0")
            },
            current.Release with
            {
                Version = "1.0.0",
                GameVersion = "steam-build-50",
                NotesUrl = new Uri("https://catalog.test/releases/1.0.0")
            }
        ]
    };

    var available = ProductReleaseCatalog.GetAvailableReleases(current);
    Assert(
        available.Select(release => release.Version).SequenceEqual(["2.0.0", "1.5.0", "1.0.0"]),
        "Available releases were not ordered by descending SemVer.");
    var recommendedForOlderGame = ProductReleaseCatalog.SelectRecommendedRelease(current, "100");
    Assert(
        recommendedForOlderGame?.Version == "1.5.0",
        "The release verified for the detected game build was not recommended.");
    Assert(
        ProductReleaseCatalog.SelectRecommendedRelease(current, "999")?.Version == "2.0.0",
        "An unknown game build should fall back to the newest published release.");
    var selectedManifest = ProductReleaseCatalog.CreateManifestForRelease(
        current,
        recommendedForOlderGame!);
    Assert(
        selectedManifest.Release.Version == "1.5.0" &&
        selectedManifest.Compatibility.GameVersion == "steam-build-100",
        "A release-specific manifest did not carry its tested game version.");
}

void CheckRefreshGameCompatibility()
{
    var product = CreateBranchProduct();
    var libraryRoot = Path.Combine(testRoot, "compatibility-refresh-steam");
    var gameRoot = WriteSteamInstallation(
        libraryRoot,
        "Anvil Compatibility Refresh",
        buildId: "200");
    var dataRoot = Path.Combine(testRoot, "compatibility-refresh-data");
    using var httpClient = new HttpClient(new OfflineHandler());
    var coordinator = new ProductInstallationCoordinator(
        httpClient,
        dataRoot,
        new InstallationStateStore(dataRoot),
        new SteamGameLocator([libraryRoot], includeConfiguredSteam: false));
    var initialInstallation = coordinator.DetectGameInstallation(product);
    Assert(initialInstallation?.BuildId == "200",
        "The initial Steam build was not detected for the open Hub fixture.");

    var viewModel = new ProductStateViewModel(
        new ProductViewModel(product),
        product,
        coordinator,
        new ProductLibraryStore(dataRoot),
        installedState: null,
        isInLibrary: false,
        detectedGameBuildId: initialInstallation!.BuildId);
    Assert(
        viewModel.SelectedVersion?.Version == "2.0.0" &&
        viewModel.SelectedVersion.IsRecommended &&
        viewModel.IsSelectedVersionExact,
        "The release matching the initial Steam build was not selected automatically.");

    WriteSteamInstallation(
        libraryRoot,
        Path.GetFileName(gameRoot),
        buildId: "100");
    viewModel.RefreshGameCompatibility();

    Assert(
        viewModel.SelectedVersion?.Version == "1.0.0" &&
        viewModel.SelectedVersion.IsRecommended &&
        viewModel.IsSelectedVersionExact,
        "Refreshing an open Hub did not select the release matching the changed Steam build.");
    Assert(viewModel.CatalogVersionLabel == "1.0.0",
        "The catalog-facing version did not follow refreshed game compatibility.");
}

void CheckDetectionWithoutSingleSteamAppId()
{
    var product = CreateProduct(1, new string('a', 64));
    var dataRoot = Path.Combine(testRoot, "optional-steam-detection-data");
    using var httpClient = new HttpClient(new OfflineHandler());
    var coordinator = new ProductInstallationCoordinator(
        httpClient,
        dataRoot,
        new InstallationStateStore(dataRoot),
        new SteamGameLocator([], includeConfiguredSteam: false));

    var withoutSteam = product with
    {
        Installation = product.Installation with { Detection = [] }
    };
    Assert(coordinator.DetectGameInstallation(withoutSteam) is null,
        "Detection should return null when a product has no Steam App ID.");

    var ambiguousSteam = product with
    {
        Installation = product.Installation with
        {
            Detection =
            [
                new ProductDetectionRule { Provider = "steam", ProductId = "2383950" },
                new ProductDetectionRule { Provider = "steam", ProductId = "9999999" }
            ]
        }
    };
    Assert(coordinator.DetectGameInstallation(ambiguousSteam) is null,
        "Detection should return null when a product declares multiple Steam App IDs.");
}

async Task CheckMultipleSteamInstallationsUseExactBuildAsync()
{
    const string exactBuildId = "24378492";
    var packageBytes = BuildPackageBytes(version: "1.0.0");
    var product = CreateProduct(
        packageBytes.LongLength,
        Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant());
    var mismatchLibraryRoot = Path.Combine(testRoot, "multi-steam-a-mismatch");
    var exactLibraryRoot = Path.Combine(testRoot, "multi-steam-z-exact");
    var mismatchGameRoot = WriteSteamInstallation(
        mismatchLibraryRoot,
        "Anvil Mismatch",
        buildId: "99999999");
    var exactGameRoot = WriteSteamInstallation(
        exactLibraryRoot,
        "Anvil Exact",
        exactBuildId);
    Directory.CreateDirectory(Path.Combine(mismatchGameRoot, "Anvil", "Content", "Paks"));
    Directory.CreateDirectory(Path.Combine(exactGameRoot, "Anvil", "Content", "Paks"));

    var dataRoot = Path.Combine(testRoot, "multi-steam-data");
    var packageHandler = new CountingBytesHandler(packageBytes);
    using var httpClient = new HttpClient(packageHandler);
    var coordinator = new ProductInstallationCoordinator(
        httpClient,
        dataRoot,
        new InstallationStateStore(dataRoot),
        new SteamGameLocator(
            [mismatchLibraryRoot, exactLibraryRoot],
            includeConfiguredSteam: false));

    var detected = coordinator.DetectGameInstallation(product);
    Assert(
        detected?.BuildId == exactBuildId && PathsEqual(detected.GameRoot, exactGameRoot),
        "Game detection did not prioritize the Steam installation with the exact BuildID.");

    var installed = await coordinator.InstallAsync(product);
    Assert(PathsEqual(installed.State.GameRoot, exactGameRoot),
        "Installation targeted a different Steam copy than compatibility detection.");
    Assert(installed.State.DetectedSteamBuildId == exactBuildId,
        "The exact BuildID selected during installation was not persisted.");
    Assert(!File.Exists(Path.Combine(
            mismatchGameRoot,
            "Anvil",
            "Content",
            "Paks",
            "Test-Russian.pak")),
        "Installation wrote product files into the lower-priority Steam copy.");
}

void CheckFallbackReleasePresentation()
{
    var product = CreateBranchProduct();
    var dataRoot = Path.Combine(testRoot, "fallback-release-data");
    using var httpClient = new HttpClient(new OfflineHandler());
    var viewModel = new ProductStateViewModel(
        new ProductViewModel(product),
        product,
        new ProductInstallationCoordinator(
            httpClient,
            dataRoot,
            new InstallationStateStore(dataRoot),
            new SteamGameLocator([], includeConfiguredSteam: false)),
        new ProductLibraryStore(dataRoot),
        installedState: null,
        isInLibrary: false,
        detectedGameBuildId: "999");

    var fallbackVersion = viewModel.SelectedVersion
        ?? throw new InvalidOperationException("An unknown Steam build did not select a fallback release.");
    Assert(fallbackVersion.Version == "2.0.0",
        "An unknown Steam build should select the newest published fallback release.");
    Assert(fallbackVersion.IsAutomaticChoice && !fallbackVersion.IsRecommended,
        "An unverified fallback release must not be marked as recommended.");
    Assert(
        fallbackVersion.DisplayLabel == "2.0.0 — последняя доступная" &&
        !fallbackVersion.DisplayLabel.Contains("рекомендуется", StringComparison.Ordinal),
        "An unverified fallback release should be labelled as the latest available version.");
}

void CheckProductUpdatePresentation()
{
    var product = CreateProduct(1, new string('a', 64), "1.1.0");
    var installedState = new InstalledProductState
    {
        SchemaVersion = 1,
        ProductId = product.Id,
        Version = "1.0.0",
        PackageSizeBytes = 1,
        PackageSha256 = new string('b', 64),
        SteamAppId = "2383950",
        SteamBuildId = "24378492",
        GameRoot = Path.Combine(testRoot, "presentation-game"),
        InstalledAt = DateTimeOffset.UtcNow,
        IsEnabled = false,
        Files =
        [
            new InstalledFileState
            {
                Destination = "Anvil/Content/Paks/Test-Russian.pak",
                SizeBytes = 1,
                Sha256 = new string('c', 64)
            }
        ]
    };
    var presentationDataRoot = Path.Combine(testRoot, "presentation-data");
    using var httpClient = new HttpClient(new OfflineHandler());
    var stateStore = new InstallationStateStore(presentationDataRoot);
    var viewModel = new ProductStateViewModel(
        new ProductViewModel(product),
        product,
        new ProductInstallationCoordinator(httpClient, presentationDataRoot, stateStore),
        new ProductLibraryStore(presentationDataRoot),
        installedState,
        isInLibrary: true,
        detectedGameBuildId: "24378492");

    Assert(viewModel.HasNewerCatalogVersion, "A newer catalog version was not detected.");
    Assert(viewModel.IsUpdateAvailable, "A compatible published update should be available.");
    Assert(viewModel.CanUpdate, "An idle available update should be actionable.");
    Assert(!viewModel.CanInstall, "An installed product should not expose the install action.");
    Assert(viewModel.InstalledVersionLabel == "1.0.0", "Installed and catalog versions were mixed.");
    Assert(viewModel.UpdateVersionText.Contains("1.1.0", StringComparison.Ordinal),
        "The update label does not identify the available version.");
    Assert(viewModel.UpdateActionText == "Обновить до 1.1.0",
        "A newer compatible release should be presented as an update.");
    Assert(viewModel.CatalogStatusText == "СМЕНИТЬ ВЕРСИЮ",
        "The catalog does not expose a recommended version change.");

    var branchProduct = CreateProduct(1, new string('d', 64), "2.0.0") with
    {
        Release = product.Release with
        {
            Version = "2.0.0",
            GameVersion = "steam-build-200",
            Payload = product.Release.Payload! with { Sha256 = new string('d', 64) }
        },
        Releases =
        [
            product.Release with
            {
                Version = "1.0.0",
                GameVersion = "steam-build-100",
                Payload = product.Release.Payload! with { Sha256 = new string('e', 64) }
            }
        ]
    };
    var branchInstalledState = installedState with
    {
        Version = "2.0.0",
        PackageSha256 = new string('d', 64),
        SteamBuildId = "200"
    };
    var branchViewModel = new ProductStateViewModel(
        new ProductViewModel(branchProduct),
        branchProduct,
        new ProductInstallationCoordinator(httpClient, presentationDataRoot, stateStore),
        new ProductLibraryStore(presentationDataRoot),
        branchInstalledState,
        isInLibrary: true,
        detectedGameBuildId: "100");

    Assert(branchViewModel.HasMultipleVersions,
        "A multi-release product should expose a manual version selector.");
    Assert(branchViewModel.SelectedVersion?.Version == "1.0.0",
        "The release matching an older current game build was not selected automatically.");
    Assert(branchViewModel.IsSelectedVersionExact && branchViewModel.CanUpdate,
        "The exact older release should be actionable from the library.");
    Assert(branchViewModel.UpdateActionText == "Переключить на 1.0.0",
        "A recommended older release should be presented as a version switch.");

    branchViewModel.SelectedVersion = branchViewModel.AvailableVersions.Single(option =>
        option.Version == "2.0.0");
    Assert(!branchViewModel.HasSelectedVersionChange && !branchViewModel.CanUpdate,
        "Manually selecting the installed version should remove the switch action.");
    Assert(branchViewModel.IsSelectedVersionUnverified,
        "Manual selection should retain release-specific compatibility information.");
}

void CheckLocalization()
{
    Assert(LocalizationService.DetectLanguage(CultureInfo.GetCultureInfo("ru-RU")) == "ru",
        "Russian system culture was not detected.");
    Assert(LocalizationService.DetectLanguage(CultureInfo.GetCultureInfo("es-MX")) == "es",
        "Spanish regional culture was not detected.");
    Assert(LocalizationService.DetectLanguage(CultureInfo.GetCultureInfo("de-DE")) == "en",
        "Unsupported system culture should fall back to English.");

    var product = new ProductViewModel(CreateProduct(1, new string('a', 64)));
    var languageRefreshes = 0;
    product.PropertyChanged += (_, eventArgs) =>
    {
        if (string.IsNullOrEmpty(eventArgs.PropertyName))
        {
            languageRefreshes++;
        }
    };

    LocalizationService.Instance.SetLanguage("en");
    Assert(LocalizationService.Instance.Get("Nav.Catalog") == "Catalog",
        "English UI resources were not selected.");
    Assert(product.TypeLabel == "Localization",
        "An existing view model did not expose English text.");
    LocalizationService.Instance.SetLanguage("es");
    Assert(LocalizationService.Instance.Get("Nav.Catalog") == "Catálogo",
        "Spanish UI resources were not selected.");
    Assert(product.TypeLabel == "Localización",
        "An existing view model did not refresh to Spanish.");
    LocalizationService.Instance.SetLanguage("ru");
    Assert(LocalizationService.Instance.Get("Nav.Catalog") == "Каталог",
        "Russian UI resources were not selected.");
    Assert(product.TypeLabel == "Локализация" && languageRefreshes == 3,
        "Live UI language changes were not broadcast to existing view models.");
}

void CheckAppSettingsPersistence()
{
    var settingsRoot = Path.Combine(testRoot, "app-settings");
    var store = new AppSettingsStore(settingsRoot);
    var defaultSettings = store.Load();
    Assert(
        defaultSettings.CheckUpdatesAutomatically,
        "Automatic catalog checks should default to enabled.");
    Assert(defaultSettings.UiLanguage is null,
        "A clean install should defer to the system UI language.");

    store.Save(new AppSettings
    {
        CheckUpdatesAutomatically = false,
        UiLanguage = "es"
    });
    var persistedSettings = new AppSettingsStore(settingsRoot).Load();
    Assert(
        !persistedSettings.CheckUpdatesAutomatically,
        "The automatic catalog check setting was not persisted.");
    Assert(persistedSettings.UiLanguage == "es",
        "The selected UI language was not persisted.");

    var legacyRoot = Path.Combine(testRoot, "app-settings-legacy");
    var legacyStore = new AppSettingsStore(legacyRoot);
    Directory.CreateDirectory(Path.GetDirectoryName(legacyStore.SettingsPath)!);
    File.WriteAllText(
        legacyStore.SettingsPath,
        """{ "schemaVersion": 1, "checkUpdatesAutomatically": true }""");
    Assert(legacyStore.Load().UiLanguage is null,
        "Settings written before UI localization should remain loadable.");

    foreach (var (caseName, invalidJson) in new[]
             {
                 ("malformed", "{"),
                 ("unsupported-schema", """{ "schemaVersion": 2, "checkUpdatesAutomatically": true }"""),
                 ("missing-schema", """{ "checkUpdatesAutomatically": true }"""),
                 ("missing-setting", """{ "schemaVersion": 1 }"""),
                 ("unsupported-language", """{ "schemaVersion": 1, "checkUpdatesAutomatically": true, "uiLanguage": "fr" }""")
             })
    {
        var invalidRoot = Path.Combine(testRoot, "app-settings-invalid", caseName);
        var invalidStore = new AppSettingsStore(invalidRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(invalidStore.SettingsPath)!);
        File.WriteAllText(invalidStore.SettingsPath, invalidJson);

        AssertThrows<InvalidDataException>(
            () => invalidStore.Load(),
            $"Invalid settings case '{caseName}' should be rejected for safe startup fallback.");
        Assert(
            File.ReadAllText(invalidStore.SettingsPath) == invalidJson,
            $"Invalid settings case '{caseName}' was modified during load.");
    }
}

void CheckAppDataMigration()
{
    var localRoot = Path.Combine(testRoot, "app-data-migration");
    var legacyRoot = Path.Combine(localRoot, "NFG", "Store");
    var legacyStatePath = Path.Combine(legacyRoot, "state", "library.json");
    var legacyCachePath = Path.Combine(legacyRoot, "cache", "catalog", "catalog.json");
    Directory.CreateDirectory(Path.GetDirectoryName(legacyStatePath)!);
    Directory.CreateDirectory(Path.GetDirectoryName(legacyCachePath)!);
    File.WriteAllText(legacyStatePath, "legacy-library");
    File.WriteAllBytes(legacyCachePath, [0, 1, 2, 3, 255]);

    var resolution = AppDataMigration.Resolve(localRoot);
    var hubRoot = Path.Combine(localRoot, "NFG", "Hub");
    var hubStatePath = Path.Combine(hubRoot, "state", "library.json");
    var hubCachePath = Path.Combine(hubRoot, "cache", "catalog", "catalog.json");

    Assert(resolution.DataRoot == hubRoot,
        "Legacy app data did not resolve to the NFG Hub location.");
    Assert(File.ReadAllText(hubStatePath) == "legacy-library",
        "Legacy library state was not copied into the Hub data directory.");
    Assert(File.ReadAllBytes(hubCachePath).SequenceEqual(new byte[] { 0, 1, 2, 3, 255 }),
        "Legacy catalog cache was not copied byte-for-byte.");
    Assert(File.ReadAllText(legacyStatePath) == "legacy-library" && File.Exists(legacyCachePath),
        "The legacy Store data was modified during migration.");

    File.WriteAllText(hubStatePath, "hub-library");
    var repeatedResolution = AppDataMigration.Resolve(localRoot);
    Assert(repeatedResolution.DataRoot == hubRoot,
        "An existing Hub data directory was not preferred after migration.");
    Assert(File.ReadAllText(hubStatePath) == "hub-library",
        "A repeated migration overwrote current Hub data with legacy data.");
    Assert(File.ReadAllText(legacyStatePath) == "legacy-library",
        "A repeated migration modified the legacy backup.");

    var cleanLocalRoot = Path.Combine(testRoot, "app-data-clean-install");
    var cleanResolution = AppDataMigration.Resolve(cleanLocalRoot);
    Assert(cleanResolution.DataRoot == Path.Combine(cleanLocalRoot, "NFG", "Hub"),
        "A clean installation did not select the NFG Hub data directory.");

    var blockedLocalRoot = Path.Combine(testRoot, "app-data-blocked-migration");
    var blockedNfgRoot = Path.Combine(blockedLocalRoot, "NFG");
    var blockedLegacyRoot = Path.Combine(blockedNfgRoot, "Store");
    var blockedLegacyPath = Path.Combine(blockedLegacyRoot, "state", "library.json");
    Directory.CreateDirectory(Path.GetDirectoryName(blockedLegacyPath)!);
    File.WriteAllText(blockedLegacyPath, "locked-legacy-library");
    using (var lockedLegacyFile = new FileStream(
               blockedLegacyPath,
               FileMode.Open,
               FileAccess.ReadWrite,
               FileShare.None))
    {
        AssertThrows<IOException>(
            () => AppDataMigration.Resolve(blockedLocalRoot),
            "A failed migration should stop startup instead of using the legacy data root.");
    }

    Assert(!Directory.Exists(Path.Combine(blockedNfgRoot, "Hub")),
        "A failed migration published a partial Hub data directory.");
    Assert(File.ReadAllText(blockedLegacyPath) == "locked-legacy-library",
        "A failed migration modified the legacy data.");
    Assert(!Directory.EnumerateDirectories(blockedNfgRoot, ".Hub.migration-*").Any(),
        "A failed migration left a staging directory behind.");
}

async Task CheckCatalogSourcesAsync()
{
    using var mappedClient = new HttpClient(new CatalogHandler(bundledRoot));
    var remoteResult = await new CatalogService(mappedClient).LoadRemoteFirstAsync(
        catalogUri,
        cacheRoot,
        bundledRoot);

    Assert(remoteResult.Source == CatalogSourceKind.Remote, "Expected remote catalog source.");
    var product = remoteResult.Catalog.Products.Single(candidate =>
        candidate.Id == "nfg.anvil-empires.ru");
    Assert(product.SchemaVersion == 1, "Russian localization should remain compatible with schema v1.");
    Assert(product.FamilyId is null, "Schema-v1 Russian localization must not declare familyId.");
    Assert(product.Locale is null, "Schema-v1 Russian localization must not declare locale.");
    Assert(product.ExclusiveGroup is null,
        "Schema-v1 Russian localization must not declare exclusiveGroup.");
    Assert(product.Release.Version == "1.0.1", "Unexpected current localization version.");
    Assert(product.Release.GameVersion == "steam-build-24619810",
        "Current localization targets an unexpected game build.");
    Assert(product.Releases.Count == 1 && product.Releases[0].Version == "1.0.0",
        "Historical localization v1.0.0 was not loaded.");
    Assert(product.Display.Features.Count > 0, "Product features were not loaded.");
    Assert(product.Release.Highlights.Count > 0, "Release highlights were not loaded.");
    Assert(product.Release.KnownIssues.Count > 0, "Known issues were not loaded.");
    Assert(
        product.Release.NotesUrl is { IsAbsoluteUri: true } notesUrl &&
        notesUrl.Scheme == Uri.UriSchemeHttps,
        "Release notes URL was not loaded as absolute HTTPS.");
    Assert(File.Exists(Path.Combine(cacheRoot, "catalog.json")), "Catalog index was not cached.");
    Assert(
        File.Exists(Path.Combine(cacheRoot, "products", "nfg.anvil-empires.ru.json")),
        "Product manifest was not cached.");

    var forgeHelper = remoteResult.Catalog.Products.Single(candidate =>
        candidate.Id == "nfg.anvil-empires.forge-helper");
    Assert(forgeHelper.SchemaVersion == 1, "Forge Helper should remain on product schema v1.");
    Assert(forgeHelper.Type == "mod", "Forge Helper was not loaded as a modification.");
    Assert(
        forgeHelper.Release.Version == "1.0.0",
        "Forge Helper has an unexpected product version.");
    Assert(
        forgeHelper.Release.GameVersion == "steam-build-24619810",
        "Forge Helper targets an unexpected game build.");
    Assert(
        forgeHelper.Release.Payload is { SizeBytes: 30633 },
        "Forge Helper payload metadata was not loaded.");
    Assert(
        forgeHelper.Release.Payload?.Sha256 ==
        "976e4529f97a40516740678b21bf36fcb25141c79981b4e4f95b123cc2791ab7",
        "Forge Helper payload digest was not loaded.");
    Assert(
        File.Exists(Path.Combine(
            cacheRoot,
            "products",
            "nfg.anvil-empires.forge-helper.json")),
        "Forge Helper manifest was not cached.");

    using var offlineClient = new HttpClient(new OfflineHandler());
    var cacheResult = await new CatalogService(offlineClient).LoadRemoteFirstAsync(
        catalogUri,
        cacheRoot,
        bundledRoot);
    Assert(cacheResult.Source == CatalogSourceKind.Cache, "Expected cached catalog fallback.");

    using var unexpectedNetworkClient = new HttpClient(new UnexpectedNetworkHandler());
    var localResult = await new CatalogService(unexpectedNetworkClient).LoadLocalFirstAsync(
        catalogUri,
        cacheRoot,
        bundledRoot);
    Assert(
        localResult.Source == CatalogSourceKind.Cache,
        "Disabled automatic checks should load the validated cache without network access.");
    await AssertThrowsAsync<ArgumentException>(
        () => new CatalogService(unexpectedNetworkClient).LoadLocalFirstAsync(
            new Uri("http://catalog.test/catalog.json"),
            cacheRoot,
            bundledRoot),
        "Local-only catalog loading should enforce the same HTTPS URI contract.");
    await AssertThrowsAsync<CatalogValidationException>(
        () => new CatalogService(unexpectedNetworkClient).LoadLocalFirstAsync(
            catalogUri,
            cacheRoot,
            bundledRoot,
            requiredProductIds: new HashSet<string>(StringComparer.Ordinal)
            {
                "nfg.test.missing-library-product"
            }),
        "A catalog source must not silently drop a product retained in the local library.");

    var bundledResult = await new CatalogService(offlineClient).LoadRemoteFirstAsync(
        catalogUri,
        emptyCacheRoot,
        bundledRoot);
    Assert(bundledResult.Source == CatalogSourceKind.Bundled, "Expected bundled catalog fallback.");
}

async Task CheckRichCatalogValidationAsync()
{
    var product = CreateProduct(1, new string('a', 64));
    var productJson = SerializeProduct(product);
    var legacyProduct = JsonNode.Parse(productJson)?.AsObject()
        ?? throw new InvalidOperationException("Could not construct legacy product JSON.");
    legacyProduct["display"]!.AsObject().Remove("features");
    legacyProduct["release"]!.AsObject().Remove("highlights");
    legacyProduct["release"]!.AsObject().Remove("knownIssues");
    legacyProduct["release"]!.AsObject().Remove("notesUrl");

    var legacyCatalog = await LoadCatalogCaseAsync("legacy-rich-fields", legacyProduct.ToJsonString());
    var legacy = legacyCatalog.Products.Single();
    Assert(legacy.Display.Features.Count == 0, "Missing features should default to an empty list.");
    Assert(legacy.Release.Highlights.Count == 0, "Missing highlights should default to an empty list.");
    Assert(legacy.Release.KnownIssues.Count == 0, "Missing known issues should default to an empty list.");
    Assert(legacy.Release.NotesUrl is null, "Missing release notes URL should default to null.");

    await AssertCatalogRejectedAsync(
        "blank-feature",
        product with { Display = product.Display with { Features = ["Valid", " "] } },
        "A blank feature should be rejected.");
    await AssertCatalogRejectedAsync(
        "blank-highlight",
        product with { Release = product.Release with { Highlights = [""] } },
        "A blank release highlight should be rejected.");
    await AssertCatalogRejectedAsync(
        "blank-known-issue",
        product with { Release = product.Release with { KnownIssues = ["\t"] } },
        "A blank known issue should be rejected.");
    await AssertCatalogRejectedAsync(
        "too-many-features",
        product with
        {
            Display = product.Display with
            {
                Features = Enumerable.Range(1, 101).Select(index => $"Feature {index}").ToArray()
            }
        },
        "An unreasonable feature count should be rejected.");
    await AssertCatalogRejectedAsync(
        "insecure-notes-url",
        product with
        {
            Release = product.Release with { NotesUrl = new Uri("http://catalog.test/release") }
        },
        "A non-HTTPS release notes URL should be rejected.");
    await AssertCatalogRejectedAsync(
        "duplicate-release-version",
        product with { Releases = [product.Release] },
        "A release version duplicated between 'release' and 'releases' should be rejected.");
    await AssertCatalogRejectedAsync(
        "invalid-release-game-version",
        product with
        {
            Releases =
            [
                product.Release with
                {
                    Version = "0.9.0",
                    GameVersion = "build-123"
                }
            ]
        },
        "A release-specific game version outside the Steam BuildID contract should be rejected.");

    var nullRelease = JsonNode.Parse(productJson)?.AsObject()
        ?? throw new InvalidOperationException("Could not construct null-release product JSON.");
    nullRelease["release"] = null;
    await AssertThrowsAsync<CatalogValidationException>(
        () => LoadCatalogCaseAsync("null-release", nullRelease.ToJsonString()),
        "A structurally null required product object should be rejected as catalog validation.");
}

async Task CheckProductSchemaV2ValidationAsync()
{
    var legacyProduct = CreateProduct(1, new string('a', 64));
    var serializedLegacyProduct = JsonNode.Parse(SerializeProduct(legacyProduct))?.AsObject()
        ?? throw new InvalidOperationException("Could not construct schema-v1 product JSON.");
    Assert(!serializedLegacyProduct.ContainsKey("familyId"),
        "Schema-v1 serialization should omit familyId.");
    Assert(!serializedLegacyProduct.ContainsKey("locale"),
        "Schema-v1 serialization should omit locale.");
    Assert(!serializedLegacyProduct.ContainsKey("exclusiveGroup"),
        "Schema-v1 serialization should omit exclusiveGroup.");

    var legacyCatalog = await LoadCatalogProductsCaseAsync("schema-v1-compatible", legacyProduct);
    var loadedLegacyProduct = legacyCatalog.Products.Single();
    Assert(loadedLegacyProduct.FamilyId is null, "Schema-v1 familyId should default to null.");
    Assert(loadedLegacyProduct.Locale is null, "Schema-v1 locale should default to null.");
    Assert(loadedLegacyProduct.ExclusiveGroup is null,
        "Schema-v1 exclusiveGroup should default to null.");
    await LoadCatalogProductsCaseAsync(
        "schema-v1-uppercase-id-compatible",
        legacyProduct with { Id = "Nfg.Test.Localization" });
    var legacyLooseIdentifiers = legacyProduct with
    {
        Id = "nfg.test_legacy.localization",
        Dependencies =
        [
            new ProductDependency
            {
                ProductId = "Legacy_Dependency",
                VersionRange = "*"
            }
        ]
    };
    var legacyLooseCatalog = await LoadCatalogJsonCaseAsync(
        "schema-v1-legacy-identifiers-compatible",
        [SerializeProduct(legacyLooseIdentifiers)],
        catalogId: "Legacy_Catalog");
    var loadedLegacyLooseProduct = legacyLooseCatalog.Products.Single();
    Assert(loadedLegacyLooseProduct.Id == legacyLooseIdentifiers.Id,
        "Schema-v1 product ids accepted by the legacy contract must remain valid.");
    Assert(loadedLegacyLooseProduct.Dependencies.Single().ProductId == "Legacy_Dependency",
        "Schema-v1 dependency ids accepted by the legacy contract must remain valid.");

    var russian = CreateVariantProduct("nfg.test.localization.ru", "ru");
    var legacyModification = legacyProduct with
    {
        Id = "nfg.test.mod",
        Type = "mod"
    };
    var mixedCatalog = await LoadCatalogProductsCaseAsync(
        "schema-v2-mixed-with-v1",
        russian,
        legacyModification);
    var loadedRussian = mixedCatalog.Products.Single(product => product.Id == russian.Id);
    Assert(loadedRussian.SchemaVersion == 2, "Schema-v2 product version was not retained.");
    Assert(loadedRussian.FamilyId == "nfg.test.localization", "Schema-v2 familyId was not loaded.");
    Assert(loadedRussian.Locale == "ru", "Schema-v2 locale was not loaded.");
    Assert(loadedRussian.ExclusiveGroup == "nfg.test.game-language",
        "Schema-v2 exclusiveGroup was not loaded.");

    var schemaV2Modification = legacyModification with { SchemaVersion = 2 };
    await LoadCatalogProductsCaseAsync("schema-v2-non-localization-without-variant", schemaV2Modification);

    string[] validLocales =
    [
        "pt-BR",
        "zh-Hant-TW",
        "sl-rozaj-biske-1994",
        "de-DE-u-co-phonebk",
        "x-private",
        "i-klingon"
    ];
    await LoadCatalogProductsCaseAsync(
        "valid-bcp47-locales",
        validLocales
            .Select((locale, index) => CreateVariantProduct(
                $"nfg.test.localization.locale-{index}",
                locale))
            .ToArray());

    await AssertCatalogRejectedAsync(
        "unsupported-product-schema-v3",
        legacyProduct with { SchemaVersion = 3 },
        "An unsupported product schema should be rejected.");
    await AssertCatalogRejectedAsync(
        "schema-v1-with-variant-metadata",
        legacyProduct with
        {
            FamilyId = russian.FamilyId,
            Locale = russian.Locale,
            ExclusiveGroup = russian.ExclusiveGroup
        },
        "Schema-v1 products must not declare schema-v2 variant metadata.");
    await AssertCatalogRejectedAsync(
        "schema-v2-localization-without-variant-metadata",
        legacyProduct with { SchemaVersion = 2 },
        "Schema-v2 localization products must declare variant metadata.");

    foreach (var (caseName, incompleteProduct) in new[]
             {
                 (
                     "schema-v2-missing-family",
                     russian with { FamilyId = null }),
                 (
                     "schema-v2-missing-locale",
                     russian with { Locale = null }),
                 (
                     "schema-v2-missing-exclusive-group",
                     russian with { ExclusiveGroup = null }),
                 (
                     "schema-v2-non-localization-partial-variant",
                     schemaV2Modification with { FamilyId = "nfg.test.localization" })
             })
    {
        await AssertCatalogRejectedAsync(
            caseName,
            incompleteProduct,
            $"Incomplete schema-v2 variant metadata case '{caseName}' should be rejected.");
    }

    foreach (var (caseName, invalidProduct) in new[]
             {
                 (
                     "unsafe-product-id",
                     russian with { Id = "nfg.test/unsafe" }),
                 (
                     "empty-product-id",
                     russian with { Id = "" }),
                 (
                     "empty-family-id",
                     russian with { FamilyId = "" }),
                 (
                     "unsafe-family-id",
                     russian with { FamilyId = "../nfg.test.localization" }),
                 (
                     "unsafe-exclusive-group",
                     russian with { ExclusiveGroup = "nfg.test.game_language" }),
                 (
                     "empty-exclusive-group",
                     russian with { ExclusiveGroup = "" }),
                 (
                     "unsafe-dependency-id",
                     russian with
                     {
                         Dependencies =
                         [
                             new ProductDependency
                             {
                                 ProductId = "legacy_dependency",
                                 VersionRange = "*"
                             }
                         ]
                     })
             })
    {
        await AssertCatalogRejectedAsync(
            caseName,
            invalidProduct,
            $"Unsafe stable id case '{caseName}' should be rejected.");
    }

    foreach (var invalidLocale in new[] { "ru_RU", "ru--RU", "r", "en-u", "en-variant-variant" })
    {
        await AssertCatalogRejectedAsync(
            $"invalid-locale-{invalidLocale.Replace('_', '-').Replace("--", "-")}",
            russian with { Locale = invalidLocale },
            $"Invalid BCP-47 locale '{invalidLocale}' should be rejected.");
    }

    await AssertCatalogSetRejectedAsync(
        "duplicate-locale-case-insensitive",
        [
            russian,
            CreateVariantProduct(
                "nfg.test.localization.ru-alternate",
                "RU",
                familyId: "NFG.TEST.LOCALIZATION",
                exclusiveGroup: "NFG.TEST.GAME-LANGUAGE")
        ],
        "Locales must be unique case-insensitively within one family.");

    await LoadCatalogProductsCaseAsync(
        "same-locale-in-different-families",
        russian,
        CreateVariantProduct(
            "nfg.test.other-family.ru",
            "RU",
            familyId: "nfg.test.other-localization"));

    await AssertCatalogSetRejectedAsync(
        "duplicate-locale-same-family-different-group",
        [
            russian,
            CreateVariantProduct(
                "nfg.test.localization.ru-other-slot",
                "RU",
                exclusiveGroup: "nfg.test.other-game-language")
        ],
        "A locale must remain unique within its family even when exclusive groups differ.");
    await AssertCatalogSetRejectedAsync(
        "same-family-different-groups-different-locales",
        [
            russian,
            CreateVariantProduct(
                "nfg.test.localization.en-other-slot",
                "en",
                exclusiveGroup: "nfg.test.other-game-language")
        ],
        "All variants in one family must share one exclusive group.");
    await AssertCatalogSetRejectedAsync(
        "same-family-exclusive-group-case-alias",
        [
            russian,
            CreateVariantProduct(
                "nfg.test.localization.es",
                "es",
                exclusiveGroup: "NFG.TEST.GAME-LANGUAGE")
        ],
        "Installation slot ids must use stable, identical casing.");

    var anchoredRussian = CreateVariantProduct(
        "nfg.anvil-empires.ru",
        "ru",
        familyId: "nfg.anvil-empires.localization",
        exclusiveGroup: "nfg.anvil-empires.ru");
    var anchoredSpanish = CreateVariantProduct(
        "nfg.anvil-empires.es",
        "es",
        familyId: "nfg.anvil-empires.localization",
        exclusiveGroup: "nfg.anvil-empires.ru");
    var anchoredCatalog = await LoadCatalogProductsCaseAsync(
        "exclusive-group-product-id-self-anchor",
        anchoredRussian,
        anchoredSpanish);
    Assert(anchoredCatalog.Products.Count == 2,
        "An exclusiveGroup may equal the product id of a member in that same slot.");

    await AssertCatalogSetRejectedAsync(
        "exclusive-group-collides-with-standalone-product-id",
        [
            russian,
            schemaV2Modification with { Id = "nfg.test.game-language" }
        ],
        "An exclusiveGroup must not collide with a standalone product id.");
    await AssertCatalogSetRejectedAsync(
        "exclusive-group-collides-with-standalone-product-id-case-alias",
        [
            russian,
            schemaV2Modification with { Id = "NFG.TEST.GAME-LANGUAGE" }
        ],
        "A case alias of an exclusiveGroup must not collide with a standalone product id.");
    await AssertCatalogSetRejectedAsync(
        "exclusive-group-collides-with-other-slot-product-id",
        [
            russian,
            CreateVariantProduct(
                "nfg.test.game-language",
                "en",
                familyId: "nfg.test.other-localization",
                exclusiveGroup: "nfg.test.other-game-language")
        ],
        "An exclusiveGroup must not collide with a product id belonging to another slot.");
    await AssertCatalogSetRejectedAsync(
        "exclusive-group-collides-with-other-slot-product-id-case-alias",
        [
            russian,
            CreateVariantProduct(
                "NFG.TEST.GAME-LANGUAGE",
                "en",
                familyId: "nfg.test.other-localization",
                exclusiveGroup: "nfg.test.other-game-language")
        ],
        "A case alias of an exclusiveGroup must not collide with another slot's product id.");

    await AssertCatalogSetRejectedAsync(
        "same-family-different-types",
        [
            russian,
            CreateVariantProduct("nfg.test.localization.es", "es") with { Type = "mod" }
        ],
        "All variants in one family must use compatible product types.");
    var mismatchedTitle = CreateVariantProduct("nfg.test.localization.es", "es");
    await AssertCatalogSetRejectedAsync(
        "same-family-different-titles",
        [
            russian,
            mismatchedTitle with
            {
                Display = mismatchedTitle.Display with { Title = "Different title" }
            }
        ],
        "All variants in one family must use the same display title.");

    await AssertCatalogSetRejectedAsync(
        "exclusive-group-different-steam-app",
        [
            russian,
            CreateVariantProduct("nfg.test.localization.en", "en", steamAppId: "999999")
        ],
        "Products in one exclusive group must share one Steam app id.");
    await AssertCatalogSetRejectedAsync(
        "exclusive-group-different-strategy",
        [
            russian,
            CreateVariantProduct(
                "nfg.test.localization.en",
                "en",
                installationStrategy: "other-managed-files")
        ],
        "Products in one exclusive group must use compatible installation strategies.");
    await AssertCatalogRejectedAsync(
        "exclusive-group-without-steam-app",
        russian with
        {
            Installation = russian.Installation with { Detection = [] }
        },
        "A variant in an exclusive group must declare one Steam app id.");
    await AssertCatalogRejectedAsync(
        "exclusive-group-with-nonnumeric-steam-app",
        russian with
        {
            Installation = russian.Installation with
            {
                Detection =
                [
                    new ProductDetectionRule { Provider = "steam", ProductId = "not-numeric" }
                ]
            }
        },
        "A variant Steam app id must be numeric.");
    await AssertCatalogRejectedAsync(
        "exclusive-group-with-multiple-steam-apps",
        russian with
        {
            Installation = russian.Installation with
            {
                Detection =
                [
                    new ProductDetectionRule { Provider = "steam", ProductId = "2383950" },
                    new ProductDetectionRule { Provider = "steam", ProductId = "999999" }
                ]
            }
        },
        "A variant in an exclusive group must not declare multiple Steam app ids.");
}

async Task AssertCatalogRejectedAsync(
    string caseName,
    ProductManifest product,
    string message) =>
    await AssertThrowsAsync<CatalogValidationException>(
        () => LoadCatalogCaseAsync(caseName, SerializeProduct(product)),
        message);

async Task AssertCatalogSetRejectedAsync(
    string caseName,
    IReadOnlyList<ProductManifest> products,
    string message) =>
    await AssertThrowsAsync<CatalogValidationException>(
        () => LoadCatalogProductsCaseAsync(caseName, products.ToArray()),
        message);

async Task<StoreCatalog> LoadCatalogCaseAsync(string caseName, string productJson)
    => await LoadCatalogJsonCaseAsync(caseName, [productJson]);

async Task<StoreCatalog> LoadCatalogProductsCaseAsync(
    string caseName,
    params ProductManifest[] products) =>
    await LoadCatalogJsonCaseAsync(caseName, products.Select(SerializeProduct).ToArray());

async Task<StoreCatalog> LoadCatalogJsonCaseAsync(
    string caseName,
    IReadOnlyList<string> productJsons,
    string catalogId = "nfg.test")
{
    var root = Path.Combine(testRoot, "catalog-validation", caseName);
    var productsRoot = Path.Combine(root, "products");
    Directory.CreateDirectory(productsRoot);

    var productPaths = productJsons
        .Select((_, index) => $"products/test-{index}.json")
        .ToArray();

    await File.WriteAllTextAsync(
        Path.Combine(root, "catalog.json"),
        JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                catalogId,
                displayName = "Test catalog",
                products = productPaths
            }));
    for (var index = 0; index < productJsons.Count; index++)
    {
        await File.WriteAllTextAsync(
            Path.Combine(productsRoot, $"test-{index}.json"),
            productJsons[index]);
    }

    return await new CatalogService().LoadAsync(root);
}

async Task CheckProductLibraryStoreAsync()
{
    var root = Path.Combine(testRoot, "product-library");
    var libraryPath = Path.Combine(root, "state", "library.json");
    var store = new ProductLibraryStore(root);

    var empty = await store.LoadAsync();
    Assert(empty.Count == 0, "A missing product library should load as empty.");
    Assert(!File.Exists(libraryPath), "Loading an empty product library should not create state.");

    await store.AddAsync("nfg.test.alpha");
    await store.AddAsync("nfg.test.alpha");
    Assert(File.Exists(libraryPath), "Adding a product did not create library.json.");

    var reloaded = await new ProductLibraryStore(root).LoadAsync();
    Assert(
        reloaded.Count == 1 && reloaded.Contains("nfg.test.alpha"),
        "Product library add should be persistent and idempotent.");

    var concurrentIds = Enumerable.Range(0, 64)
        .Select(index => $"nfg.test.concurrent_{index:D2}")
        .ToArray();
    await Task.WhenAll(concurrentIds.Select(productId =>
        new ProductLibraryStore(root).AddAsync(productId)));

    var afterConcurrentAdds = await store.LoadAsync();
    Assert(
        afterConcurrentAdds.Count == concurrentIds.Length + 1 &&
        concurrentIds.All(afterConcurrentAdds.Contains),
        "Concurrent library additions lost a read-modify-write update.");

    var removedIds = concurrentIds.Where((_, index) => index % 2 == 0).ToArray();
    await Task.WhenAll(removedIds.Select(productId =>
        new ProductLibraryStore(root).RemoveAsync(productId)));
    await store.RemoveAsync("nfg.test.missing");
    await store.RemoveAsync(removedIds[0]);

    var afterConcurrentRemoves = await new ProductLibraryStore(root).LoadAsync();
    Assert(
        afterConcurrentRemoves.Count == concurrentIds.Length - removedIds.Length + 1 &&
        removedIds.All(productId => !afterConcurrentRemoves.Contains(productId)),
        "Concurrent or repeated library removal produced an invalid membership set.");
    Assert(
        !Directory.EnumerateFiles(Path.GetDirectoryName(libraryPath)!, "library.json.*.tmp").Any(),
        "Atomic library writes left temporary files behind.");

    await AssertThrowsAsync<ProductLibraryException>(
        () => store.AddAsync("../outside"),
        "A path-like product id should be rejected on add.");
    await AssertThrowsAsync<ProductLibraryException>(
        () => store.RemoveAsync(" "),
        "A blank product id should be rejected on removal.");
    await AssertThrowsAsync<ProductLibraryException>(
        () => store.AddAsync("nfg.test.кириллица"),
        "A non-ASCII product id should be rejected on add.");

    await AssertProductLibraryJsonRejectedAsync(
        "malformed-json",
        "{");
    await AssertProductLibraryJsonRejectedAsync(
        "unsupported-schema",
        """
        { "schemaVersion": 2, "productIds": [] }
        """);
    await AssertProductLibraryJsonRejectedAsync(
        "missing-members",
        """
        { "schemaVersion": 1 }
        """);
    await AssertProductLibraryJsonRejectedAsync(
        "unknown-member",
        """
        { "schemaVersion": 1, "productIds": [], "unexpected": true }
        """);
    await AssertProductLibraryJsonRejectedAsync(
        "duplicate-member",
        """
        { "schemaVersion": 1, "schemaVersion": 1, "productIds": [] }
        """);
    await AssertProductLibraryJsonRejectedAsync(
        "duplicate-product",
        """
        { "schemaVersion": 1, "productIds": ["nfg.test.alpha", "nfg.test.alpha"] }
        """);
    await AssertProductLibraryJsonRejectedAsync(
        "invalid-persisted-id",
        """
        { "schemaVersion": 1, "productIds": ["../outside"] }
        """);
}

async Task AssertProductLibraryJsonRejectedAsync(string caseName, string json)
{
    var root = Path.Combine(testRoot, "product-library-invalid", caseName);
    var stateRoot = Path.Combine(root, "state");
    var libraryPath = Path.Combine(stateRoot, "library.json");
    Directory.CreateDirectory(stateRoot);
    await File.WriteAllTextAsync(libraryPath, json);

    var store = new ProductLibraryStore(root);
    await AssertThrowsAsync<ProductLibraryException>(
        () => store.LoadAsync(),
        $"Invalid product library case '{caseName}' should be rejected on load.");
    await AssertThrowsAsync<ProductLibraryException>(
        () => store.AddAsync("nfg.test.valid"),
        $"Invalid product library case '{caseName}' should not be overwritten on add.");
    Assert(
        await File.ReadAllTextAsync(libraryPath) == json,
        $"Invalid product library case '{caseName}' was modified after rejection.");
}

static string SerializeProduct(ProductManifest product) =>
    JsonSerializer.Serialize(
        product,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

async Task CheckPackageDownloadAndValidationAsync()
{
    var packageBytes = BuildPackageBytes();
    var packageHash = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
    var product = CreateProduct(packageBytes.LongLength, packageHash);
    var downloadsRoot = Path.Combine(testRoot, "downloads");

    using var packageClient = new HttpClient(new BytesHandler(packageBytes));
    var downloadedPath = await new PackageDownloader(packageClient).DownloadAsync(
        product,
        downloadsRoot);
    Assert(File.Exists(downloadedPath), "Package was not downloaded.");

    var validated = await new PackageArchiveService().ValidateAsync(downloadedPath, product);
    Assert(validated.Manifest.Schema == "nfg-package/1", "Unexpected package schema.");
    Assert(validated.Files.Count == 1, "Unexpected package file count.");
    Assert(
        validated.Files[0].DestinationRelativePath == "Anvil/Content/Paks/Test-Russian.pak",
        "Unexpected package destination.");

    await CheckManagedInstallationAsync(validated, product);

    var traversalBytes = BuildPackageBytes("steam-game/../outside.pak");
    var traversalHash = Convert.ToHexString(SHA256.HashData(traversalBytes)).ToLowerInvariant();
    var traversalProduct = CreateProduct(traversalBytes.LongLength, traversalHash);
    var traversalPath = Path.Combine(testRoot, "traversal.zip");
    await File.WriteAllBytesAsync(traversalPath, traversalBytes);

    await AssertThrowsAsync<PackageValidationException>(
        () => new PackageArchiveService().ValidateAsync(traversalPath, traversalProduct),
        "Traversal destination should be rejected.");
}

async Task CheckManagedInstallationAsync(ValidatedPackage package, ProductManifest product)
{
    var gameRoot = Path.Combine(testRoot, "managed-game");
    var targetDirectory = Path.Combine(gameRoot, "Anvil", "Content", "Paks");
    var targetPath = Path.Combine(targetDirectory, "Test-Russian.pak");
    var disabledPath = $"{targetPath}.nfg-disabled";
    Directory.CreateDirectory(targetDirectory);

    var managedDataRoot = Path.Combine(testRoot, "managed-store-data");
    var stateStore = new InstallationStateStore(managedDataRoot);
    var installer = new ManagedFilesInstaller(stateStore);
    var installed = await installer.InstallAsync(package, product, gameRoot);
    Assert(installed.Outcome == ManagedInstallOutcome.Installed, "Expected a fresh managed install.");
    Assert(installed.State.IsEnabled, "Fresh installation should be enabled.");
    Assert(File.ReadAllBytes(targetPath).SequenceEqual(GetSyntheticPayload()), "Installed file differs from package.");

    var statePath = Path.Combine(
        managedDataRoot,
        "state",
        "installations",
        $"{product.Id}.json");
    await File.WriteAllTextAsync(
        statePath,
        JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            productId = installed.State.ProductId,
            version = installed.State.Version,
            packageSizeBytes = installed.State.PackageSizeBytes,
            packageSha256 = installed.State.PackageSha256,
            steamAppId = installed.State.SteamAppId,
            steamBuildId = installed.State.SteamBuildId,
            gameRoot = installed.State.GameRoot,
            installedAt = installed.State.InstalledAt,
            files = installed.State.Files.Select(file => new
            {
                destination = file.Destination,
                sizeBytes = file.SizeBytes,
                sha256 = file.Sha256
            })
        }));
    var migratedLegacyState = await stateStore.LoadAsync(product.Id);
    Assert(
        migratedLegacyState?.IsEnabled == true,
        "Legacy installation state should default to enabled.");
    await new InstallationStateMigrator(stateStore).MigrateAsync(
        new StoreCatalog("nfg.test", "Test", [product]));

    var repeated = await installer.InstallAsync(package, product, gameRoot);
    Assert(repeated.Outcome == ManagedInstallOutcome.AlreadyInstalled, "Expected idempotent install.");

    var disabled = await installer.SetEnabledAsync(product.Id, isEnabled: false);
    Assert(!disabled.IsEnabled, "Disabled installation state was not saved.");
    Assert(!File.Exists(targetPath), "Active managed file remained after disabling.");
    Assert(File.Exists(disabledPath), "Disabled managed file was not preserved.");
    Assert(
        !File.Exists(targetPath) && File.ReadAllBytes(disabledPath).SequenceEqual(GetSyntheticPayload()),
        "Disabling changed the managed file.");

    var repeatedDisable = await installer.SetEnabledAsync(product.Id, isEnabled: false);
    Assert(!repeatedDisable.IsEnabled, "Repeated disable should be idempotent.");

    var reloadedStateStore = new InstallationStateStore(managedDataRoot);
    var reloadedInstaller = new ManagedFilesInstaller(reloadedStateStore);
    var enabled = await reloadedInstaller.SetEnabledAsync(product.Id, isEnabled: true);
    Assert(enabled.IsEnabled, "Enabled installation state was not saved.");
    Assert(File.Exists(targetPath), "Managed file was not restored when enabled.");
    Assert(!File.Exists(disabledPath), "Disabled copy remained after enabling.");

    var repeatedEnable = await reloadedInstaller.SetEnabledAsync(product.Id, isEnabled: true);
    Assert(repeatedEnable.IsEnabled, "Repeated enable should be idempotent.");

    await reloadedInstaller.SetEnabledAsync(product.Id, isEnabled: false);
    await reloadedInstaller.UninstallAsync(product.Id);
    Assert(!File.Exists(targetPath), "Managed file was not removed.");
    Assert(!File.Exists(disabledPath), "Disabled managed file was not removed.");
    Assert(await reloadedStateStore.LoadAsync(product.Id) is null, "Installation state was not removed.");

    File.WriteAllBytes(targetPath, GetSyntheticPayload());
    var adoptionStore = new InstallationStateStore(Path.Combine(testRoot, "adoption-store-data"));
    var adoptionInstaller = new ManagedFilesInstaller(adoptionStore);
    var adoption = await adoptionInstaller.InstallAsync(
        package,
        product,
        gameRoot);
    Assert(adoption.Outcome == ManagedInstallOutcome.Adopted, "Expected exact-file adoption.");

    var disabledConflict = Encoding.UTF8.GetBytes("foreign disabled file");
    File.WriteAllBytes(disabledPath, disabledConflict);
    await AssertThrowsAsync<ManagedFilesInstallException>(
        () => adoptionInstaller.SetEnabledAsync(product.Id, isEnabled: false),
        "A conflicting disabled path should block deactivation.");
    Assert(
        File.ReadAllBytes(targetPath).SequenceEqual(GetSyntheticPayload()),
        "Active file was changed after a disabled-path conflict.");
    Assert(
        File.ReadAllBytes(disabledPath).SequenceEqual(disabledConflict),
        "Conflicting disabled file was changed.");

    var conflictRoot = Path.Combine(testRoot, "conflict-game");
    var conflictDirectory = Path.Combine(conflictRoot, "Anvil", "Content", "Paks");
    var conflictPath = Path.Combine(conflictDirectory, "Test-Russian.pak");
    Directory.CreateDirectory(conflictDirectory);
    var conflictingContents = Encoding.UTF8.GetBytes("foreign file");
    File.WriteAllBytes(conflictPath, conflictingContents);
    var conflictStore = new InstallationStateStore(Path.Combine(testRoot, "conflict-store-data"));

    await AssertThrowsAsync<ManagedFilesInstallException>(
        () => new ManagedFilesInstaller(conflictStore).InstallAsync(
            package,
            product,
            conflictRoot),
        "An unowned conflicting file should block installation.");
    Assert(
        File.ReadAllBytes(conflictPath).SequenceEqual(conflictingContents),
        "Conflicting file was modified.");
}

async Task CheckManagedUpdateAsync()
{
    var oldPayload = Encoding.UTF8.GetBytes("managed update payload 1.0.0");
    var oldPackageBytes = BuildPackageBytes(version: "1.0.0", payload: oldPayload);
    var oldProduct = CreateProduct(
        oldPackageBytes.LongLength,
        Convert.ToHexString(SHA256.HashData(oldPackageBytes)).ToLowerInvariant(),
        "1.0.0");
    var oldArchivePath = Path.Combine(testRoot, "managed-update-1.0.0.zip");
    await File.WriteAllBytesAsync(oldArchivePath, oldPackageBytes);
    var oldPackage = await new PackageArchiveService().ValidateAsync(
        oldArchivePath,
        oldProduct);

    var newPayload = Encoding.UTF8.GetBytes("managed update payload 1.1.0");
    var newPackageBytes = BuildPackageBytes(version: "1.1.0", payload: newPayload);
    var newProduct = CreateProduct(
        newPackageBytes.LongLength,
        Convert.ToHexString(SHA256.HashData(newPackageBytes)).ToLowerInvariant(),
        "1.1.0");
    var newArchivePath = Path.Combine(testRoot, "managed-update-1.1.0.zip");
    await File.WriteAllBytesAsync(newArchivePath, newPackageBytes);
    var newPackage = await new PackageArchiveService().ValidateAsync(
        newArchivePath,
        newProduct);

    var gameRoot = Path.Combine(testRoot, "managed-update-game");
    var targetDirectory = Path.Combine(gameRoot, "Anvil", "Content", "Paks");
    var targetPath = Path.Combine(targetDirectory, "Test-Russian.pak");
    var disabledPath = $"{targetPath}.nfg-disabled";
    Directory.CreateDirectory(targetDirectory);

    var dataRoot = Path.Combine(testRoot, "managed-update-data");
    var stateStore = new InstallationStateStore(dataRoot);
    var installer = new ManagedFilesInstaller(stateStore);
    const string detectedBuildBeforeSwitch = "30000000";
    await installer.InstallAsync(
        oldPackage,
        oldProduct,
        gameRoot,
        detectedBuildBeforeSwitch);
    await installer.SetEnabledAsync(oldProduct.Id, isEnabled: false);

    var updated = await installer.SwitchVersionAsync(newPackage, newProduct, gameRoot);
    Assert(updated.Outcome == ManagedInstallOutcome.Updated, "Expected a version switch outcome.");
    Assert(updated.State.Version == "1.1.0", "Switched state has the wrong newer version.");
    Assert(!updated.State.IsEnabled, "A version switch should preserve the disabled product state.");
    Assert(
        updated.State.DetectedSteamBuildId == detectedBuildBeforeSwitch,
        "A version switch without a new detection lost the detected Steam build.");
    Assert(!File.Exists(targetPath), "A disabled version switch unexpectedly activated the product.");
    Assert(
        File.ReadAllBytes(disabledPath).SequenceEqual(newPayload),
        "An upward version switch did not replace the disabled product payload.");

    const string detectedBuildAfterGameRollback = "20000000";
    var downgraded = await installer.SwitchVersionAsync(
        oldPackage,
        oldProduct,
        gameRoot,
        detectedBuildAfterGameRollback);
    Assert(downgraded.Outcome == ManagedInstallOutcome.Updated,
        "Expected a downward version switch outcome.");
    Assert(downgraded.State.Version == "1.0.0",
        "A downward version switch did not restore the selected version.");
    Assert(!downgraded.State.IsEnabled,
        "A downward version switch should preserve the disabled product state.");
    Assert(downgraded.State.DetectedSteamBuildId == detectedBuildAfterGameRollback,
        "A downward version switch did not save the newly detected Steam build.");
    Assert(!File.Exists(targetPath),
        "A downward switch unexpectedly activated the disabled product.");
    Assert(
        File.ReadAllBytes(disabledPath).SequenceEqual(oldPayload),
        "A downward version switch did not restore the selected package payload.");
    Assert(
        !Directory.Exists(Path.Combine(dataRoot, "state", "transactions")) ||
        !Directory.EnumerateFiles(
            Path.Combine(dataRoot, "state", "transactions"),
            "*.update.json").Any(),
        "A successful round-trip version switch left a transaction journal behind.");

    var operationLockRoot = Path.Combine(dataRoot, "state", "locks");
    Directory.CreateDirectory(operationLockRoot);
    var operationLockPath = Path.Combine(operationLockRoot, $"{oldProduct.Id}.lock");
    await using (var heldOperationLock = new FileStream(
                     operationLockPath,
                     FileMode.OpenOrCreate,
                     FileAccess.ReadWrite,
                     FileShare.None))
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await AssertThrowsAsync<OperationCanceledException>(
            () => installer.SetEnabledAsync(oldProduct.Id, isEnabled: true, cancellation.Token),
            "A product operation should wait for the cross-process lock.");
    }
    Assert(
        !(await stateStore.LoadAsync(oldProduct.Id))!.IsEnabled,
        "A cancelled lock wait changed the product state.");

    var transactionsRoot = Path.Combine(dataRoot, "state", "transactions");
    Directory.CreateDirectory(transactionsRoot);
    var unrelatedBrokenJournalPath = Path.Combine(
        transactionsRoot,
        "nfg.test.unrelated.update.json");
    await File.WriteAllTextAsync(unrelatedBrokenJournalPath, "{");
    var enabledWithUnrelatedBrokenJournal = await installer.SetEnabledAsync(
        oldProduct.Id,
        isEnabled: true);
    Assert(
        enabledWithUnrelatedBrokenJournal.IsEnabled,
        "An unrelated broken journal blocked a product-scoped operation.");
    File.Delete(unrelatedBrokenJournalPath);
    await installer.SetEnabledAsync(oldProduct.Id, isEnabled: false);

    var rollbackGameRoot = Path.Combine(testRoot, "managed-update-rollback-game");
    var rollbackTargetDirectory = Path.Combine(
        rollbackGameRoot,
        "Anvil",
        "Content",
        "Paks");
    var rollbackTargetPath = Path.Combine(rollbackTargetDirectory, "Test-Russian.pak");
    Directory.CreateDirectory(rollbackTargetDirectory);
    var rollbackDataRoot = Path.Combine(testRoot, "managed-update-rollback-data");
    var rollbackStateStore = new InstallationStateStore(rollbackDataRoot);
    await new ManagedFilesInstaller(rollbackStateStore).InstallAsync(
        oldPackage,
        oldProduct,
        rollbackGameRoot);

    var failingInstaller = new ManagedFilesInstaller(
        new FailingVersionStateStore(rollbackStateStore, "1.1.0"));
    await AssertThrowsAsync<InstallationStateException>(
        () => failingInstaller.UpdateAsync(newPackage, newProduct, rollbackGameRoot),
        "A state commit failure should fail the update.");
    var rolledBackState = await rollbackStateStore.LoadAsync(oldProduct.Id);
    Assert(rolledBackState?.Version == "1.0.0", "Rollback did not preserve the old state.");
    Assert(
        File.ReadAllBytes(rollbackTargetPath).SequenceEqual(oldPayload),
        "Rollback did not restore the old managed file.");
    Assert(
        !Directory.Exists(Path.Combine(rollbackDataRoot, "state", "transactions")) ||
        !Directory.EnumerateFiles(
            Path.Combine(rollbackDataRoot, "state", "transactions"),
            "*.update.json").Any(),
        "A completed rollback left a transaction journal behind.");

    await CheckManagedUpdateRecoveryAsync(
        oldPackage,
        oldProduct,
        oldPayload,
        newPackage,
        newProduct,
        newPayload);
}

async Task CheckManagedUpdateRecoveryAsync(
    ValidatedPackage oldPackage,
    ProductManifest oldProduct,
    byte[] oldPayload,
    ValidatedPackage newPackage,
    ProductManifest newProduct,
    byte[] newPayload)
{
    var rollbackGameRoot = Path.Combine(testRoot, "update-recovery-rollback-game");
    var rollbackTargetDirectory = Path.Combine(
        rollbackGameRoot,
        "Anvil",
        "Content",
        "Paks");
    var rollbackTargetPath = Path.Combine(rollbackTargetDirectory, "Test-Russian.pak");
    Directory.CreateDirectory(rollbackTargetDirectory);
    var rollbackDataRoot = Path.Combine(testRoot, "update-recovery-rollback-data");
    var rollbackStore = new InstallationStateStore(rollbackDataRoot);
    var rollbackInstaller = new ManagedFilesInstaller(rollbackStore);
    var rollbackOldState = ToLegacyState((await rollbackInstaller.InstallAsync(
        oldPackage,
        oldProduct,
        rollbackGameRoot)).State);
    await WriteLegacyStateAsync(rollbackDataRoot, rollbackOldState);
    var rollbackNewState = CreateUpdatedState(
        rollbackOldState,
        newPackage,
        newProduct);
    var rollbackOperationId = new string('a', 32);
    var rollbackJournal = new ManagedUpdateJournal
    {
        SchemaVersion = 1,
        OperationId = rollbackOperationId,
        ProductId = oldProduct.Id,
        OldState = rollbackOldState,
        NewState = rollbackNewState
    };
    await WriteLegacyJournalAsync(rollbackDataRoot, rollbackJournal);
    await AssertThrowsAsync<InstallationStateException>(
        () => new ManagedUpdateJournalStore(rollbackStore).SaveAsync(rollbackJournal),
        "The v2 journal writer must reject a legacy journal.");
    var rollbackBackupPath =
        $"{rollbackTargetPath}.nfg-update-old-{rollbackOperationId}.disabled";
    File.Move(rollbackTargetPath, rollbackBackupPath);
    await File.WriteAllBytesAsync(rollbackTargetPath, newPayload);

    await rollbackInstaller.RecoverPendingOperationsAsync();
    Assert(
        File.ReadAllBytes(rollbackTargetPath).SequenceEqual(oldPayload),
        "Startup recovery did not roll back an uncommitted update.");
    Assert(!File.Exists(rollbackBackupPath), "Rollback recovery left the old backup behind.");
    Assert(
        (await rollbackStore.LoadAsync(oldProduct.Id))?.Version == "1.0.0",
        "Rollback recovery changed the committed installation state.");

    var commitGameRoot = Path.Combine(testRoot, "update-recovery-commit-game");
    var commitTargetDirectory = Path.Combine(commitGameRoot, "Anvil", "Content", "Paks");
    var commitTargetPath = Path.Combine(commitTargetDirectory, "Test-Russian.pak");
    Directory.CreateDirectory(commitTargetDirectory);
    var commitDataRoot = Path.Combine(testRoot, "update-recovery-commit-data");
    var commitStore = new InstallationStateStore(commitDataRoot);
    var commitInstaller = new ManagedFilesInstaller(commitStore);
    var commitOldState = ToLegacyState((await commitInstaller.InstallAsync(
        oldPackage,
        oldProduct,
        commitGameRoot)).State);
    await WriteLegacyStateAsync(commitDataRoot, commitOldState);
    var commitNewState = CreateUpdatedState(commitOldState, newPackage, newProduct);
    var commitOperationId = new string('b', 32);
    await WriteLegacyJournalAsync(commitDataRoot, new ManagedUpdateJournal
    {
        SchemaVersion = 1,
        OperationId = commitOperationId,
        ProductId = oldProduct.Id,
        OldState = commitOldState,
        NewState = commitNewState
    });
    var commitBackupPath =
        $"{commitTargetPath}.nfg-update-old-{commitOperationId}.disabled";
    File.Move(commitTargetPath, commitBackupPath);
    await File.WriteAllBytesAsync(commitTargetPath, newPayload);
    await WriteLegacyStateAsync(commitDataRoot, commitNewState);

    await commitInstaller.RecoverPendingOperationsAsync();
    Assert(
        File.ReadAllBytes(commitTargetPath).SequenceEqual(newPayload),
        "Startup recovery did not preserve a committed update.");
    Assert(!File.Exists(commitBackupPath), "Commit recovery left the old backup behind.");
    Assert(
        (await commitStore.LoadAsync(oldProduct.Id))?.Version == "1.1.0",
        "Commit recovery did not preserve the new installation state.");
}

static InstalledProductState CreateUpdatedState(
    InstalledProductState oldState,
    ValidatedPackage newPackage,
    ProductManifest newProduct) =>
    oldState with
    {
        Version = newProduct.Release.Version,
        PackageSizeBytes = newProduct.Release.Payload!.SizeBytes,
        PackageSha256 = newProduct.Release.Payload.Sha256,
        SteamAppId = newPackage.Manifest.Steam.AppId,
        SteamBuildId = newPackage.Manifest.Steam.BuildId,
        InstalledAt = oldState.InstalledAt.AddSeconds(1),
        Files = newPackage.Files.Select(file => new InstalledFileState
        {
            Destination = file.DestinationRelativePath,
            SizeBytes = file.SizeBytes,
            Sha256 = file.Sha256
        }).ToArray()
    };

static InstalledProductState ToLegacyState(InstalledProductState state) =>
    state with
    {
        SchemaVersion = 1,
        InstallationKey = null
    };

static async Task WriteLegacyStateAsync(
    string dataRoot,
    InstalledProductState state)
{
    var path = Path.Combine(
        dataRoot,
        "state",
        "installations",
        $"{state.ProductId}.json");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await File.WriteAllTextAsync(
        path,
        JsonSerializer.Serialize(
            state,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
}

static async Task WriteLegacyJournalAsync(
    string dataRoot,
    ManagedUpdateJournal journal)
{
    var productId = journal.ProductId
        ?? throw new InvalidOperationException("Legacy journal fixture has no product id.");
    var path = Path.Combine(
        dataRoot,
        "state",
        "transactions",
        $"{productId}.update.json");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await File.WriteAllTextAsync(
        path,
        JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                journal.OperationId,
                productId,
                journal.OldState,
                journal.NewState
            },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
}

static ProductManifest CreateBranchProduct()
{
    var product = CreateProduct(1, new string('a', 64), "2.0.0");
    return product with
    {
        Release = product.Release with
        {
            GameVersion = "steam-build-200"
        },
        Releases =
        [
            product.Release with
            {
                Version = "1.0.0",
                GameVersion = "steam-build-100",
                Payload = product.Release.Payload! with { Sha256 = new string('b', 64) }
            }
        ],
        Compatibility = product.Compatibility with
        {
            GameVersion = "steam-build-200"
        }
    };
}

async Task CheckCoordinatorAllowsUnverifiedBuildAsync()
{
    const string verifiedBuildId = "24378492";
    const string detectedBuildId = "99999999";
    var packageBytes = BuildPackageBytes(version: "1.0.0");
    var product = CreateProduct(
        packageBytes.LongLength,
        Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant());
    var libraryRoot = Path.Combine(testRoot, "coordinator-unverified-steam-library");
    var steamAppsRoot = Path.Combine(libraryRoot, "steamapps");
    var gameRoot = Path.Combine(steamAppsRoot, "common", "Anvil Unverified");
    Directory.CreateDirectory(Path.Combine(gameRoot, "Anvil", "Content", "Paks"));
    File.WriteAllText(
        Path.Combine(steamAppsRoot, "appmanifest_2383950.acf"),
        $$"""
        "AppState"
        {
            "appid" "2383950"
            "name" "Anvil Empires"
            "installdir" "Anvil Unverified"
            "buildid" "{{detectedBuildId}}"
            "TargetBuildID" "{{detectedBuildId}}"
        }
        """);

    var dataRoot = Path.Combine(testRoot, "coordinator-unverified-data");
    var stateStore = new InstallationStateStore(dataRoot);
    var packageHandler = new CountingBytesHandler(packageBytes);
    using (var httpClient = new HttpClient(packageHandler))
    {
        var coordinator = new ProductInstallationCoordinator(
            httpClient,
            dataRoot,
            stateStore,
            new SteamGameLocator([libraryRoot], includeConfiguredSteam: false));
        var installed = await coordinator.InstallAsync(product);

        Assert(installed.State.SteamBuildId == verifiedBuildId,
            "The package's verified Steam build was not saved.");
        Assert(installed.State.DetectedSteamBuildId == detectedBuildId,
            "The different Steam build detected during installation was not saved.");
        Assert(packageHandler.RequestCount == 1,
            "Installing on an unverified game build should download the package once.");
    }

    var missingLibraryRoot = Path.Combine(testRoot, "coordinator-missing-steam-library");
    Directory.CreateDirectory(missingLibraryRoot);
    var missingDataRoot = Path.Combine(testRoot, "coordinator-missing-data");
    var missingHandler = new CountingBytesHandler(packageBytes);
    using var missingHttpClient = new HttpClient(missingHandler);
    var missingCoordinator = new ProductInstallationCoordinator(
        missingHttpClient,
        missingDataRoot,
        new InstallationStateStore(missingDataRoot),
        new SteamGameLocator([missingLibraryRoot], includeConfiguredSteam: false));

    await AssertThrowsAsync<ProductInstallationException>(
        () => missingCoordinator.InstallAsync(product),
        "Installation should stop when Steam does not contain the game.");
    Assert(missingHandler.RequestCount == 0,
        "The coordinator contacted the package server before finding the game.");
}

void CheckSteamDiscovery()
{
    var libraryRoot = Path.Combine(testRoot, "steam-library");
    var steamAppsRoot = Path.Combine(libraryRoot, "steamapps");
    var gameRoot = Path.Combine(steamAppsRoot, "common", "Anvil Playtest");
    Directory.CreateDirectory(gameRoot);

    File.WriteAllText(
        Path.Combine(steamAppsRoot, "appmanifest_2383950.acf"),
        """
        "AppState"
        {
            "appid" "2383950"
            "name" "Anvil Empires"
            "installdir" "Anvil Playtest"
            "buildid" "24378492"
            "TargetBuildID" "24378492"
            "UserConfig"
            {
                "language" "english"
            }
        }
        """);

    var matches = new SteamGameLocator([libraryRoot], includeConfiguredSteam: false).Find("2383950");
    var installation = matches.Single();
    Assert(installation.BuildId == "24378492", "Unexpected Steam build.");
    Assert(installation.Language == "english", "Unexpected Steam game language.");
    Assert(
        Path.GetFullPath(installation.GameRoot) == Path.GetFullPath(gameRoot),
        "Unexpected Steam game root.");
}

static string WriteSteamInstallation(
    string libraryRoot,
    string installDirectoryName,
    string buildId)
{
    var steamAppsRoot = Path.Combine(libraryRoot, "steamapps");
    var gameRoot = Path.Combine(steamAppsRoot, "common", installDirectoryName);
    Directory.CreateDirectory(gameRoot);
    File.WriteAllText(
        Path.Combine(steamAppsRoot, "appmanifest_2383950.acf"),
        $$"""
        "AppState"
        {
            "appid" "2383950"
            "name" "Anvil Empires"
            "installdir" "{{installDirectoryName}}"
            "buildid" "{{buildId}}"
            "TargetBuildID" "{{buildId}}"
        }
        """);
    return gameRoot;
}

static bool PathsEqual(string left, string right) =>
    Path.GetFullPath(left).Equals(
        Path.GetFullPath(right),
        StringComparison.OrdinalIgnoreCase);

static byte[] BuildPackageBytes(
    string destination = "steam-game/Anvil/Content/Paks/Test-Russian.pak",
    string version = "1.0.0",
    byte[]? payload = null)
{
    payload ??= GetSyntheticPayload();
    var payloadHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    var manifest = new
    {
        schema = "nfg-package/1",
        productId = "nfg.test.localization",
        version,
        strategy = "managed-files",
        steam = new { appId = "2383950", buildId = "24378492" },
        files = new[]
        {
            new
            {
                source = "Test-Russian.pak",
                destination,
                sizeBytes = payload.LongLength,
                sha256 = payloadHash
            }
        }
    };

    using var output = new MemoryStream();
    using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
    {
        WriteEntry(archive, "TestPackage/Test-Russian.pak", payload);
        WriteEntry(
            archive,
            "TestPackage/nfg-package.json",
            JsonSerializer.SerializeToUtf8Bytes(manifest));
    }

    return output.ToArray();
}

static byte[] GetSyntheticPayload() =>
    Encoding.UTF8.GetBytes("synthetic managed package payload");

static ProductManifest CreateProduct(
    long packageSize,
    string packageHash,
    string version = "1.0.0") => new()
    {
        SchemaVersion = 1,
        Id = "nfg.test.localization",
        Type = "localization",
        Display = new ProductDisplay
        {
            Title = "Test",
            Subtitle = "Test",
            Summary = "Test",
            Description = "Test",
            Features = ["Test feature"]
        },
        Release = new ProductRelease
        {
            Version = version,
            Channel = "stable",
            Highlights = ["Test highlight"],
            KnownIssues = ["Test known issue"],
            NotesUrl = new Uri("https://catalog.test/releases/1.0.0"),
            Payload = new ProductPayload
            {
                Url = new Uri("https://packages.test/package.zip"),
                SizeBytes = packageSize,
                Sha256 = packageHash
            }
        },
        Compatibility = new ProductCompatibility
        {
            Platforms = ["windows-x64"],
            GameVersion = "steam-build-24378492",
            Status = "compatible"
        },
        Installation = new ProductInstallation
        {
            Strategy = "managed-files",
            SupportsRollback = true,
            RequiresElevation = "auto",
            Detection =
        [
            new ProductDetectionRule { Provider = "steam", ProductId = "2383950" }
        ]
        },
        Dependencies = [],
        Progress = new ProductProgress { Label = "Test" }
    };

static ProductManifest CreateVariantProduct(
    string id,
    string locale,
    string familyId = "nfg.test.localization",
    string exclusiveGroup = "nfg.test.game-language",
    string steamAppId = "2383950",
    string installationStrategy = "managed-files")
{
    var product = CreateProduct(1, new string('a', 64));
    return product with
    {
        SchemaVersion = 2,
        Id = id,
        FamilyId = familyId,
        Locale = locale,
        ExclusiveGroup = exclusiveGroup,
        Installation = product.Installation with
        {
            Strategy = installationStrategy,
            Detection =
            [
                new ProductDetectionRule { Provider = "steam", ProductId = steamAppId }
            ]
        }
    };
}

static void WriteEntry(ZipArchive archive, string path, byte[] contents)
{
    var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
    using var stream = entry.Open();
    stream.Write(contents);
}

static async Task AssertThrowsAsync<TException>(Func<Task> action, string message)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

static void AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

file sealed class CatalogHandler(string bundledRoot) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var relativePath = request.RequestUri?.AbsolutePath.TrimStart('/')
            ?? throw new InvalidOperationException("Request URI is missing.");
        var localPath = Path.GetFullPath(Path.Combine(bundledRoot, relativePath));
        var relative = Path.GetRelativePath(bundledRoot, localPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || !File.Exists(localPath))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(File.ReadAllBytes(localPath))
        };
        return Task.FromResult(response);
    }
}

file sealed class BytesHandler(byte[] contents) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var content = new ByteArrayContent(contents);
        content.Headers.ContentLength = contents.LongLength;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
}

file sealed class CountingBytesHandler(byte[] contents) : HttpMessageHandler
{
    private int _requestCount;

    public int RequestCount => Volatile.Read(ref _requestCount);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requestCount);
        var content = new ByteArrayContent(contents);
        content.Headers.ContentLength = contents.LongLength;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
}

file sealed class OfflineHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(new HttpRequestException("Offline test."));
}

file sealed class UnexpectedNetworkHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A local-only catalog load attempted network access.");
}

file sealed class FailingVersionStateStore(
    IInstallationStateStore inner,
    string failingVersion) : IInstallationStateStore
{
    public string DataRoot => inner.DataRoot;

    public Task<InstalledProductState?> LoadAsync(
        string productId,
        CancellationToken cancellationToken = default) =>
        inner.LoadAsync(productId, cancellationToken);

    public Task<IReadOnlyList<InstalledProductState>> LoadAllAsync(
        CancellationToken cancellationToken = default) =>
        inner.LoadAllAsync(cancellationToken);

    public Task SaveAsync(
        InstalledProductState state,
        CancellationToken cancellationToken = default) =>
        state.Version.Equals(failingVersion, StringComparison.Ordinal)
            ? Task.FromException(new InstallationStateException(
                $"Injected state failure for version '{failingVersion}'."))
            : inner.SaveAsync(state, cancellationToken);

    public void Delete(string productId) => inner.Delete(productId);
}
