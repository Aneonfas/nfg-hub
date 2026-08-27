using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nfg.Store.App.Services;
using Nfg.Store.Contracts;
using Nfg.Store.Core;
using Nfg.Store.Installation;
using Nfg.Store.Platform.Windows;

internal static class ManagedVariantInstallerSmoke
{
    private const string InstallationKey = "nfg.anvil-empires.ru";
    private const string FamilyId = "nfg.anvil-empires.localization";
    private const string RuProductId = "nfg.anvil-empires.ru";
    private const string EsProductId = "nfg.anvil-empires.es";
    private const string ThirdProductId = "nfg.anvil-empires.third";
    private const string ForgeProductId = "nfg.anvil-empires.forge-helper";
    private const string SteamAppId = "2383950";
    private const string SteamBuildId = "24619810";
    private const string Version = "1.0.0";

    private const string LanguageDestination =
        "Anvil/Content/Paks/Nfg-Test-Language.pak";
    private const string SharedDestination =
        "Anvil/Content/Paks/Nfg-Test-Shared.pak";
    private const string RuOnlyDestination =
        "Anvil/Content/Paks/Nfg-Test-Ru-Only.pak";
    private const string EsOnlyDestination =
        "Anvil/Content/Paks/Nfg-Test-Es-Only.pak";
    private const string AdoptedDestination =
        "Anvil/Content/Paks/Nfg-Test-Adopted.pak";
    private const string FreshDestination =
        "Anvil/Content/Paks/Nfg-Test-Fresh.pak";
    private const string ForgeDestination =
        "Anvil/Content/Paks/Nfg-Test-Forge.pak";

    private static readonly byte[] RuPayload = Encoding.UTF8.GetBytes(
        "managed variant RU payload");
    private static readonly byte[] EsPayload = Encoding.UTF8.GetBytes(
        "managed variant ES payload");
    private static readonly byte[] SharedPayload = Encoding.UTF8.GetBytes(
        "managed variant shared payload");
    private static readonly byte[] RuOnlyPayload = Encoding.UTF8.GetBytes(
        "managed variant RU-only payload");
    private static readonly byte[] EsOnlyPayload = Encoding.UTF8.GetBytes(
        "managed variant ES-only payload");
    private static readonly byte[] AdoptedPayload = Encoding.UTF8.GetBytes(
        "pre-existing exact adopted payload");
    private static readonly byte[] FreshPayload = Encoding.UTF8.GetBytes(
        "new payload beside adopted file");
    private static readonly byte[] ForgePayload = Encoding.UTF8.GetBytes(
        "unrelated forge payload");

    public static async Task RunAsync(string testRoot)
    {
        var root = Path.Combine(testRoot, "managed-variant-installer");
        await CheckSameVersionRoundTripsAsync(Path.Combine(root, "round-trips"));
        await CheckExpectedProductGuardsAsync(Path.Combine(root, "expected-product"));
        await CheckCorruptedArchiveLeavesOldStateAsync(Path.Combine(root, "corrupt-archive"));
        await CheckFailureAndInterruptionRecoveryAsync(Path.Combine(root, "recovery-matrix"));
        await CheckUnknownThirdStateFailsClosedAsync(Path.Combine(root, "third-state"));
        await CheckConcurrentInstallersAsync(Path.Combine(root, "concurrency"));
        await CheckFreshAdoptionRollbackAsync(Path.Combine(root, "adoption-rollback"));
        await CheckLegacyTruncatedStageRecoveryAsync(Path.Combine(root, "legacy-truncated-stage"));
        await CheckExternalExactTargetIsPreservedAsync(Path.Combine(root, "external-exact-target"));
        await CheckUnverifiedPartialFailsClosedAsync(Path.Combine(root, "unverified-partial"));
        await CheckLegacyLocaleInventoryReconciliationAsync(
            Path.Combine(root, "legacy-locale-inventory"));
        await RunLocalizationLifecycleAsync(root);
    }

    public static async Task RunLocalizationLifecycleAsync(string testRoot)
    {
        var failures = new List<Exception>();
        foreach (var (name, check) in new (string, Func<string, Task>)[]
                 {
                     ("manual-localization-conflict", CheckManualLocalizationConflictAsync),
                     ("localization-mutation-guards", CheckLocalizationMutationGuardsAsync),
                     ("cached-localization-history", CheckCachedLocalizationHistoryAsync),
                     ("offline-localization-removal", CheckOfflineLocalizationRemovalAsync),
                     ("offline-removal-ownership", CheckOfflineRemovalOwnershipAsync)
                 })
        {
            try
            {
                await check(Path.Combine(testRoot, name));
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"{name}: {exception.Message}");
                failures.Add(exception);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Localization lifecycle regressions failed.", failures);
        }
    }

    private static async Task CheckManualLocalizationConflictAsync(string root)
    {
        const string ruDestination = "Anvil/Content/Paks/Anvil-Russian-Full_P.pak";
        const string esDestination = "Anvil/Content/Paks/Anvil-Spanish-Full_P.pak";
        var (libraryRoot, gameRoot) = CreateSteamGameRoot(root);
        var dataRoot = Path.Combine(root, "data");
        var ru = await CreateLanguagePackageAsync(
            Path.Combine(root, "packages"), RuProductId, "ru",
            new PackageFile("ru.pak", ruDestination, FreshPayload));
        var es = await CreateLanguagePackageAsync(
            Path.Combine(root, "packages"),
            EsProductId,
            "es",
            new PackageFile("es.pak", esDestination, EsPayload));
        WriteManagedFile(gameRoot, ForgeDestination, ForgePayload);
        using var offline = new OfflinePackageHandler();
        using var httpClient = new HttpClient(offline);
        var stateStore = new InstallationStateStore(dataRoot);
        var coordinator = new ProductInstallationCoordinator(
            httpClient,
            dataRoot,
            stateStore,
            new SteamGameLocator([libraryRoot], includeConfiguredSteam: false));
        var emptyInventory = await coordinator.ReconcileFamilyAsync([ru.Product, es.Product]);
        Assert(emptyInventory.State is null && !emptyInventory.HasConflict &&
               !emptyInventory.HasAnyInstalledVariants &&
               (await stateStore.LoadAllAsync()).Count == 0,
            "The first-run inventory claimed a localization in a game with no language PAK.");
        WriteManagedFile(gameRoot, ruDestination, RuPayload);
        var before = SnapshotDirectory(gameRoot);
        var ruPath = ResolveManagedPath(gameRoot, ruDestination);
        var inventory = await coordinator.ReconcileFamilyAsync([ru.Product, es.Product]);
        Assert(inventory.HasConflict && inventory.State is null &&
               inventory.ConflictPaths?.Contains(ruPath) == true &&
               inventory.HasVariant(RuProductId) && !inventory.HasAnyInstalledVariants,
            "A manual old RU PAK was not exposed as an unowned active conflict.");
        Assert((await stateStore.LoadAllAsync()).Count == 0 &&
               (await new ProductLibraryStore(dataRoot).LoadAsync()).Count == 0,
            "The first-run manual conflict invented an installed version or a library entry.");
        Assert(!Directory.Exists(Path.Combine(dataRoot, "downloads")),
            "A first-run inventory probe created a package cache without a download.");

        CachePackage(dataRoot, ru);
        CachePackage(dataRoot, es);
        inventory = await coordinator.ReconcileFamilyAsync([ru.Product, es.Product]);
        Assert(inventory.HasConflict && inventory.State is null,
            "The current cached RU release incorrectly claimed the older manual PAK.");

        var error = await AssertThrowsAsync<ProductInstallationException>(
            () => coordinator.InstallAsync(es.Product),
            "Hub installed ES beside an unmanaged active RU PAK.");
        Assert(error.Message.Contains(ruPath, StringComparison.Ordinal) &&
               error.Message.Contains(".disabled", StringComparison.Ordinal),
            "The service conflict error omitted the preserved file or disabling instructions.");
        var installer = new ManagedFilesInstaller(stateStore);
        await AssertThrowsAsync<ManagedFilesInstallException>(
            () => installer.ApplyVariantAsync(es.Package, es.Product, gameRoot, expectedProductId: null),
            "The direct variant API bypassed the active localization conflict guard.");
        Assert(SnapshotDirectory(gameRoot) == before,
            "An active localization conflict changed game files.");
        Assert(await stateStore.LoadAsync(InstallationKey) is null,
            "An active localization conflict claimed an installation state.");
        Assert(offline.Requests.Count == 0,
            "A cached localization conflict check accessed the network.");

        File.Move(ruPath, $"{ruPath}.disabled");
        await coordinator.InstallAsync(es.Product);
        AssertManagedFile(gameRoot, esDestination, EsPayload, isEnabled: true);
        Assert(File.ReadAllBytes($"{ruPath}.disabled").SequenceEqual(RuPayload),
            "Installing ES changed a disabled manual RU backup.");
        AssertManagedFile(gameRoot, ForgeDestination, ForgePayload, isEnabled: true);
    }

    private static async Task CheckLocalizationMutationGuardsAsync(string root)
    {
        const string ruDestination = "Anvil/Content/Paks/Anvil-Russian-Full_P.pak";
        const string esDestination = "Anvil/Content/Paks/Anvil-Spanish-Full_P.pak";
        const string extraDestination = "Anvil/Content/Paks/Anvil-Spanish-Manual_P.pak";
        var gameRoot = CreateGameRoot(root);
        var dataRoot = Path.Combine(root, "data");
        var ru = await CreatePackageAsync(root, RuProductId, "ru",
            [new PackageFile("ru.pak", ruDestination, RuPayload)]);
        var ruUpdate = await CreatePackageAsync(root, RuProductId, "ru",
            [new PackageFile("ru.pak", ruDestination, FreshPayload)], "1.0.1");
        var es = await CreatePackageAsync(root, EsProductId, "es",
            [new PackageFile("es.pak", esDestination, EsPayload)]);
        var stateStore = new InstallationStateStore(dataRoot);
        var installer = new ManagedFilesInstaller(stateStore);
        await installer.InstallAsync(ru.Package, ru.Product, gameRoot);
        WriteManagedFile(gameRoot, extraDestination, EsOnlyPayload);
        var gameBefore = SnapshotDirectory(gameRoot);
        var stateBefore = await File.ReadAllBytesAsync(GetStatePath(dataRoot, InstallationKey));
        foreach (var mutation in new Func<Task>[]
                 {
                     () => installer.InstallAsync(ru.Package, ru.Product, gameRoot),
                     () => installer.InstallAsync(ruUpdate.Package, ruUpdate.Product, gameRoot),
                     () => installer.UpdateAsync(ruUpdate.Package, ruUpdate.Product, gameRoot),
                     () => installer.SwitchVersionAsync(ruUpdate.Package, ruUpdate.Product, gameRoot),
                     () => installer.ApplyVariantAsync(es.Package, es.Product, gameRoot, RuProductId),
                     () => installer.SetEnabledAsync(InstallationKey, RuProductId, isEnabled: true)
                 })
        {
            await AssertThrowsAsync<ActiveLocalizationConflictException>(mutation,
                "A mutation API bypassed the active localization conflict guard.");
            Assert(SnapshotDirectory(gameRoot) == gameBefore &&
                   File.ReadAllBytes(GetStatePath(dataRoot, InstallationKey)).SequenceEqual(stateBefore),
                "A rejected localization mutation changed files or state.");
        }

        await installer.SetEnabledAsync(InstallationKey, RuProductId, isEnabled: false);
        await AssertThrowsAsync<ActiveLocalizationConflictException>(
            () => installer.SetEnabledAsync(InstallationKey, RuProductId, isEnabled: true),
            "Re-enabling RU left a second manual language active.");
        AssertManagedFile(gameRoot, ruDestination, RuPayload, isEnabled: false);
        var extraPath = ResolveManagedPath(gameRoot, extraDestination);
        File.Move(extraPath, $"{extraPath}.disabled");
        await installer.SetEnabledAsync(InstallationKey, RuProductId, isEnabled: true);

        var duringStaging = new ManagedFilesInstaller(stateStore)
        {
            PhaseObserver = phase =>
            {
                if (phase == ManagedInstallerPhase.StagingComplete)
                {
                    File.Move($"{extraPath}.disabled", extraPath);
                }

                return Task.CompletedTask;
            }
        };
        await AssertThrowsAsync<ActiveLocalizationConflictException>(
            () => duringStaging.ApplyVariantAsync(es.Package, es.Product, gameRoot, RuProductId),
            "A PAK introduced during staging bypassed the pre-activation guard.");
        AssertState(await stateStore.LoadAsync(InstallationKey), RuProductId, isEnabled: true);
        AssertManagedFile(gameRoot, ruDestination, RuPayload, isEnabled: true);
        AssertManagedFile(gameRoot, extraDestination, EsOnlyPayload, isEnabled: true);
        AssertManagedPathAbsent(gameRoot, esDestination);
        AssertNoOperationArtifacts(dataRoot, gameRoot);
    }

    private static async Task CheckCachedLocalizationHistoryAsync(string root)
    {
        const string ruDestination = "Anvil/Content/Paks/Anvil-Russian-Full_P.pak";
        const string esDestination = "Anvil/Content/Paks/Anvil-Spanish-Full_P.pak";
        var (libraryRoot, gameRoot) = CreateSteamGameRoot(root);
        var dataRoot = Path.Combine(root, "data");
        var oldRu = await CreatePackageAsync(root, RuProductId, "ru",
            [new PackageFile("ru.pak", ruDestination, RuPayload)], "1.0.1");
        var ru = await CreatePackageAsync(root, RuProductId, "ru",
            [new PackageFile("ru.pak", ruDestination, FreshPayload)], "1.0.2");
        var es = await CreatePackageAsync(root, EsProductId, "es",
            [new PackageFile("es.pak", esDestination, EsPayload)], "1.0.0-beta.1");
        var currentRu = ru.Product with { Releases = [oldRu.Product.Release] };
        var spanish = es.Product with { Release = es.Product.Release with { Channel = "beta" } };
        WriteManagedFile(gameRoot, ruDestination, RuPayload);
        CachePackage(dataRoot, oldRu);
        CachePackage(dataRoot, es);
        using var offline = new OfflinePackageHandler();
        using var httpClient = new HttpClient(offline);
        var stateStore = new InstallationStateStore(dataRoot);
        var coordinator = new ProductInstallationCoordinator(httpClient, dataRoot, stateStore,
            new SteamGameLocator([libraryRoot], includeConfiguredSteam: false));
        var inventory = await coordinator.ReconcileFamilyAsync([currentRu, spanish]);
        Assert(inventory.State?.Version == "1.0.1" && !inventory.HasConflict,
            "Cached RU history did not recognize the manual old release without downloading current RU.");
        AssertState(inventory.State, RuProductId, isEnabled: true, version: "1.0.1");
        Assert(ProductReleaseCatalog.IsPublished(spanish.Release),
            "The prerelease package fixture was not selectable.");
        await coordinator.ApplyVariantAsync(spanish, expectedProductId: RuProductId);
        inventory = await coordinator.ReconcileFamilyAsync([currentRu, spanish]);
        AssertState(inventory.State, EsProductId, isEnabled: true, version: "1.0.0-beta.1");
        Assert(inventory.State?.Version == "1.0.0-beta.1" && !inventory.HasConflict,
            "The prerelease ES switch did not preserve its exact package version.");
        AssertManagedPathAbsent(gameRoot, ruDestination);
        AssertManagedFile(gameRoot, esDestination, EsPayload, isEnabled: true);
        Assert(offline.Requests.Count == 0,
            "Recognition or switching with cached historical packages attempted HTTP.");
    }

    private static async Task CheckOfflineLocalizationRemovalAsync(string root)
    {
        const string ruDestination = "Anvil/Content/Paks/Anvil-Russian-Full_P.pak";
        var packagesRoot = Path.Combine(root, "packages");
        var ru = await CreateLanguagePackageAsync(
            packagesRoot, RuProductId, "ru",
            new PackageFile("ru.pak", ruDestination, RuPayload));
        var es = await CreateLanguagePackageAsync(
            packagesRoot, EsProductId, "es",
            new PackageFile("es.pak", "Anvil/Content/Paks/Anvil-Spanish-Full_P.pak", EsPayload));
        foreach (var (cacheOwnPackage, enabled, scope) in new[]
                 {
                     (true, true, ManagedVariantRemovalScope.SelectedVariant),
                     (false, true, ManagedVariantRemovalScope.SelectedVariant),
                     (false, false, ManagedVariantRemovalScope.SelectedVariant),
                     (false, true, ManagedVariantRemovalScope.AllVariants),
                     (false, false, ManagedVariantRemovalScope.AllVariants)
                 })
        {
            var caseRoot = Path.Combine(root, $"cached-{cacheOwnPackage}-enabled-{enabled}-{scope}");
            var (libraryRoot, gameRoot) = CreateSteamGameRoot(caseRoot);
            var dataRoot = Path.Combine(caseRoot, "data");
            var stateStore = new InstallationStateStore(dataRoot);
            var installer = new ManagedFilesInstaller(stateStore);
            await installer.InstallAsync(ru.Package, ru.Product, gameRoot, SteamBuildId);
            if (!enabled)
            {
                await installer.SetEnabledAsync(InstallationKey, RuProductId, isEnabled: false);
            }

            WriteManagedFile(gameRoot, ForgeDestination, ForgePayload);
            if (cacheOwnPackage)
            {
                CachePackage(dataRoot, ru);
            }

            using var offline = new OfflinePackageHandler();
            using var httpClient = new HttpClient(offline);
            var coordinator = new ProductInstallationCoordinator(
                httpClient, dataRoot, stateStore,
                new SteamGameLocator([libraryRoot], includeConfiguredSteam: false));
            var inventory = await coordinator.RemoveFamilyVariantAsync(
                [ru.Product, es.Product],
                RuProductId,
                scope);
            Assert(inventory.State is null && !inventory.HasAnyInstalledVariants,
                "Offline RU removal retained its installed inventory.");
            AssertManagedPathAbsent(gameRoot, ruDestination);
            Assert(await stateStore.LoadAsync(InstallationKey) is null,
                "Offline RU removal retained persisted state.");
            AssertManagedFile(gameRoot, ForgeDestination, ForgePayload, isEnabled: true);
            Assert(offline.Requests.Count == 0,
                "Removing a known RU installation tried to download a package.");
        }
    }

    private static async Task CheckOfflineRemovalOwnershipAsync(string root)
    {
        const string ruDestination = "Anvil/Content/Paks/Anvil-Russian-Full_P.pak";
        const string esDestination = "Anvil/Content/Paks/Anvil-Spanish-Full_P.pak";
        var ru = await CreatePackageAsync(root, RuProductId, "ru",
            [new PackageFile("ru.pak", ruDestination, RuPayload)]);
        var es = await CreatePackageAsync(root, EsProductId, "es",
            [new PackageFile("es.pak", esDestination, EsPayload)]);
        foreach (var scenario in new[] { "modified", "unknown-backup", "unknown-sibling-all", "unknown-sibling-selected" })
        {
            var (libraryRoot, gameRoot) = CreateSteamGameRoot(Path.Combine(root, scenario));
            var dataRoot = Path.Combine(root, scenario, "data");
            var stateStore = new InstallationStateStore(dataRoot);
            await new ManagedFilesInstaller(stateStore).InstallAsync(
                ru.Package, ru.Product, gameRoot, SteamBuildId);
            var ruPath = ResolveManagedPath(gameRoot, ruDestination);
            if (scenario == "modified")
            {
                File.WriteAllBytes(ruPath, FreshPayload);
            }
            else if (scenario == "unknown-backup")
            {
                File.WriteAllBytes($"{ruPath}.previous-manual.disabled", FreshPayload);
            }
            else
            {
                WriteManagedFile(gameRoot, esDestination, EsOnlyPayload);
            }

            var before = SnapshotDirectory(gameRoot);
            var stateBefore = File.ReadAllBytes(GetStatePath(dataRoot, InstallationKey));
            using var offline = new OfflinePackageHandler();
            using var httpClient = new HttpClient(offline);
            var coordinator = new ProductInstallationCoordinator(httpClient, dataRoot, stateStore,
                new SteamGameLocator([libraryRoot], includeConfiguredSteam: false));
            if (scenario == "unknown-sibling-selected")
            {
                var inventory = await coordinator.RemoveFamilyVariantAsync(
                    [ru.Product, es.Product], RuProductId, ManagedVariantRemovalScope.SelectedVariant);
                Assert(inventory.State is null && inventory.HasConflict,
                    "Removing known RU concealed the remaining unknown ES conflict.");
                AssertManagedPathAbsent(gameRoot, ruDestination);
                AssertManagedFile(gameRoot, esDestination, EsOnlyPayload, isEnabled: true);
            }
            else
            {
                await AssertThrowsAsync<ManagedFilesInstallException>(
                    () => coordinator.RemoveFamilyVariantAsync([ru.Product, es.Product], RuProductId,
                        scenario == "unknown-sibling-all"
                            ? ManagedVariantRemovalScope.AllVariants
                            : ManagedVariantRemovalScope.SelectedVariant),
                    "Removal discarded an unowned or modified localization artifact.");
                Assert(SnapshotDirectory(gameRoot) == before &&
                       File.ReadAllBytes(GetStatePath(dataRoot, InstallationKey)).SequenceEqual(stateBefore),
                    "Ownership preflight failed after removing known files or changing saved state.");
            }

            Assert(offline.Requests.Count == 0, "Ownership checks attempted a package download.");
            AssertNoOperationArtifacts(dataRoot, gameRoot);
        }

        foreach (var interrupted in new[] { false, true })
        {
            var caseRoot = Path.Combine(root, $"recovery-{interrupted}");
            var gameRoot = CreateGameRoot(caseRoot);
            var dataRoot = Path.Combine(caseRoot, "data");
            var stateStore = new InstallationStateStore(dataRoot);
            await new ManagedFilesInstaller(stateStore).InstallAsync(ru.Package, ru.Product, gameRoot);
            var mutationReached = false;
            var failing = new ManagedFilesInstaller(stateStore)
            {
                PhaseObserver = phase =>
                {
                    mutationReached |= phase == ManagedInstallerPhase.FileMutationCompleted;
                    return ThrowAtPhaseAsync(phase, ManagedInstallerPhase.FileMutationCompleted, interrupted);
                }
            };
            Func<Task> remove = () => failing.RemoveFamilyVariantAsync(
                [ru.Product, es.Product], [], gameRoot, RuProductId, ManagedVariantRemovalScope.SelectedVariant);
            if (interrupted)
            {
                await AssertThrowsAsync<ManagedInstallerInterruptionException>(remove,
                    "The family removal was not interrupted at the managed file mutation.");
            }
            else
            {
                await AssertThrowsAsync<InvalidOperationException>(remove,
                    "The family removal did not fail at the managed file mutation.");
            }

            Assert(mutationReached, "The family removal fixture did not reach its rollback boundary.");
            var recovered = new ManagedFilesInstaller(stateStore);
            await recovered.RecoverPendingOperationsAsync();
            AssertState(await stateStore.LoadAsync(InstallationKey), RuProductId, isEnabled: true);
            AssertManagedFile(gameRoot, ruDestination, RuPayload, isEnabled: true);
            AssertNoOperationArtifacts(dataRoot, gameRoot);
            await recovered.RemoveFamilyVariantAsync([ru.Product, es.Product], [], gameRoot,
                RuProductId, ManagedVariantRemovalScope.SelectedVariant);
            AssertManagedPathAbsent(gameRoot, ruDestination);
        }
    }

    internal static (string LibraryRoot, string GameRoot) CreateSteamGameRoot(string root)
    {
        var libraryRoot = Path.Combine(root, "steam-library");
        var gameRoot = Path.Combine(libraryRoot, "steamapps", "common", "Anvil Empires");
        Directory.CreateDirectory(Path.Combine(gameRoot, "Anvil", "Content", "Paks"));
        File.WriteAllText(
            Path.Combine(libraryRoot, "steamapps", $"appmanifest_{SteamAppId}.acf"),
            $$"""
              "AppState"
              {
                  "appid" "{{SteamAppId}}"
                  "installdir" "Anvil Empires"
                  "buildid" "{{SteamBuildId}}"
                  "TargetBuildID" "{{SteamBuildId}}"
                  "UserConfig" { "language" "english" }
              }
              """);
        return (libraryRoot, gameRoot);
    }

    internal static void CachePackage(string dataRoot, PackageFixture fixture)
    {
        var payload = fixture.Product.Release.Payload!;
        var path = Path.Combine(
            dataRoot, "downloads", fixture.Product.Id, fixture.Product.Release.Version,
            $"{payload.Sha256}.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(fixture.Package.ArchivePath, path);
    }

    private static async Task CheckLegacyLocaleInventoryReconciliationAsync(string root)
    {
        var dataRoot = Path.Combine(root, "data");
        var gameRoot = CreateGameRoot(root);
        var packagesRoot = Path.Combine(root, "packages");
        var ru = await CreateLanguagePackageAsync(
            packagesRoot,
            RuProductId,
            "ru",
            new PackageFile("ru-only.pak", RuOnlyDestination, RuOnlyPayload));
        var es = await CreateLanguagePackageAsync(
            packagesRoot,
            EsProductId,
            "es",
            new PackageFile("es-only.pak", EsOnlyDestination, EsOnlyPayload));
        var variants = new[]
        {
            new ManagedVariantPackage(ru.Product, ru.Package),
            new ManagedVariantPackage(es.Product, es.Package)
        };
        var stateStore = new InstallationStateStore(dataRoot);
        var installer = new ManagedFilesInstaller(stateStore);

        await installer.InstallAsync(ru.Package, ru.Product, gameRoot);
        await installer.SetEnabledAsync(InstallationKey, RuProductId, isEnabled: false);
        var ruPath = ResolveManagedPath(gameRoot, RuOnlyDestination);
        File.Move($"{ruPath}.nfg-disabled", $"{ruPath}.disabled");
        WriteManagedFile(gameRoot, EsOnlyDestination, EsOnlyPayload);

        var inventory = await installer.ReconcileFamilyAsync(variants, gameRoot, SteamBuildId);
        AssertState(inventory.State, EsProductId, isEnabled: true);
        Assert(inventory.Variants.Single(item => item.ProductId == RuProductId).IsInstalled,
            "Legacy disabled RU package was not detected.");
        Assert(inventory.Variants.Single(item => item.ProductId == EsProductId).IsEnabled,
            "Active ES package was not selected as the factual state.");
        Assert(File.Exists($"{ruPath}.disabled"),
            "Legacy disabled RU package was not preserved before an explicit removal.");

        inventory = await installer.RemoveFamilyVariantAsync(
            variants,
            gameRoot,
            RuProductId,
            ManagedVariantRemovalScope.SelectedVariant,
            SteamBuildId);
        AssertState(inventory.State, EsProductId, isEnabled: true);
        AssertManagedPathAbsent(gameRoot, RuOnlyDestination);
        AssertManagedFile(gameRoot, EsOnlyDestination, EsOnlyPayload, isEnabled: true);

        var esPath = ResolveManagedPath(gameRoot, EsOnlyDestination);
        File.WriteAllBytes($"{esPath}.previous-83e10db3.disabled", EsOnlyPayload);
        inventory = await installer.RemoveFamilyVariantAsync(
            variants,
            gameRoot,
            EsProductId,
            ManagedVariantRemovalScope.AllVariants,
            SteamBuildId);
        Assert(inventory.State is null && !inventory.HasAnyInstalledVariants,
            "Removing all locale variants retained an installed state.");
        AssertManagedPathAbsent(gameRoot, EsOnlyDestination);
        Assert(!File.Exists($"{esPath}.previous-83e10db3.disabled"),
            "Removing all locale variants retained a previous package artifact.");
    }

    private static async Task CheckSameVersionRoundTripsAsync(string root)
    {
        await CheckIdenticalDestinationRoundTripAsync(Path.Combine(root, "identical"));
        await CheckDifferentDestinationDisabledRoundTripAsync(Path.Combine(root, "different-disabled"));
    }

    private static async Task CheckIdenticalDestinationRoundTripAsync(string root)
    {
        var dataRoot = Path.Combine(root, "data");
        var gameRoot = CreateGameRoot(root);
        var packagesRoot = Path.Combine(root, "packages");
        var ru = await CreateLanguagePackageAsync(
            packagesRoot,
            RuProductId,
            "ru",
            new PackageFile("ru-language.pak", LanguageDestination, RuPayload),
            new PackageFile("shared.pak", SharedDestination, SharedPayload));
        var es = await CreateLanguagePackageAsync(
            packagesRoot,
            EsProductId,
            "es",
            new PackageFile("es-language.pak", LanguageDestination, EsPayload),
            new PackageFile("shared.pak", SharedDestination, SharedPayload));
        var stateStore = new InstallationStateStore(dataRoot);
        var installer = new ManagedFilesInstaller(stateStore);

        await installer.InstallAsync(ru.Package, ru.Product, gameRoot);
        AssertState(await stateStore.LoadAsync(InstallationKey), RuProductId, isEnabled: true);
        AssertManagedFile(gameRoot, LanguageDestination, RuPayload, isEnabled: true);

        var switchedToEs = await installer.ApplyVariantAsync(
            es.Package,
            es.Product,
            gameRoot,
            expectedProductId: RuProductId);
        Assert(switchedToEs.Outcome == ManagedInstallOutcome.Updated,
            "RU to ES did not use the unified update outcome.");
        Assert(switchedToEs.State.Version == Version,
            "A same-SemVer RU to ES transition changed the version.");
        AssertState(switchedToEs.State, EsProductId, isEnabled: true);
        AssertManagedFile(gameRoot, LanguageDestination, EsPayload, isEnabled: true);
        AssertManagedFile(gameRoot, SharedDestination, SharedPayload, isEnabled: true);

        var switchedToRu = await installer.ApplyVariantAsync(
            ru.Package,
            ru.Product,
            gameRoot,
            expectedProductId: EsProductId);
        Assert(switchedToRu.State.Version == Version,
            "A same-SemVer ES to RU transition changed the version.");
        AssertState(switchedToRu.State, RuProductId, isEnabled: true);
        AssertManagedFile(gameRoot, LanguageDestination, RuPayload, isEnabled: true);
        AssertManagedFile(gameRoot, SharedDestination, SharedPayload, isEnabled: true);
        AssertNoOperationArtifacts(dataRoot, gameRoot);
    }

    private static async Task CheckDifferentDestinationDisabledRoundTripAsync(string root)
    {
        var dataRoot = Path.Combine(root, "data");
        var gameRoot = CreateGameRoot(root);
        var packagesRoot = Path.Combine(root, "packages");
        var ru = await CreateLanguagePackageAsync(
            packagesRoot,
            RuProductId,
            "ru",
            new PackageFile("ru-shared.pak", SharedDestination, RuPayload),
            new PackageFile("ru-only.pak", RuOnlyDestination, RuOnlyPayload));
        var es = await CreateLanguagePackageAsync(
            packagesRoot,
            EsProductId,
            "es",
            new PackageFile("es-shared.pak", SharedDestination, EsPayload),
            new PackageFile("es-only.pak", EsOnlyDestination, EsOnlyPayload));
        var stateStore = new InstallationStateStore(dataRoot);
        var installer = new ManagedFilesInstaller(stateStore);

        await installer.InstallAsync(es.Package, es.Product, gameRoot);
        var disabledEs = await installer.SetEnabledAsync(
            InstallationKey,
            EsProductId,
            isEnabled: false);
        AssertState(disabledEs, EsProductId, isEnabled: false);

        var disabledRu = await installer.ApplyVariantAsync(
            ru.Package,
            ru.Product,
            gameRoot,
            expectedProductId: EsProductId);
        AssertState(disabledRu.State, RuProductId, isEnabled: false);
        AssertManagedFile(gameRoot, SharedDestination, RuPayload, isEnabled: false);
        AssertManagedFile(gameRoot, RuOnlyDestination, RuOnlyPayload, isEnabled: false);
        AssertManagedPathAbsent(gameRoot, EsOnlyDestination);

        var disabledEsAgain = await installer.ApplyVariantAsync(
            es.Package,
            es.Product,
            gameRoot,
            expectedProductId: RuProductId);
        AssertState(disabledEsAgain.State, EsProductId, isEnabled: false);
        AssertManagedFile(gameRoot, SharedDestination, EsPayload, isEnabled: false);
        AssertManagedFile(gameRoot, EsOnlyDestination, EsOnlyPayload, isEnabled: false);
        AssertManagedPathAbsent(gameRoot, RuOnlyDestination);
        AssertNoOperationArtifacts(dataRoot, gameRoot);
    }

    private static async Task CheckExpectedProductGuardsAsync(string root)
    {
        var dataRoot = Path.Combine(root, "data");
        var gameRoot = CreateGameRoot(root);
        var packagesRoot = Path.Combine(root, "packages");
        var ru = await CreateLanguagePackageAsync(
            packagesRoot,
            RuProductId,
            "ru",
            new PackageFile("ru.pak", LanguageDestination, RuPayload));
        var es = await CreateLanguagePackageAsync(
            packagesRoot,
            EsProductId,
            "es",
            new PackageFile("es.pak", LanguageDestination, EsPayload));
        var stateStore = new InstallationStateStore(dataRoot);
        var installer = new ManagedFilesInstaller(stateStore);
        await installer.InstallAsync(es.Package, es.Product, gameRoot);

        var stateBefore = await File.ReadAllBytesAsync(GetStatePath(dataRoot, InstallationKey));
        var gameBefore = SnapshotDirectory(gameRoot);
        await AssertThrowsAsync<ManagedFilesInstallException>(
            () => installer.SetEnabledAsync(
                InstallationKey,
                RuProductId,
                isEnabled: false),
            "Stale RU SetEnabled changed an active ES slot.");
        await AssertThrowsAsync<ManagedFilesInstallException>(
            () => installer.UninstallAsync(InstallationKey, RuProductId),
            "Stale RU uninstall changed an active ES slot.");
        await AssertThrowsAsync<ManagedFilesInstallException>(
            () => installer.ApplyVariantAsync(
                ru.Package,
                ru.Product,
                gameRoot,
                expectedProductId: RuProductId),
            "Stale RU ApplyVariant changed an active ES slot.");

        var stateAfter = await File.ReadAllBytesAsync(
            GetStatePath(dataRoot, InstallationKey));
        Assert(
            stateBefore.AsSpan().SequenceEqual(stateAfter),
            "Expected-product guards rewrote the active ES state.");
        Assert(gameBefore == SnapshotDirectory(gameRoot),
            "Expected-product guards changed active ES files.");
        AssertState(await stateStore.LoadAsync(InstallationKey), EsProductId, isEnabled: true);
        AssertManagedFile(gameRoot, LanguageDestination, EsPayload, isEnabled: true);
        AssertNoOperationArtifacts(dataRoot, gameRoot);
    }

    private static async Task CheckCorruptedArchiveLeavesOldStateAsync(string root)
    {
        var dataRoot = Path.Combine(root, "data");
        var gameRoot = CreateGameRoot(root);
        var packagesRoot = Path.Combine(root, "packages");
        var ru = await CreateLanguagePackageAsync(
            packagesRoot,
            RuProductId,
            "ru",
            new PackageFile("ru.pak", LanguageDestination, RuPayload));
        var es = await CreateLanguagePackageAsync(
            packagesRoot,
            EsProductId,
            "es",
            new PackageFile("es.pak", LanguageDestination, EsPayload));
        var stateStore = new InstallationStateStore(dataRoot);
        var installer = new ManagedFilesInstaller(stateStore);
        await installer.InstallAsync(ru.Package, ru.Product, gameRoot);
        var stateBefore = await File.ReadAllBytesAsync(GetStatePath(dataRoot, InstallationKey));
        var gameBefore = SnapshotDirectory(gameRoot);

        await using (var archive = new FileStream(
                         es.Package.ArchivePath,
                         FileMode.Append,
                         FileAccess.Write,
                         FileShare.None))
        {
            archive.WriteByte(0x7f);
            await archive.FlushAsync();
        }

        await AssertThrowsAsync<ManagedFilesInstallException>(
            () => installer.ApplyVariantAsync(
                es.Package,
                es.Product,
                gameRoot,
                expectedProductId: RuProductId),
            "A package changed after validation was applied to the live slot.");
        var stateAfter = await File.ReadAllBytesAsync(
            GetStatePath(dataRoot, InstallationKey));
        Assert(
            stateBefore.AsSpan().SequenceEqual(stateAfter),
            "Corrupted archive handling rewrote the old state.");
        Assert(gameBefore == SnapshotDirectory(gameRoot),
            "Corrupted archive handling changed the old files.");
        AssertState(await stateStore.LoadAsync(InstallationKey), RuProductId, isEnabled: true);
        AssertNoOperationArtifacts(dataRoot, gameRoot);
    }

    private static async Task CheckFailureAndInterruptionRecoveryAsync(string root)
    {
        await CheckInjectedTransitionAsync(
            Path.Combine(root, "failure-before-commit"),
            ManagedInstallerPhase.ActivationComplete,
            simulateInterruption: false);
        await CheckInjectedTransitionAsync(
            Path.Combine(root, "failure-after-commit"),
            ManagedInstallerPhase.StateCommitted,
            simulateInterruption: false);
        await CheckInjectedTransitionAsync(
            Path.Combine(root, "interruption-before-commit"),
            ManagedInstallerPhase.ActivationComplete,
            simulateInterruption: true);
        await CheckInjectedTransitionAsync(
            Path.Combine(root, "interruption-after-commit"),
            ManagedInstallerPhase.StateCommitted,
            simulateInterruption: true);
    }

    private static async Task CheckInjectedTransitionAsync(
        string root,
        ManagedInstallerPhase injectedPhase,
        bool simulateInterruption)
    {
        var dataRoot = Path.Combine(root, "data");
        var gameRoot = CreateGameRoot(root);
        var packagesRoot = Path.Combine(root, "packages");
        var ru = await CreateLanguagePackageAsync(
            packagesRoot,
            RuProductId,
            "ru",
            new PackageFile("ru-shared.pak", SharedDestination, RuPayload),
            new PackageFile("ru-only.pak", RuOnlyDestination, RuOnlyPayload));
        var es = await CreateLanguagePackageAsync(
            packagesRoot,
            EsProductId,
            "es",
            new PackageFile("es-shared.pak", SharedDestination, EsPayload),
            new PackageFile("es-only.pak", EsOnlyDestination, EsOnlyPayload));
        var stateStore = new InstallationStateStore(dataRoot);
        await new ManagedFilesInstaller(stateStore).InstallAsync(
            ru.Package,
            ru.Product,
            gameRoot);
        var failingInstaller = new ManagedFilesInstaller(stateStore)
        {
            PhaseObserver = phase => ThrowAtPhaseAsync(
                phase,
                injectedPhase,
                simulateInterruption)
        };

        if (simulateInterruption)
        {
            await AssertThrowsAsync<ManagedInstallerInterruptionException>(
                () => failingInstaller.ApplyVariantAsync(
                    es.Package,
                    es.Product,
                    gameRoot,
                    expectedProductId: RuProductId),
                $"Injected interruption at {injectedPhase} did not escape recovery.");
            Assert(File.Exists(GetJournalPath(dataRoot, InstallationKey)),
                "Simulated interruption did not retain its durable journal.");
            var persistedBeforeRecovery = await stateStore.LoadAsync(InstallationKey);
            AssertState(
                persistedBeforeRecovery,
                injectedPhase == ManagedInstallerPhase.StateCommitted
                    ? EsProductId
                    : RuProductId,
                isEnabled: true);
            await new ManagedFilesInstaller(stateStore).RecoverPendingOperationsAsync();
        }
        else
        {
            await AssertThrowsAsync<InvalidOperationException>(
                () => failingInstaller.ApplyVariantAsync(
                    es.Package,
                    es.Product,
                    gameRoot,
                    expectedProductId: RuProductId),
                $"Injected failure at {injectedPhase} was not reported.");
        }

        var committedNewState = injectedPhase == ManagedInstallerPhase.StateCommitted;
        AssertState(
            await stateStore.LoadAsync(InstallationKey),
            committedNewState ? EsProductId : RuProductId,
            isEnabled: true);
        if (committedNewState)
        {
            AssertManagedFile(gameRoot, SharedDestination, EsPayload, isEnabled: true);
            AssertManagedFile(gameRoot, EsOnlyDestination, EsOnlyPayload, isEnabled: true);
            AssertManagedPathAbsent(gameRoot, RuOnlyDestination);
        }
        else
        {
            AssertManagedFile(gameRoot, SharedDestination, RuPayload, isEnabled: true);
            AssertManagedFile(gameRoot, RuOnlyDestination, RuOnlyPayload, isEnabled: true);
            AssertManagedPathAbsent(gameRoot, EsOnlyDestination);
        }

        AssertNoOperationArtifacts(dataRoot, gameRoot);
    }

    private static Task ThrowAtPhaseAsync(
        ManagedInstallerPhase actualPhase,
        ManagedInstallerPhase injectedPhase,
        bool simulateInterruption)
    {
        if (actualPhase != injectedPhase)
        {
            return Task.CompletedTask;
        }

        if (simulateInterruption)
        {
            throw new ManagedInstallerInterruptionException(
                $"Injected interruption at {injectedPhase}.");
        }

        throw new InvalidOperationException($"Injected failure at {injectedPhase}.");
    }

    private static async Task CheckUnknownThirdStateFailsClosedAsync(string root)
    {
        var dataRoot = Path.Combine(root, "data");
        var gameRoot = CreateGameRoot(root);
        var packagesRoot = Path.Combine(root, "packages");
        var ru = await CreateLanguagePackageAsync(
            packagesRoot,
            RuProductId,
            "ru",
            new PackageFile("ru.pak", LanguageDestination, RuPayload));
        var es = await CreateLanguagePackageAsync(
            packagesRoot,
            EsProductId,
            "es",
            new PackageFile("es.pak", LanguageDestination, EsPayload));
        var stateStore = new InstallationStateStore(dataRoot);
        await new ManagedFilesInstaller(stateStore).InstallAsync(
            ru.Package,
            ru.Product,
            gameRoot);
        var interrupted = new ManagedFilesInstaller(stateStore)
        {
            PhaseObserver = phase => ThrowAtPhaseAsync(
                phase,
                ManagedInstallerPhase.ActivationComplete,
                simulateInterruption: true)
        };
        await AssertThrowsAsync<ManagedInstallerInterruptionException>(
            () => interrupted.ApplyVariantAsync(
                es.Package,
                es.Product,
                gameRoot,
                expectedProductId: RuProductId),
            "Unknown-state fixture did not retain an interrupted transition.");

        var oldState = await stateStore.LoadAsync(InstallationKey)
            ?? throw new InvalidOperationException("Interrupted fixture lost its old state.");
        var thirdState = oldState with
        {
            ProductId = ThirdProductId,
            Version = "9.9.9",
            PackageSha256 = new string('f', 64),
            InstalledAt = oldState.InstalledAt.AddMinutes(1)
        };
        await stateStore.SaveAsync(thirdState);
        var journalPath = GetJournalPath(dataRoot, InstallationKey);
        var journalBefore = await File.ReadAllBytesAsync(journalPath);
        var stateBefore = await File.ReadAllBytesAsync(GetStatePath(dataRoot, InstallationKey));
        var gameBefore = SnapshotDirectory(gameRoot);

        var recoveryInstaller = new ManagedFilesInstaller(stateStore);
        await AssertThrowsAsync<ManagedFilesInstallException>(
            () => recoveryInstaller.RecoverPendingOperationsAsync(),
            "Recovery accepted a persisted state matching neither old nor new.");
        await AssertThrowsAsync<ManagedFilesInstallException>(
            () => recoveryInstaller.SetEnabledAsync(
                InstallationKey,
                ThirdProductId,
                isEnabled: false),
            "A mutation proceeded while unknown-state evidence was pending.");

        var journalAfter = await File.ReadAllBytesAsync(journalPath);
        var stateAfter = await File.ReadAllBytesAsync(
            GetStatePath(dataRoot, InstallationKey));
        Assert(journalBefore.AsSpan().SequenceEqual(journalAfter),
            "Unknown-state recovery changed its journal evidence.");
        Assert(stateBefore.AsSpan().SequenceEqual(stateAfter),
            "Unknown-state recovery changed the third persisted state.");
        Assert(gameBefore == SnapshotDirectory(gameRoot),
            "Unknown-state recovery changed transitional file evidence.");
    }

    private static async Task CheckConcurrentInstallersAsync(string root)
    {
        var dataRoot = Path.Combine(root, "data");
        var gameRoot = CreateGameRoot(root);
        var packagesRoot = Path.Combine(root, "packages");
        var ru = await CreateLanguagePackageAsync(
            packagesRoot,
            RuProductId,
            "ru",
            new PackageFile("ru.pak", LanguageDestination, RuPayload));
        var es = await CreateLanguagePackageAsync(
            packagesRoot,
            EsProductId,
            "es",
            new PackageFile("es.pak", LanguageDestination, EsPayload));
        var forge = await CreateForgePackageAsync(
            packagesRoot,
            new PackageFile("forge.pak", ForgeDestination, ForgePayload));
        var stateStore = new InstallationStateStore(dataRoot);
        await new ManagedFilesInstaller(stateStore).InstallAsync(
            ru.Package,
            ru.Product,
            gameRoot);

        await CheckSameSlotSerializationAsync(stateStore, gameRoot, ru, es);
        await CheckCancelledSlotWaitAsync(stateStore);
        await CheckUnrelatedSlotIndependenceAsync(stateStore, gameRoot, forge);

        AssertState(await stateStore.LoadAsync(InstallationKey), RuProductId, isEnabled: true);
        AssertState(await stateStore.LoadAsync(ForgeProductId), ForgeProductId, isEnabled: true);
        AssertManagedFile(gameRoot, ForgeDestination, ForgePayload, isEnabled: true);
        AssertNoOperationArtifacts(dataRoot, gameRoot);
    }

    private static async Task CheckSameSlotSerializationAsync(
        InstallationStateStore stateStore,
        string gameRoot,
        PackageFixture ru,
        PackageFixture es)
    {
        var firstEntered = NewSignal();
        var releaseFirst = NewSignal();
        var secondEntered = NewSignal();
        var firstInstaller = new ManagedFilesInstaller(stateStore)
        {
            PhaseObserver = phase => BlockAtLockAsync(phase, firstEntered, releaseFirst)
        };
        var secondInstaller = new ManagedFilesInstaller(stateStore)
        {
            PhaseObserver = phase =>
            {
                if (phase == ManagedInstallerPhase.LockAcquired)
                {
                    secondEntered.TrySetResult(true);
                }

                return Task.CompletedTask;
            }
        };
        var firstTask = firstInstaller.ApplyVariantAsync(
            es.Package,
            es.Product,
            gameRoot,
            expectedProductId: RuProductId);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var secondTask = secondInstaller.ApplyVariantAsync(
            ru.Package,
            ru.Product,
            gameRoot,
            expectedProductId: EsProductId);
        try
        {
            var prematureEntry = await Task.WhenAny(
                secondEntered.Task,
                Task.Delay(TimeSpan.FromMilliseconds(200)));
            Assert(!ReferenceEquals(prematureEntry, secondEntered.Task),
                "A second installer entered the same slot while the first held its lock.");
        }
        finally
        {
            releaseFirst.TrySetResult(true);
        }

        await Task.WhenAll(firstTask, secondTask);
        Assert(secondEntered.Task.IsCompleted,
            "The serialized second installer never acquired the slot lock.");
        AssertState(await stateStore.LoadAsync(InstallationKey), RuProductId, isEnabled: true);
        AssertManagedFile(gameRoot, LanguageDestination, RuPayload, isEnabled: true);
    }

    private static async Task CheckCancelledSlotWaitAsync(InstallationStateStore stateStore)
    {
        var holderEntered = NewSignal();
        var releaseHolder = NewSignal();
        var holder = new ManagedFilesInstaller(stateStore)
        {
            PhaseObserver = phase => BlockAtLockAsync(phase, holderEntered, releaseHolder)
        };
        var holderTask = holder.SetEnabledAsync(
            InstallationKey,
            RuProductId,
            isEnabled: true);
        await holderEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var cancellation = new CancellationTokenSource();
        var waitingTask = new ManagedFilesInstaller(stateStore).SetEnabledAsync(
            InstallationKey,
            RuProductId,
            isEnabled: false,
            cancellation.Token);
        cancellation.Cancel();
        try
        {
            await AssertThrowsAsync<OperationCanceledException>(
                () => waitingTask,
                "Cancellation did not stop a waiter for the shared slot lock.");
        }
        finally
        {
            releaseHolder.TrySetResult(true);
        }

        await holderTask;
        AssertState(await stateStore.LoadAsync(InstallationKey), RuProductId, isEnabled: true);
    }

    private static async Task CheckUnrelatedSlotIndependenceAsync(
        InstallationStateStore stateStore,
        string gameRoot,
        PackageFixture forge)
    {
        var holderEntered = NewSignal();
        var releaseHolder = NewSignal();
        var languageHolder = new ManagedFilesInstaller(stateStore)
        {
            PhaseObserver = phase => BlockAtLockAsync(phase, holderEntered, releaseHolder)
        };
        var holderTask = languageHolder.SetEnabledAsync(
            InstallationKey,
            RuProductId,
            isEnabled: true);
        await holderEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var forgeTask = new ManagedFilesInstaller(stateStore).InstallAsync(
            forge.Package,
            forge.Product,
            gameRoot);
        try
        {
            var completed = await Task.WhenAny(
                forgeTask,
                Task.Delay(TimeSpan.FromSeconds(3)));
            Assert(ReferenceEquals(completed, forgeTask),
                "An unrelated Forge slot was blocked by the language slot lock.");
            await forgeTask;
        }
        finally
        {
            releaseHolder.TrySetResult(true);
        }

        await holderTask;
    }

    private static Task BlockAtLockAsync(
        ManagedInstallerPhase phase,
        TaskCompletionSource<bool> entered,
        TaskCompletionSource<bool> release)
    {
        if (phase != ManagedInstallerPhase.LockAcquired)
        {
            return Task.CompletedTask;
        }

        entered.TrySetResult(true);
        return release.Task;
    }

    private static async Task CheckFreshAdoptionRollbackAsync(string root)
    {
        var dataRoot = Path.Combine(root, "data");
        var gameRoot = CreateGameRoot(root);
        var package = await CreateLanguagePackageAsync(
            Path.Combine(root, "packages"),
            RuProductId,
            "ru",
            new PackageFile("adopted.pak", AdoptedDestination, AdoptedPayload),
            new PackageFile("fresh.pak", FreshDestination, FreshPayload));
        WriteManagedFile(gameRoot, AdoptedDestination, AdoptedPayload);
        var stateStore = new InstallationStateStore(dataRoot);
        var interrupted = new ManagedFilesInstaller(stateStore)
        {
            PhaseObserver = phase => ThrowAtPhaseAsync(
                phase,
                ManagedInstallerPhase.ActivationComplete,
                simulateInterruption: true)
        };
        await AssertThrowsAsync<ManagedInstallerInterruptionException>(
            () => interrupted.InstallAsync(package.Package, package.Product, gameRoot),
            "Fresh adoption interruption did not retain a recoverable journal.");
        Assert(await stateStore.LoadAsync(InstallationKey) is null,
            "Fresh adoption committed state before the injected interruption.");
        Assert(File.Exists(GetJournalPath(dataRoot, InstallationKey)),
            "Fresh adoption interruption did not retain its journal.");

        await new ManagedFilesInstaller(stateStore).RecoverPendingOperationsAsync();
        Assert(await stateStore.LoadAsync(InstallationKey) is null,
            "Fresh adoption rollback unexpectedly committed installation state.");
        AssertManagedFile(gameRoot, AdoptedDestination, AdoptedPayload, isEnabled: true);
        AssertManagedPathAbsent(gameRoot, FreshDestination);
        AssertNoOperationArtifacts(dataRoot, gameRoot);
    }

    private static async Task CheckLegacyTruncatedStageRecoveryAsync(string root)
    {
        const string operationId = "11111111111111111111111111111111";
        var dataRoot = Path.Combine(root, "data");
        var gameRoot = CreateGameRoot(root);
        var oldState = CreateLegacyState(
            version: "1.0.0",
            gameRoot,
            RuPayload,
            installedAt: DateTimeOffset.UnixEpoch);
        var newState = CreateLegacyState(
            version: "1.1.0",
            gameRoot,
            EsPayload,
            installedAt: DateTimeOffset.UnixEpoch.AddMinutes(1));
        WriteManagedFile(gameRoot, LanguageDestination, RuPayload);

        var serializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        var stateRoot = Path.Combine(dataRoot, "state", "installations");
        var transactionsRoot = Path.Combine(dataRoot, "state", "transactions");
        Directory.CreateDirectory(stateRoot);
        Directory.CreateDirectory(transactionsRoot);
        await File.WriteAllBytesAsync(
            Path.Combine(stateRoot, $"{RuProductId}.json"),
            JsonSerializer.SerializeToUtf8Bytes(oldState, serializerOptions));
        await File.WriteAllBytesAsync(
            Path.Combine(transactionsRoot, $"{RuProductId}.update.json"),
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    schemaVersion = 1,
                    operationId,
                    productId = RuProductId,
                    oldState,
                    newState
                },
                serializerOptions));

        var finalPath = ResolveManagedPath(gameRoot, LanguageDestination);
        var stagePath = $"{finalPath}.nfg-update-stage-{operationId}.disabled";
        var unrelatedPartialPath = $"{stagePath}.partial";
        var truncatedBytes = EsPayload[..Math.Max(1, EsPayload.Length / 2)];
        await File.WriteAllBytesAsync(stagePath, truncatedBytes);
        var unrelatedPartial = Encoding.UTF8.GetBytes("legacy installer never owned this partial");
        await File.WriteAllBytesAsync(unrelatedPartialPath, unrelatedPartial);

        var stateStore = new InstallationStateStore(dataRoot);
        await new ManagedFilesInstaller(stateStore).RecoverPendingOperationsAsync();

        var recoveredState = await stateStore.LoadAsync(RuProductId)
            ?? throw new InvalidOperationException("Legacy recovery lost the old state.");
        Assert(recoveredState.SchemaVersion == 1 &&
               recoveredState.InstallationKey is null &&
               recoveredState.ProductId == RuProductId &&
               recoveredState.Version == "1.0.0" &&
               recoveredState.IsEnabled,
            "Legacy recovery changed the persisted schema-v1 state.");
        AssertManagedFile(gameRoot, LanguageDestination, RuPayload, isEnabled: true);
        Assert(!File.Exists(stagePath),
            "Legacy recovery retained a truncated canonical stage file.");
        Assert(!File.Exists(Path.Combine(transactionsRoot, $"{RuProductId}.update.json")),
            "Legacy recovery retained a completed schema-v1 journal.");
        Assert(File.ReadAllBytes(unrelatedPartialPath).AsSpan().SequenceEqual(unrelatedPartial),
            "Legacy recovery deleted a partial path the v1 installer never owned.");
    }

    private static async Task CheckExternalExactTargetIsPreservedAsync(string root)
    {
        var dataRoot = Path.Combine(root, "data");
        var gameRoot = CreateGameRoot(root);
        var package = await CreateLanguagePackageAsync(
            Path.Combine(root, "packages"),
            RuProductId,
            "ru",
            new PackageFile("external-exact.pak", LanguageDestination, RuPayload));
        var targetPath = ResolveManagedPath(gameRoot, LanguageDestination);
        var stateStore = new InstallationStateStore(dataRoot);
        FileStream? lockedReadyMarker = null;
        var installer = new ManagedFilesInstaller(stateStore)
        {
            PhaseObserver = phase =>
            {
                if (phase == ManagedInstallerPhase.JournalDurable)
                {
                    WriteManagedFile(gameRoot, LanguageDestination, RuPayload);
                }
                else if (phase == ManagedInstallerPhase.StagingComplete)
                {
                    var readyMarkerPath = Directory
                        .EnumerateFiles(
                            gameRoot,
                            "*.ready-owner",
                            SearchOption.AllDirectories)
                        .Single();
                    lockedReadyMarker = new FileStream(
                        readyMarkerPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read);
                }

                return Task.CompletedTask;
            }
        };

        try
        {
            await AssertThrowsAsync<ManagedFilesInstallException>(
                () => installer.InstallAsync(package.Package, package.Product, gameRoot),
                "An exact external target created after the journal was silently adopted or removed.");
            Assert(lockedReadyMarker is not null,
                "Retry fixture did not hold the ready marker across first recovery.");
            Assert(await stateStore.LoadAsync(InstallationKey) is null,
                "A failed activation committed state for an external exact target.");
            Assert(File.ReadAllBytes(targetPath).AsSpan().SequenceEqual(RuPayload),
                "First rollback attempt deleted or changed an exact external target.");
            Assert(File.Exists(GetJournalPath(dataRoot, InstallationKey)),
                "Incomplete first rollback discarded its retry journal.");
        }
        finally
        {
            lockedReadyMarker?.Dispose();
            lockedReadyMarker = null;
        }

        await new ManagedFilesInstaller(stateStore).RecoverPendingOperationsAsync();
        Assert(await stateStore.LoadAsync(InstallationKey) is null,
            "Retry recovery committed state for an external exact target.");
        Assert(File.ReadAllBytes(targetPath).AsSpan().SequenceEqual(RuPayload),
            "Retry recovery deleted or changed an exact external target.");
        AssertNoOperationArtifacts(dataRoot, gameRoot);
    }

    private static async Task CheckUnverifiedPartialFailsClosedAsync(string root)
    {
        var dataRoot = Path.Combine(root, "data");
        var gameRoot = CreateGameRoot(root);
        var packagesRoot = Path.Combine(root, "packages");
        var ru = await CreateLanguagePackageAsync(
            packagesRoot,
            RuProductId,
            "ru",
            new PackageFile("ru.pak", LanguageDestination, RuPayload));
        var es = await CreateLanguagePackageAsync(
            packagesRoot,
            EsProductId,
            "es",
            new PackageFile("es.pak", LanguageDestination, EsPayload));
        var stateStore = new InstallationStateStore(dataRoot);
        await new ManagedFilesInstaller(stateStore).InstallAsync(
            ru.Package,
            ru.Product,
            gameRoot);
        var statePath = GetStatePath(dataRoot, InstallationKey);
        var stateBefore = await File.ReadAllBytesAsync(statePath);
        var arbitraryPartial = Encoding.UTF8.GetBytes("unverified external partial evidence");
        string? partialPath = null;
        var installer = new ManagedFilesInstaller(stateStore)
        {
            PhaseObserver = async phase =>
            {
                if (phase != ManagedInstallerPhase.JournalDurable)
                {
                    return;
                }

                var journalPath = Directory
                    .EnumerateFiles(
                        Path.Combine(dataRoot, "state", "transactions"),
                        "*.update.json",
                        SearchOption.TopDirectoryOnly)
                    .Single();
                using var journal = JsonDocument.Parse(await File.ReadAllBytesAsync(journalPath));
                var operationId = journal.RootElement.GetProperty("operationId").GetString()
                    ?? throw new InvalidOperationException("Journal fixture has no operation id.");
                var finalPath = ResolveManagedPath(gameRoot, LanguageDestination);
                partialPath = $"{finalPath}.nfg-update-stage-{operationId}.disabled.partial";
                await File.WriteAllBytesAsync(partialPath, arbitraryPartial);
            }
        };

        await AssertThrowsAsync<ManagedFilesInstallException>(
            () => installer.ApplyVariantAsync(
                es.Package,
                es.Product,
                gameRoot,
                expectedProductId: RuProductId),
            "Rollback accepted an unverified partial created after the durable journal.");

        var retainedPartialPath = partialPath
            ?? throw new InvalidOperationException("Partial evidence path was not recorded.");
        Assert(File.Exists(retainedPartialPath),
            "Fail-closed recovery deleted unverified partial evidence.");
        Assert(File.ReadAllBytes(retainedPartialPath).AsSpan().SequenceEqual(arbitraryPartial),
            "Fail-closed recovery changed unverified partial evidence.");
        var stateAfter = await File.ReadAllBytesAsync(statePath);
        Assert(stateBefore.AsSpan().SequenceEqual(stateAfter),
            "Unverified partial recovery rewrote the persisted old state.");
        AssertState(await stateStore.LoadAsync(InstallationKey), RuProductId, isEnabled: true);
        AssertManagedFile(gameRoot, LanguageDestination, RuPayload, isEnabled: true);
        Assert(File.Exists(GetJournalPath(dataRoot, InstallationKey)),
            "Fail-closed partial recovery discarded its durable journal evidence.");
    }

    private static InstalledProductState CreateLegacyState(
        string version,
        string gameRoot,
        byte[] contents,
        DateTimeOffset installedAt) => new()
        {
            SchemaVersion = 1,
            InstallationKey = null,
            ProductId = RuProductId,
            Version = version,
            PackageSizeBytes = contents.LongLength,
            PackageSha256 = Sha256(contents),
            SteamAppId = SteamAppId,
            SteamBuildId = SteamBuildId,
            DetectedSteamBuildId = SteamBuildId,
            GameRoot = gameRoot,
            InstalledAt = installedAt,
            IsEnabled = true,
            Files =
            [
                new InstalledFileState
                {
                    Destination = LanguageDestination,
                    SizeBytes = contents.LongLength,
                    Sha256 = Sha256(contents)
                }
            ]
        };

    private static async Task<PackageFixture> CreateLanguagePackageAsync(
        string root,
        string productId,
        string locale,
        params PackageFile[] files) =>
        await CreatePackageAsync(root, productId, locale, files);

    private static async Task<PackageFixture> CreateForgePackageAsync(
        string root,
        params PackageFile[] files) =>
        await CreatePackageAsync(root, ForgeProductId, locale: null, files);

    internal static async Task<PackageFixture> CreatePackageAsync(
        string root,
        string productId,
        string? locale,
        IReadOnlyList<PackageFile> files,
        string version = Version)
    {
        Directory.CreateDirectory(root);
        var bytes = BuildPackageBytes(productId, files, version);
        var packageSha256 = Sha256(bytes);
        var product = CreateProduct(
            productId,
            locale,
            bytes.LongLength,
            packageSha256,
            version);
        var path = Path.Combine(
            root,
            $"{productId}-{packageSha256[..12]}.zip");
        await File.WriteAllBytesAsync(path, bytes);
        var package = await new PackageArchiveService().ValidateAsync(path, product);
        return new PackageFixture(product, package, files);
    }

    private static byte[] BuildPackageBytes(
        string productId,
        IReadOnlyList<PackageFile> files,
        string version)
    {
        var manifest = new
        {
            schema = "nfg-package/1",
            productId,
            version,
            strategy = "managed-files",
            steam = new { appId = SteamAppId, buildId = SteamBuildId },
            files = files.Select(file => new
            {
                source = file.SourceName,
                destination = $"steam-game/{file.Destination}",
                sizeBytes = file.Contents.LongLength,
                sha256 = Sha256(file.Contents)
            }).ToArray()
        };

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                WriteEntry(archive, $"Fixture/{file.SourceName}", file.Contents);
            }

            WriteEntry(
                archive,
                "Fixture/nfg-package.json",
                JsonSerializer.SerializeToUtf8Bytes(manifest));
        }

        return output.ToArray();
    }

    private static ProductManifest CreateProduct(
        string productId,
        string? locale,
        long packageSize,
        string packageSha256,
        string version) => new()
        {
            SchemaVersion = locale is null ? 1 : 2,
            Id = productId,
            Type = locale is null ? "mod" : "localization",
            FamilyId = locale is null ? null : FamilyId,
            Locale = locale,
            ExclusiveGroup = locale is null ? null : InstallationKey,
            Display = new ProductDisplay
            {
                Title = locale is null ? "Anvil Forge Helper" : "Anvil Empires",
                Subtitle = locale is null ? "Forge fixture" : $"{locale} fixture",
                Summary = "Managed variant smoke fixture",
                Description = "Managed variant smoke fixture",
                Features = ["Transactional fixture"]
            },
            Release = new ProductRelease
            {
                Version = version,
                Channel = "stable",
                GameVersion = $"steam-build-{SteamBuildId}",
                Highlights = [],
                KnownIssues = [],
                Payload = new ProductPayload
                {
                    Url = new Uri($"https://packages.test/{productId}/{version}.zip"),
                    SizeBytes = packageSize,
                    Sha256 = packageSha256
                }
            },
            Releases = [],
            Compatibility = new ProductCompatibility
            {
                Platforms = ["windows-x64"],
                GameVersion = $"steam-build-{SteamBuildId}",
                Status = "compatible"
            },
            Installation = new ProductInstallation
            {
                Strategy = "managed-files",
                SupportsRollback = true,
                RequiresElevation = "auto",
                Detection =
                [
                    new ProductDetectionRule
                    {
                        Provider = "steam",
                        ProductId = SteamAppId
                    }
                ]
            },
            Dependencies = [],
            Progress = new ProductProgress { Label = "Smoke" }
        };

    private static void WriteEntry(ZipArchive archive, string path, byte[] contents)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        stream.Write(contents);
    }

    private static string CreateGameRoot(string root)
    {
        var gameRoot = Path.Combine(root, "game");
        Directory.CreateDirectory(
            Path.Combine(gameRoot, "Anvil", "Content", "Paks"));
        return gameRoot;
    }

    private static void WriteManagedFile(
        string gameRoot,
        string destination,
        byte[] contents)
    {
        var path = ResolveManagedPath(gameRoot, destination);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, contents);
    }

    private static void AssertManagedFile(
        string gameRoot,
        string destination,
        byte[] expectedContents,
        bool isEnabled)
    {
        var activePath = ResolveManagedPath(gameRoot, destination);
        var disabledPath = $"{activePath}.nfg-disabled";
        var expectedPath = isEnabled ? activePath : disabledPath;
        var unexpectedPath = isEnabled ? disabledPath : activePath;
        Assert(File.Exists(expectedPath), $"Expected managed file '{expectedPath}' is missing.");
        Assert(!File.Exists(unexpectedPath) && !Directory.Exists(unexpectedPath),
            $"Unexpected activation copy '{unexpectedPath}' exists.");
        Assert(File.ReadAllBytes(expectedPath).AsSpan().SequenceEqual(expectedContents),
            $"Managed file '{expectedPath}' has unexpected contents.");
    }

    private static void AssertManagedPathAbsent(string gameRoot, string destination)
    {
        var activePath = ResolveManagedPath(gameRoot, destination);
        Assert(
            !File.Exists(activePath) &&
            !Directory.Exists(activePath) &&
            !File.Exists($"{activePath}.nfg-disabled") &&
            !Directory.Exists($"{activePath}.nfg-disabled"),
            $"Removed managed destination '{destination}' still exists.");
    }

    private static string ResolveManagedPath(string gameRoot, string destination) =>
        Path.Combine(
            gameRoot,
            destination.Replace('/', Path.DirectorySeparatorChar));

    private static void AssertState(
        InstalledProductState? state,
        string productId,
        bool isEnabled,
        string version = Version)
    {
        var actual = state
            ?? throw new InvalidOperationException(
                $"Installation state for '{productId}' is missing.");
        Assert(actual.SchemaVersion == 2, "Managed variant state is not schema v2.");
        Assert(actual.InstallationKey == InstallationKey || productId == ForgeProductId &&
            actual.InstallationKey == ForgeProductId,
            $"Managed state for '{productId}' has the wrong installation key.");
        Assert(actual.ProductId == productId,
            $"Expected active product '{productId}', found '{actual.ProductId}'.");
        Assert(actual.Version == version,
            $"Managed state for '{productId}' has the wrong version.");
        Assert(actual.IsEnabled == isEnabled,
            $"Managed state for '{productId}' has the wrong activation state.");
    }

    private static void AssertNoOperationArtifacts(string dataRoot, params string[] gameRoots)
    {
        var transactionsRoot = Path.Combine(dataRoot, "state", "transactions");
        Assert(
            !Directory.Exists(transactionsRoot) ||
            !Directory.EnumerateFiles(transactionsRoot, "*", SearchOption.AllDirectories).Any(),
            "A completed operation or recovery left transaction files behind.");

        var stateRoot = Path.Combine(dataRoot, "state");
        Assert(
            !Directory.Exists(stateRoot) ||
            !Directory.EnumerateFiles(stateRoot, "*.tmp", SearchOption.AllDirectories).Any(),
            "A completed operation or recovery left atomic-write temp files behind.");

        string[] forbiddenMarkers =
        [
            ".nfg-update-stage-",
            ".nfg-update-old-",
            ".nfg-remove-",
            ".nfg-stage-"
        ];
        foreach (var gameRoot in gameRoots.Where(Directory.Exists))
        {
            var leftovers = Directory
                .EnumerateFiles(gameRoot, "*", SearchOption.AllDirectories)
                .Where(path => forbiddenMarkers.Any(marker =>
                    Path.GetFileName(path).Contains(marker, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            Assert(leftovers.Length == 0,
                $"Completed recovery left managed-file artifacts: {string.Join(", ", leftovers)}");
        }
    }

    private static string SnapshotDirectory(string root)
    {
        if (!Directory.Exists(root))
        {
            return string.Empty;
        }

        return string.Join(
            "\n",
            Directory
                .EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => new
                {
                    Relative = Path.GetRelativePath(root, path).Replace('\\', '/'),
                    Length = new FileInfo(path).Length,
                    Hash = Sha256(File.ReadAllBytes(path))
                })
                .OrderBy(file => file.Relative, StringComparer.OrdinalIgnoreCase)
                .Select(file => $"{file.Relative}|{file.Length}|{file.Hash}"));
    }

    private static string GetStatePath(string dataRoot, string installationKey) =>
        Path.Combine(
            dataRoot,
            "state",
            "installations",
            $"{installationKey}.json");

    private static string GetJournalPath(string dataRoot, string installationKey) =>
        Path.Combine(
            dataRoot,
            "state",
            "transactions",
            $"{installationKey}.update.json");

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task<TException> AssertThrowsAsync<TException>(
        Func<Task> action,
        string message)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException(message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    internal sealed record PackageFile(
        string SourceName,
        string Destination,
        byte[] Contents);

    internal sealed record PackageFixture(
        ProductManifest Product,
        ValidatedPackage Package,
        IReadOnlyList<PackageFile> Files);

    private sealed class OfflinePackageHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            throw new HttpRequestException("Synthetic offline fixture; no network request was sent.");
        }
    }
}
