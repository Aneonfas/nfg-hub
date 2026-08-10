using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nfg.Store.Contracts;
using Nfg.Store.Core;
using Nfg.Store.Installation;

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

    private static async Task<PackageFixture> CreatePackageAsync(
        string root,
        string productId,
        string? locale,
        IReadOnlyList<PackageFile> files)
    {
        Directory.CreateDirectory(root);
        var bytes = BuildPackageBytes(productId, files);
        var packageSha256 = Sha256(bytes);
        var product = CreateProduct(
            productId,
            locale,
            bytes.LongLength,
            packageSha256);
        var path = Path.Combine(
            root,
            $"{productId}-{packageSha256[..12]}.zip");
        await File.WriteAllBytesAsync(path, bytes);
        var package = await new PackageArchiveService().ValidateAsync(path, product);
        return new PackageFixture(product, package, files);
    }

    private static byte[] BuildPackageBytes(
        string productId,
        IReadOnlyList<PackageFile> files)
    {
        var manifest = new
        {
            schema = "nfg-package/1",
            productId,
            version = Version,
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
        string packageSha256) => new()
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
                Version = Version,
                Channel = "stable",
                GameVersion = $"steam-build-{SteamBuildId}",
                Highlights = [],
                KnownIssues = [],
                Payload = new ProductPayload
                {
                    Url = new Uri($"https://packages.test/{productId}/{Version}.zip"),
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
        bool isEnabled)
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
        Assert(actual.Version == Version,
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

    private static async Task AssertThrowsAsync<TException>(
        Func<Task> action,
        string message)
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

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record PackageFile(
        string SourceName,
        string Destination,
        byte[] Contents);

    private sealed record PackageFixture(
        ProductManifest Product,
        ValidatedPackage Package,
        IReadOnlyList<PackageFile> Files);
}
