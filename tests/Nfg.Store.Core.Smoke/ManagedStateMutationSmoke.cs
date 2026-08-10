using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nfg.Store.App.Services;
using Nfg.Store.Contracts;
using Nfg.Store.Core;
using Nfg.Store.Installation;
using Nfg.Store.Platform.Windows;

internal static class ManagedStateMutationSmoke
{
    private const string InstallationKey = "nfg.anvil-empires.ru";
    private const string RuProductId = "nfg.anvil-empires.ru";
    private const string EsProductId = "nfg.anvil-empires.es";
    private const string ForgeProductId = "nfg.anvil-empires.forge-helper";
    private const string FamilyId = "nfg.anvil-empires.localization";
    private const string SteamAppId = "2383950";
    private const string SteamBuildId = "24619810";
    private const string FirstDestination = "Anvil/Content/Paks/Nfg-Mutation-A.pak";
    private const string SecondDestination = "Anvil/Content/Paks/Nfg-Mutation-B.pak";

    private static readonly byte[] FirstPayload = Encoding.UTF8.GetBytes(
        "managed mutation first payload");
    private static readonly byte[] SecondPayload = Encoding.UTF8.GetBytes(
        "managed mutation second payload");
    private static readonly byte[] UpdatedPayload = Encoding.UTF8.GetBytes(
        "managed mutation updated payload");

    public static async Task RunAsync(string testRoot)
    {
        var root = Path.Combine(testRoot, "managed-state-mutation");
        await CheckSetEnabledRestartMatrixAsync(Path.Combine(root, "set-enabled"));
        await CheckUninstallRestartMatrixAsync(Path.Combine(root, "uninstall"));
        await CheckPostCommitCleanupRetryAsync(Path.Combine(root, "cleanup-retry"));
        await CheckMutationJournalValidationAsync(Path.Combine(root, "validation"));
        await CheckUnknownThirdStateFailsClosedAsync(Path.Combine(root, "third-state"));
        await CheckCoordinatorRepeatedInstallAsync(Path.Combine(root, "coordinator-repeat"));
        await CheckCoordinatorSiblingInstallRejectedAsync(
            Path.Combine(root, "coordinator-sibling"));
        await CheckExternalBackupCollisionsAsync(Path.Combine(root, "backup-collisions"));
        await CheckCrossProcessLockAsync(Path.Combine(root, "cross-process-lock"));
    }

    public static async Task HoldInstallationLockAsync(
        string dataRoot,
        string installationKey,
        string readyPath,
        string releasePath)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await new InstallationOperationLock(dataRoot).ExecuteAsync(
            installationKey,
            timeout.Token,
            async () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(readyPath)!);
                await File.WriteAllTextAsync(readyPath, "locked", timeout.Token);
                while (!File.Exists(releasePath))
                {
                    await Task.Delay(25, timeout.Token);
                }

                return true;
            });
    }

    private static async Task CheckSetEnabledRestartMatrixAsync(string root)
    {
        foreach (var (oldEnabled, newEnabled, direction) in new[]
                 {
                     (true, false, "disable"),
                     (false, true, "enable")
                 })
        {
            foreach (var phase in MutationBoundaries)
            {
                var caseRoot = Path.Combine(root, direction, phase.ToString());
                var fixture = await CreateInstalledFixtureAsync(caseRoot, oldEnabled);
                var completedFileMutations = 0;
                var interrupted = new ManagedFilesInstaller(fixture.StateStore)
                {
                    PhaseObserver = value => InterruptAtBoundaryAsync(
                        value,
                        phase,
                        ref completedFileMutations)
                };

                await AssertThrowsAsync<ManagedInstallerInterruptionException>(
                    () => interrupted.SetEnabledAsync(
                        InstallationKey,
                        RuProductId,
                        newEnabled),
                    $"SetEnabled {direction} did not stop at {phase}.");
                Assert(File.Exists(GetMutationJournalPath(fixture.DataRoot)),
                    $"SetEnabled {direction} did not retain its journal at {phase}.");

                var stateBeforeRecovery = await fixture.StateStore.LoadAsync(InstallationKey);
                AssertState(
                    stateBeforeRecovery,
                    RuProductId,
                    phase == ManagedInstallerPhase.StateCommitted
                        ? newEnabled
                        : oldEnabled);
                if (phase == ManagedInstallerPhase.FileMutationCompleted)
                {
                    AssertManagedFile(
                        fixture.GameRoot,
                        FirstDestination,
                        FirstPayload,
                        newEnabled);
                    AssertManagedFile(
                        fixture.GameRoot,
                        SecondDestination,
                        SecondPayload,
                        oldEnabled);
                }

                var restarted = new ManagedFilesInstaller(fixture.StateStore);
                await restarted.RecoverPendingOperationsAsync();
                var expectedEnabled = phase == ManagedInstallerPhase.StateCommitted
                    ? newEnabled
                    : oldEnabled;
                AssertState(
                    await fixture.StateStore.LoadAsync(InstallationKey),
                    RuProductId,
                    expectedEnabled);
                AssertManagedFiles(fixture.GameRoot, expectedEnabled);
                AssertNoMutationArtifacts(fixture.DataRoot, fixture.GameRoot);

                var snapshot = SnapshotDirectory(fixture.GameRoot);
                await restarted.RecoverPendingOperationsAsync();
                Assert(snapshot == SnapshotDirectory(fixture.GameRoot),
                    $"Repeated SetEnabled {direction} recovery at {phase} was not idempotent.");
                AssertNoMutationArtifacts(fixture.DataRoot, fixture.GameRoot);
            }
        }
    }

    private static async Task CheckUninstallRestartMatrixAsync(string root)
    {
        foreach (var oldEnabled in new[] { true, false })
        {
            var activation = oldEnabled ? "enabled" : "disabled";
            foreach (var phase in MutationBoundaries)
            {
                var caseRoot = Path.Combine(root, activation, phase.ToString());
                var fixture = await CreateInstalledFixtureAsync(caseRoot, oldEnabled);
                var completedFileMutations = 0;
                var interrupted = new ManagedFilesInstaller(fixture.StateStore)
                {
                    PhaseObserver = value => InterruptAtBoundaryAsync(
                        value,
                        phase,
                        ref completedFileMutations)
                };

                await AssertThrowsAsync<ManagedInstallerInterruptionException>(
                    () => interrupted.UninstallAsync(InstallationKey, RuProductId),
                    $"Uninstall of an {activation} slot did not stop at {phase}.");
                Assert(File.Exists(GetMutationJournalPath(fixture.DataRoot)),
                    $"Uninstall did not retain its journal at {phase}.");

                var stateBeforeRecovery = await fixture.StateStore.LoadAsync(InstallationKey);
                if (phase == ManagedInstallerPhase.StateCommitted)
                {
                    Assert(stateBeforeRecovery is null,
                        "Committed uninstall still had persisted installation state.");
                }
                else
                {
                    AssertState(stateBeforeRecovery, RuProductId, oldEnabled);
                }

                if (phase == ManagedInstallerPhase.FileMutationCompleted)
                {
                    AssertMixedUninstallEvidence(fixture.GameRoot, oldEnabled);
                }

                var restarted = new ManagedFilesInstaller(fixture.StateStore);
                await restarted.RecoverPendingOperationsAsync();
                if (phase == ManagedInstallerPhase.StateCommitted)
                {
                    Assert(await fixture.StateStore.LoadAsync(InstallationKey) is null,
                        "Committed uninstall recovery restored deleted state.");
                    AssertManagedFilesAbsent(fixture.GameRoot);
                }
                else
                {
                    AssertState(
                        await fixture.StateStore.LoadAsync(InstallationKey),
                        RuProductId,
                        oldEnabled);
                    AssertManagedFiles(fixture.GameRoot, oldEnabled);
                }

                AssertNoMutationArtifacts(fixture.DataRoot, fixture.GameRoot);
                var snapshot = SnapshotDirectory(fixture.GameRoot);
                await restarted.RecoverPendingOperationsAsync();
                Assert(snapshot == SnapshotDirectory(fixture.GameRoot),
                    $"Repeated uninstall recovery at {phase} was not idempotent.");
                AssertNoMutationArtifacts(fixture.DataRoot, fixture.GameRoot);
            }
        }
    }

    private static async Task CheckPostCommitCleanupRetryAsync(string root)
    {
        await CheckSetEnabledCleanupRetryAsync(Path.Combine(root, "set-enabled"));
        await CheckUninstallCleanupRetryAsync(Path.Combine(root, "uninstall"));
        await CheckUninstallRecreatedTombstoneAsync(
            Path.Combine(root, "uninstall-recreated-tombstone"));
    }

    private static async Task CheckSetEnabledCleanupRetryAsync(string root)
    {
        var fixture = await CreateInstalledFixtureAsync(root, isEnabled: true);
        FileStream? heldJournal = null;
        var installer = new ManagedFilesInstaller(fixture.StateStore)
        {
            PhaseObserver = phase =>
            {
                if (phase == ManagedInstallerPhase.StateCommitted)
                {
                    heldJournal = new FileStream(
                        GetMutationJournalPath(fixture.DataRoot),
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read);
                }

                return Task.CompletedTask;
            }
        };

        var state = await installer.SetEnabledAsync(
            InstallationKey,
            RuProductId,
            isEnabled: false);
        AssertState(state, RuProductId, isEnabled: false);
        Assert(File.Exists(GetMutationJournalPath(fixture.DataRoot)),
            "SetEnabled cleanup failure did not retain its retry journal.");
        heldJournal?.Dispose();

        await new ManagedFilesInstaller(fixture.StateStore)
            .RecoverPendingOperationsAsync();
        AssertState(
            await fixture.StateStore.LoadAsync(InstallationKey),
            RuProductId,
            isEnabled: false);
        AssertManagedFiles(fixture.GameRoot, isEnabled: false);
        AssertNoMutationArtifacts(fixture.DataRoot, fixture.GameRoot);
    }

    private static async Task CheckUninstallCleanupRetryAsync(string root)
    {
        var fixture = await CreateInstalledFixtureAsync(root, isEnabled: true);
        FileStream? heldJournal = null;
        var installer = new ManagedFilesInstaller(fixture.StateStore)
        {
            PhaseObserver = phase =>
            {
                if (phase == ManagedInstallerPhase.StateCommitted)
                {
                    heldJournal = new FileStream(
                        GetMutationJournalPath(fixture.DataRoot),
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read);
                }

                return Task.CompletedTask;
            }
        };

        await installer.UninstallAsync(InstallationKey, RuProductId);
        Assert(await fixture.StateStore.LoadAsync(InstallationKey) is null,
            "Uninstall cleanup failure restored committed state.");
        Assert(File.Exists(GetMutationJournalPath(fixture.DataRoot)),
            "Uninstall cleanup failure did not retain its retry journal.");
        foreach (var destination in new[] { FirstDestination, SecondDestination })
        {
            var source = ResolveManagedPath(fixture.GameRoot, destination);
            Assert(Directory.EnumerateFiles(
                    Path.GetDirectoryName(source)!,
                    $"{Path.GetFileName(source)}.nfg-remove-*",
                    SearchOption.TopDirectoryOnly).Count() == 1,
                "Uninstall deleted a tombstone before its journal became terminal.");
        }

        heldJournal?.Dispose();

        await new ManagedFilesInstaller(fixture.StateStore)
            .RecoverPendingOperationsAsync();
        Assert(await fixture.StateStore.LoadAsync(InstallationKey) is null,
            "Uninstall cleanup retry restored committed state.");
        AssertManagedFilesAbsent(fixture.GameRoot);
        AssertNoMutationArtifacts(fixture.DataRoot, fixture.GameRoot);
    }

    private static async Task CheckUninstallRecreatedTombstoneAsync(string root)
    {
        await CheckUninstallRecreatedTombstoneAtPhaseAsync(
            Path.Combine(root, "first-quarantine"),
            ManagedInstallerPhase.FileQuarantined);
        await CheckUninstallRecreatedTombstoneAtPhaseAsync(
            Path.Combine(root, "first-delete"),
            ManagedInstallerPhase.FileCleanupCompleted);
    }

    private static async Task CheckUninstallRecreatedTombstoneAtPhaseAsync(
        string root,
        ManagedInstallerPhase interruptionPhase)
    {
        var fixture = await CreateInstalledFixtureAsync(root, isEnabled: true);
        string? firstTombstone = null;
        string? secondTombstone = null;
        string? operationId = null;
        var completedTargetPhase = 0;
        var installer = new ManagedFilesInstaller(fixture.StateStore)
        {
            PhaseObserver = phase =>
            {
                if (phase == ManagedInstallerPhase.StateCommitted)
                {
                    firstTombstone = FindRemovalTombstone(
                        fixture.GameRoot,
                        FirstDestination);
                    secondTombstone = FindRemovalTombstone(
                        fixture.GameRoot,
                        SecondDestination);
                    using var journal = JsonDocument.Parse(
                        File.ReadAllBytes(GetMutationJournalPath(fixture.DataRoot)));
                    operationId = journal.RootElement
                        .GetProperty("operationId")
                        .GetString();
                }

                if (phase == interruptionPhase && ++completedTargetPhase == 1)
                {
                    throw new ManagedInstallerInterruptionException(
                        $"Injected tombstone interruption at {phase}.");
                }

                return Task.CompletedTask;
            }
        };

        await installer.UninstallAsync(InstallationKey, RuProductId);
        var deletedPath = firstTombstone
            ?? throw new InvalidOperationException("First tombstone path was not recorded.");
        var possibleOrphanPath = secondTombstone
            ?? throw new InvalidOperationException("Second tombstone path was not recorded.");
        var actualOperationId = operationId
            ?? throw new InvalidOperationException("Uninstall operation ID was not recorded.");
        var terminalPaths = new[] { deletedPath, possibleOrphanPath }
            .Select(path => GetTerminalQuarantinePath(path, actualOperationId))
            .ToArray();

        if (interruptionPhase == ManagedInstallerPhase.FileQuarantined)
        {
            Assert(File.Exists(GetMutationJournalPath(fixture.DataRoot)),
                "Uninstall lost recovery authority after its first tombstone quarantine.");
            Assert(!File.Exists(deletedPath) && File.Exists(terminalPaths[0]) &&
                   File.Exists(possibleOrphanPath) && !File.Exists(terminalPaths[1]),
                "First-quarantine interruption left the wrong tombstone state.");
            await File.WriteAllBytesAsync(deletedPath, FirstPayload);
        }
        else
        {
            Assert(!File.Exists(GetMutationJournalPath(fixture.DataRoot)),
                "Uninstall retained recovery authority after its first terminal delete.");
            Assert(!File.Exists(deletedPath) && !File.Exists(possibleOrphanPath) &&
                   !File.Exists(terminalPaths[0]) && File.Exists(terminalPaths[1]),
                "First-delete interruption left the wrong terminal tombstone state.");
            await File.WriteAllBytesAsync(terminalPaths[0], FirstPayload);
            await File.WriteAllBytesAsync(deletedPath, FirstPayload);
        }

        var remainingEvidencePath = interruptionPhase == ManagedInstallerPhase.FileQuarantined
            ? terminalPaths[0]
            : terminalPaths[1];
        var remainingEvidenceBytes = await File.ReadAllBytesAsync(remainingEvidencePath);
        if (interruptionPhase == ManagedInstallerPhase.FileQuarantined)
        {
            var journalPath = GetMutationJournalPath(fixture.DataRoot);
            var journalBefore = await File.ReadAllBytesAsync(journalPath);
            var gameBefore = SnapshotDirectory(fixture.GameRoot);
            await AssertThrowsAsync<ManagedFilesInstallException>(
                () => new ManagedFilesInstaller(fixture.StateStore)
                    .RecoverPendingOperationsAsync(),
                "Tombstone retry accepted ambiguous source+quarantine evidence.");
            await AssertThrowsAsync<ManagedFilesInstallException>(
                () => new ManagedFilesInstaller(fixture.StateStore)
                    .RecoverPendingOperationsAsync(),
                "Repeated tombstone retry accepted ambiguous evidence.");
            Assert(File.ReadAllBytes(journalPath).AsSpan().SequenceEqual(journalBefore) &&
                   SnapshotDirectory(fixture.GameRoot) == gameBefore,
                "Fail-closed tombstone retry changed transaction evidence.");
        }
        else
        {
            await new ManagedFilesInstaller(fixture.StateStore).RecoverPendingOperationsAsync();
            await new ManagedFilesInstaller(fixture.StateStore).RecoverPendingOperationsAsync();
            Assert(File.Exists(deletedPath) &&
                   File.ReadAllBytes(deletedPath).AsSpan().SequenceEqual(FirstPayload),
                "Recovery deleted a recreated tombstone source after authority revoke.");
            Assert(File.Exists(terminalPaths[0]) &&
                   File.ReadAllBytes(terminalPaths[0]).AsSpan().SequenceEqual(FirstPayload),
                "Recovery deleted a recreated terminal tombstone quarantine.");
            Assert(File.Exists(terminalPaths[1]) &&
                   File.ReadAllBytes(terminalPaths[1]).AsSpan()
                       .SequenceEqual(remainingEvidenceBytes),
                "Recovery deleted remaining tombstone evidence after authority revoke.");
        }

        Assert(await fixture.StateStore.LoadAsync(InstallationKey) is null,
            "Repeated tombstone recovery restored uninstalled state.");
        AssertManagedFilesAbsent(fixture.GameRoot);
    }

    private static async Task CheckMutationJournalValidationAsync(string root)
    {
        await CheckInvalidSetEnabledJournalAsync(
            Path.Combine(root, "is-enabled"),
            "IsEnabled",
            document =>
            {
                var oldEnabled = document["oldState"]!["isEnabled"]!.GetValue<bool>();
                document["newState"]!["isEnabled"] = oldEnabled;
            });
        await CheckInvalidSetEnabledJournalAsync(
            Path.Combine(root, "steam-app-id"),
            "SteamAppId",
            document => document["newState"]!["steamAppId"] = "9999999");
        await CheckInvalidSetEnabledJournalAsync(
            Path.Combine(root, "installation-key"),
            "installation key",
            document => document["installationKey"] = "nfg.anvil-empires.other-slot");
        await CheckInvalidSetEnabledJournalAsync(
            Path.Combine(root, "game-root"),
            "game path",
            document => document["newState"]!["gameRoot"] =
                Path.Combine(root, "different-game"));
        await CheckInvalidUninstallJournalAsync(Path.Combine(root, "transition-shape"));
    }

    private static async Task CheckInvalidSetEnabledJournalAsync(
        string root,
        string description,
        Action<JsonObject> mutate)
    {
        var fixture = await CreateInstalledFixtureAsync(root, isEnabled: true);
        await CreateInterruptedSetEnabledJournalAsync(fixture, isEnabled: false);
        var journalPath = GetMutationJournalPath(fixture.DataRoot);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(journalPath))?.AsObject()
            ?? throw new InvalidOperationException("Mutation journal fixture is not an object.");
        mutate(document);
        await File.WriteAllTextAsync(
            journalPath,
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        await AssertInvalidJournalPreservedAsync(fixture, description);
    }

    private static async Task CheckInvalidUninstallJournalAsync(string root)
    {
        var fixture = await CreateInstalledFixtureAsync(root, isEnabled: true);
        var interrupted = new ManagedFilesInstaller(fixture.StateStore)
        {
            PhaseObserver = phase => InterruptAtAsync(
                phase,
                ManagedInstallerPhase.JournalDurable)
        };
        await AssertThrowsAsync<ManagedInstallerInterruptionException>(
            () => interrupted.UninstallAsync(InstallationKey, RuProductId),
            "Uninstall journal fixture did not stop after its durable commit.");

        var journalPath = GetMutationJournalPath(fixture.DataRoot);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(journalPath))?.AsObject()
            ?? throw new InvalidOperationException("Uninstall journal fixture is not an object.");
        document["newState"] = document["oldState"]!.DeepClone();
        await File.WriteAllTextAsync(
            journalPath,
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        await AssertInvalidJournalPreservedAsync(fixture, "inconsistent uninstall transition");
    }

    private static async Task AssertInvalidJournalPreservedAsync(
        InstalledFixture fixture,
        string description)
    {
        var journalPath = GetMutationJournalPath(fixture.DataRoot);
        var journalBefore = await File.ReadAllBytesAsync(journalPath);
        var statePath = GetStatePath(fixture.DataRoot);
        var stateBefore = await File.ReadAllBytesAsync(statePath);
        var gameBefore = SnapshotDirectory(fixture.GameRoot);

        await AssertThrowsAsync<InstallationStateException>(
            () => new ManagedFilesInstaller(fixture.StateStore)
                .RecoverPendingOperationsAsync(),
            $"Mutation journal with invalid {description} was accepted.");

        var journalAfter = await File.ReadAllBytesAsync(journalPath);
        var stateAfter = await File.ReadAllBytesAsync(statePath);
        Assert(journalBefore.AsSpan().SequenceEqual(journalAfter),
            $"Invalid {description} recovery rewrote journal evidence.");
        Assert(stateBefore.AsSpan().SequenceEqual(stateAfter),
            $"Invalid {description} recovery rewrote installation state.");
        Assert(gameBefore == SnapshotDirectory(fixture.GameRoot),
            $"Invalid {description} recovery changed managed files.");
    }

    private static async Task CheckUnknownThirdStateFailsClosedAsync(string root)
    {
        await CheckUnknownThirdStateForSetEnabledAsync(Path.Combine(root, "set-enabled"));
        await CheckUnknownThirdStateForUninstallAsync(Path.Combine(root, "uninstall"));
    }

    private static async Task CheckUnknownThirdStateForSetEnabledAsync(string root)
    {
        var fixture = await CreateInstalledFixtureAsync(root, isEnabled: true);
        var interrupted = new ManagedFilesInstaller(fixture.StateStore)
        {
            PhaseObserver = phase => InterruptAtAsync(
                phase,
                ManagedInstallerPhase.ActivationComplete)
        };
        await AssertThrowsAsync<ManagedInstallerInterruptionException>(
            () => interrupted.SetEnabledAsync(
                InstallationKey,
                RuProductId,
                isEnabled: false),
            "SetEnabled third-state fixture did not retain moved files.");
        await SaveUnknownThirdStateAsync(fixture);
        await AssertUnknownThirdStatePreservedAsync(fixture, "SetEnabled");
    }

    private static async Task CheckUnknownThirdStateForUninstallAsync(string root)
    {
        var fixture = await CreateInstalledFixtureAsync(root, isEnabled: true);
        var interrupted = new ManagedFilesInstaller(fixture.StateStore)
        {
            PhaseObserver = phase => InterruptAtAsync(
                phase,
                ManagedInstallerPhase.ActivationComplete)
        };
        await AssertThrowsAsync<ManagedInstallerInterruptionException>(
            () => interrupted.UninstallAsync(InstallationKey, RuProductId),
            "Uninstall third-state fixture did not retain tombstones.");
        await SaveUnknownThirdStateAsync(fixture);
        await AssertUnknownThirdStatePreservedAsync(fixture, "Uninstall");
    }

    private static async Task SaveUnknownThirdStateAsync(InstalledFixture fixture)
    {
        var oldState = await fixture.StateStore.LoadAsync(InstallationKey)
            ?? throw new InvalidOperationException("Third-state fixture lost its old state.");
        await fixture.StateStore.SaveAsync(oldState with
        {
            ProductId = EsProductId,
            Version = "9.9.9",
            PackageSha256 = new string('f', 64),
            InstalledAt = oldState.InstalledAt.AddMinutes(1)
        });
    }

    private static async Task AssertUnknownThirdStatePreservedAsync(
        InstalledFixture fixture,
        string operation)
    {
        var journalPath = GetMutationJournalPath(fixture.DataRoot);
        var statePath = GetStatePath(fixture.DataRoot);
        var journalBefore = await File.ReadAllBytesAsync(journalPath);
        var stateBefore = await File.ReadAllBytesAsync(statePath);
        var gameBefore = SnapshotDirectory(fixture.GameRoot);
        var restarted = new ManagedFilesInstaller(fixture.StateStore);

        await AssertThrowsAsync<ManagedFilesInstallException>(
            () => restarted.RecoverPendingOperationsAsync(),
            $"{operation} recovery accepted persisted state matching neither old nor new.");
        await AssertThrowsAsync<ManagedFilesInstallException>(
            () => restarted.SetEnabledAsync(
                InstallationKey,
                EsProductId,
                isEnabled: false),
            $"A mutation proceeded through unknown {operation} evidence.");

        var journalAfter = await File.ReadAllBytesAsync(journalPath);
        var stateAfter = await File.ReadAllBytesAsync(statePath);
        Assert(journalBefore.AsSpan().SequenceEqual(journalAfter),
            $"Unknown {operation} recovery rewrote journal evidence.");
        Assert(stateBefore.AsSpan().SequenceEqual(stateAfter),
            $"Unknown {operation} recovery rewrote third state.");
        Assert(gameBefore == SnapshotDirectory(fixture.GameRoot),
            $"Unknown {operation} recovery changed filesystem evidence.");
    }

    private static async Task CheckCoordinatorRepeatedInstallAsync(string root)
    {
        var libraryRoot = Path.Combine(root, "steam-library");
        var gameRoot = CreateSteamGameRoot(libraryRoot, "Anvil Coordinator Repeat");
        var package = await CreatePackageAsync(
            Path.Combine(root, "package"),
            RuProductId,
            version: "1.0.0",
            FirstPayload,
            SecondPayload);
        var packageBytes = await File.ReadAllBytesAsync(package.Package.ArchivePath);
        using var httpClient = new HttpClient(new PackageBytesHandler(packageBytes));
        var dataRoot = Path.Combine(root, "data");
        var coordinator = new ProductInstallationCoordinator(
            httpClient,
            dataRoot,
            new InstallationStateStore(dataRoot),
            new SteamGameLocator([libraryRoot], includeConfiguredSteam: false));

        var first = await coordinator.InstallAsync(package.Product);
        var second = await coordinator.InstallAsync(package.Product);

        Assert(first.Outcome is ManagedInstallOutcome.Installed or ManagedInstallOutcome.Adopted,
            "Coordinator fixture did not perform its initial install.");
        Assert(second.Outcome == ManagedInstallOutcome.AlreadyInstalled,
            "Repeated coordinator InstallAsync did not return AlreadyInstalled.");
        AssertState(second.State, RuProductId, isEnabled: true);
        AssertManagedFiles(gameRoot, isEnabled: true);
        AssertNoMutationArtifacts(dataRoot, gameRoot);
    }

    private static async Task CheckCoordinatorSiblingInstallRejectedAsync(string root)
    {
        var libraryRoot = Path.Combine(root, "steam-library");
        var gameRoot = CreateSteamGameRoot(libraryRoot, "Anvil Coordinator Sibling");
        var esPackage = await CreatePackageAsync(
            Path.Combine(root, "es-package"),
            EsProductId,
            version: "1.0.0",
            UpdatedPayload,
            SecondPayload);
        var ruPackage = await CreatePackageAsync(
            Path.Combine(root, "ru-package"),
            RuProductId,
            version: "1.0.0",
            FirstPayload,
            SecondPayload);
        var dataRoot = Path.Combine(root, "data");
        var stateStore = new InstallationStateStore(dataRoot);
        await new ManagedFilesInstaller(stateStore).InstallAsync(
            esPackage.Package,
            esPackage.Product,
            gameRoot);

        var statePath = GetStatePath(dataRoot);
        var stateBefore = await File.ReadAllBytesAsync(statePath);
        var gameBefore = SnapshotDirectory(gameRoot);
        var packageBytes = await File.ReadAllBytesAsync(ruPackage.Package.ArchivePath);
        using var httpClient = new HttpClient(new PackageBytesHandler(packageBytes));
        var coordinator = new ProductInstallationCoordinator(
            httpClient,
            dataRoot,
            stateStore,
            new SteamGameLocator([libraryRoot], includeConfiguredSteam: false));

        ProductInstallationException? failure = null;
        try
        {
            _ = await coordinator.InstallAsync(ruPackage.Product);
        }
        catch (ProductInstallationException exception)
        {
            failure = exception;
        }

        var actualFailure = failure
            ?? throw new InvalidOperationException(
                "Coordinator InstallAsync accepted an inactive sibling without an expected product guard.");
        Assert(actualFailure.InnerException is ManagedFilesInstallException,
            "Coordinator InstallAsync accepted an inactive sibling without an expected product guard.");
        Assert(!actualFailure.ActualStateUnreadable,
            "Coordinator could not report persisted state after rejecting an inactive sibling.");
        AssertState(actualFailure.ActualState, EsProductId, isEnabled: true);
        Assert(File.ReadAllBytes(statePath).AsSpan().SequenceEqual(stateBefore),
            "Rejected sibling InstallAsync rewrote the active ES state.");
        Assert(SnapshotDirectory(gameRoot) == gameBefore,
            "Rejected sibling InstallAsync changed active ES files.");
        Assert(!File.Exists(GetUpdateJournalPath(dataRoot)),
            "Rejected sibling InstallAsync left an update journal.");
        AssertNoMutationArtifacts(dataRoot, gameRoot);
    }

    private static async Task CheckExternalBackupCollisionsAsync(string root)
    {
        foreach (var (name, collisionBytes) in new[]
                 {
                     ("old-exact", FirstPayload),
                     ("new-exact", UpdatedPayload),
                     ("arbitrary", Encoding.UTF8.GetBytes("external backup collision"))
                 })
        {
            var caseRoot = Path.Combine(root, name);
            var fixture = await CreateInstalledFixtureAsync(caseRoot, isEnabled: true);
            var updatePackage = await CreatePackageAsync(
                Path.Combine(caseRoot, "update-package"),
                RuProductId,
                version: "1.1.0",
                UpdatedPayload,
                SecondPayload);
            string? backupPath = null;
            var installer = new ManagedFilesInstaller(fixture.StateStore)
            {
                PhaseObserver = async phase =>
                {
                    if (phase != ManagedInstallerPhase.JournalDurable)
                    {
                        return;
                    }

                    var journalPath = GetUpdateJournalPath(fixture.DataRoot);
                    using var journal = JsonDocument.Parse(
                        await File.ReadAllBytesAsync(journalPath));
                    var operationId = journal.RootElement
                        .GetProperty("operationId")
                        .GetString()
                        ?? throw new InvalidOperationException(
                            "Update journal has no operation id.");
                    backupPath = $"{ResolveManagedPath(fixture.GameRoot, FirstDestination)}" +
                                 $".nfg-update-old-{operationId}.disabled";
                    await File.WriteAllBytesAsync(backupPath, collisionBytes);
                }
            };

            await AssertThrowsAsync<Exception>(
                () => installer.SwitchVersionAsync(
                    updatePackage.Package,
                    updatePackage.Product,
                    fixture.GameRoot),
                $"Update accepted an external {name} backup collision.");

            var retainedPath = backupPath
                ?? throw new InvalidOperationException("Backup collision path was not recorded.");
            Assert(File.Exists(retainedPath),
                $"Recovery deleted the external {name} backup collision.");
            Assert(File.ReadAllBytes(retainedPath).AsSpan().SequenceEqual(collisionBytes),
                $"Recovery changed the external {name} backup collision.");
            AssertState(
                await fixture.StateStore.LoadAsync(InstallationKey),
                RuProductId,
                isEnabled: true,
                expectedVersion: "1.0.0");
            AssertManagedFiles(fixture.GameRoot, isEnabled: true);

            try
            {
                await new ManagedFilesInstaller(fixture.StateStore)
                    .RecoverPendingOperationsAsync();
            }
            catch (ManagedFilesInstallException)
            {
                // Retained external evidence may intentionally keep recovery fail-closed.
            }

            Assert(File.Exists(retainedPath) &&
                   File.ReadAllBytes(retainedPath).AsSpan().SequenceEqual(collisionBytes),
                $"Retry deleted the external {name} backup collision.");
        }
    }

    private static async Task CheckCrossProcessLockAsync(string root)
    {
        var dataRoot = Path.Combine(root, "data");
        var readyPath = Path.Combine(root, "signals", "ready");
        var releasePath = Path.Combine(root, "signals", "release");
        Directory.CreateDirectory(Path.GetDirectoryName(readyPath)!);
        using var child = StartLockHolder(dataRoot, readyPath, releasePath);
        try
        {
            await WaitForFileOrExitAsync(child, readyPath, TimeSpan.FromSeconds(8));
            var sameSlotEntered = false;
            using (var cancellation = new CancellationTokenSource(
                       TimeSpan.FromMilliseconds(300)))
            {
                await AssertThrowsAsync<OperationCanceledException>(
                    () => new InstallationOperationLock(dataRoot).ExecuteAsync(
                        InstallationKey,
                        cancellation.Token,
                        () =>
                        {
                            sameSlotEntered = true;
                            return Task.FromResult(true);
                        }),
                    "Cross-process slot-lock waiter ignored cancellation.");
            }

            Assert(!sameSlotEntered,
                "A second process entered the same installation slot concurrently.");
            var forgeEntered = false;
            await new InstallationOperationLock(dataRoot).ExecuteAsync(
                ForgeProductId,
                CancellationToken.None,
                () =>
                {
                    forgeEntered = true;
                    return Task.FromResult(true);
                });
            Assert(forgeEntered,
                "The language slot lock blocked an unrelated Forge slot across processes.");
        }
        finally
        {
            await File.WriteAllTextAsync(releasePath, "release");
        }

        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        var standardOutput = await child.StandardOutput.ReadToEndAsync();
        var standardError = await child.StandardError.ReadToEndAsync();
        Assert(child.ExitCode == 0,
            $"Cross-process lock holder failed ({child.ExitCode}): " +
            $"{standardOutput} {standardError}");

        var enteredAfterRelease = false;
        await new InstallationOperationLock(dataRoot).ExecuteAsync(
            InstallationKey,
            CancellationToken.None,
            () =>
            {
                enteredAfterRelease = true;
                return Task.FromResult(true);
            });
        Assert(enteredAfterRelease,
            "The cross-process slot lock was not released when its owner exited.");
    }

    private static Process StartLockHolder(
        string dataRoot,
        string readyPath,
        string releasePath)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Current executable path is unavailable.");
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        }

        startInfo.ArgumentList.Add("--hold-installation-lock");
        startInfo.ArgumentList.Add(dataRoot);
        startInfo.ArgumentList.Add(InstallationKey);
        startInfo.ArgumentList.Add(readyPath);
        startInfo.ArgumentList.Add(releasePath);
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start lock-holder subprocess.");
    }

    private static async Task WaitForFileOrExitAsync(
        Process process,
        string path,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!File.Exists(path) && !process.HasExited && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        if (File.Exists(path))
        {
            return;
        }

        var output = process.HasExited
            ? await process.StandardOutput.ReadToEndAsync()
            : string.Empty;
        var error = process.HasExited
            ? await process.StandardError.ReadToEndAsync()
            : string.Empty;
        throw new InvalidOperationException(
            $"Lock-holder subprocess did not acquire the slot. {output} {error}");
    }

    private static async Task CreateInterruptedSetEnabledJournalAsync(
        InstalledFixture fixture,
        bool isEnabled)
    {
        var interrupted = new ManagedFilesInstaller(fixture.StateStore)
        {
            PhaseObserver = phase => InterruptAtAsync(
                phase,
                ManagedInstallerPhase.JournalDurable)
        };
        await AssertThrowsAsync<ManagedInstallerInterruptionException>(
            () => interrupted.SetEnabledAsync(
                InstallationKey,
                RuProductId,
                isEnabled),
            "SetEnabled journal fixture did not stop after its durable commit.");
    }

    private static Task InterruptAtAsync(
        ManagedInstallerPhase actual,
        ManagedInstallerPhase expected)
    {
        if (actual == expected)
        {
            throw new ManagedInstallerInterruptionException(
                $"Injected mutation interruption at {expected}.");
        }

        return Task.CompletedTask;
    }

    private static Task InterruptAtBoundaryAsync(
        ManagedInstallerPhase actual,
        ManagedInstallerPhase expected,
        ref int completedFileMutations)
    {
        if (actual != expected)
        {
            return Task.CompletedTask;
        }

        if (actual == ManagedInstallerPhase.FileMutationCompleted &&
            ++completedFileMutations != 1)
        {
            return Task.CompletedTask;
        }

        throw new ManagedInstallerInterruptionException(
            $"Injected mutation interruption at {expected}.");
    }

    private static async Task<InstalledFixture> CreateInstalledFixtureAsync(
        string root,
        bool isEnabled)
    {
        var dataRoot = Path.Combine(root, "data");
        var gameRoot = CreateGameRoot(root);
        var package = await CreatePackageAsync(
            Path.Combine(root, "package"),
            RuProductId,
            version: "1.0.0",
            FirstPayload,
            SecondPayload);
        var stateStore = new InstallationStateStore(dataRoot);
        var installer = new ManagedFilesInstaller(stateStore);
        await installer.InstallAsync(package.Package, package.Product, gameRoot);
        if (!isEnabled)
        {
            await installer.SetEnabledAsync(
                InstallationKey,
                RuProductId,
                isEnabled: false);
        }

        AssertManagedFiles(gameRoot, isEnabled);
        AssertNoMutationArtifacts(dataRoot, gameRoot);
        return new InstalledFixture(dataRoot, gameRoot, stateStore, package);
    }

    private static async Task<PackageFixture> CreatePackageAsync(
        string root,
        string productId,
        string version,
        byte[] firstPayload,
        byte[] secondPayload)
    {
        Directory.CreateDirectory(root);
        var archivePath = Path.Combine(
            root,
            $"{productId.Replace('.', '-')}-{version}.zip");
        var firstSource = "payload/first.pak";
        var secondSource = "payload/second.pak";
        var packageManifest = new
        {
            schema = "nfg-package/1",
            productId,
            version,
            strategy = "managed-files",
            steam = new { appId = SteamAppId, buildId = SteamBuildId },
            files = new[]
            {
                new
                {
                    source = firstSource,
                    destination = $"steam-game/{FirstDestination}",
                    sizeBytes = firstPayload.LongLength,
                    sha256 = Sha256(firstPayload)
                },
                new
                {
                    source = secondSource,
                    destination = $"steam-game/{SecondDestination}",
                    sizeBytes = secondPayload.LongLength,
                    sha256 = Sha256(secondPayload)
                }
            }
        };
        await using (var output = new FileStream(
                         archivePath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None))
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
        {
            WriteZipEntry(
                archive,
                "Fixture/nfg-package.json",
                JsonSerializer.SerializeToUtf8Bytes(packageManifest));
            WriteZipEntry(archive, $"Fixture/{firstSource}", firstPayload);
            WriteZipEntry(archive, $"Fixture/{secondSource}", secondPayload);
        }

        var archiveBytes = await File.ReadAllBytesAsync(archivePath);
        var product = new ProductManifest
        {
            SchemaVersion = 2,
            Id = productId,
            Type = "localization",
            FamilyId = FamilyId,
            Locale = productId == EsProductId ? "es" : "ru",
            ExclusiveGroup = InstallationKey,
            Display = new ProductDisplay
            {
                Title = "Anvil Empires Localization",
                Subtitle = productId == EsProductId ? "Español" : "Русский",
                Summary = "Mutation smoke fixture",
                Description = "Mutation smoke fixture",
                Features = []
            },
            Release = new ProductRelease
            {
                Version = version,
                Channel = "stable",
                GameVersion = $"steam-build-{SteamBuildId}",
                PublishedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                Highlights = [],
                KnownIssues = [],
                NotesUrl = null,
                Payload = new ProductPayload
                {
                    Url = new Uri($"https://packages.test/{productId}/{version}.zip"),
                    SizeBytes = archiveBytes.LongLength,
                    Sha256 = Sha256(archiveBytes)
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
                RequiresElevation = "never",
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
            Progress = new ProductProgress
            {
                TranslationPercent = 100,
                Label = "Complete"
            }
        };
        var validated = await new PackageArchiveService().ValidateAsync(
            archivePath,
            product);
        return new PackageFixture(product, validated);
    }

    private static string CreateGameRoot(string root)
    {
        var gameRoot = Path.Combine(root, "game");
        Directory.CreateDirectory(
            Path.Combine(gameRoot, "Anvil", "Content", "Paks"));
        return gameRoot;
    }

    private static string CreateSteamGameRoot(string libraryRoot, string installDirectory)
    {
        var steamAppsRoot = Path.Combine(libraryRoot, "steamapps");
        var gameRoot = Path.Combine(steamAppsRoot, "common", installDirectory);
        Directory.CreateDirectory(
            Path.Combine(gameRoot, "Anvil", "Content", "Paks"));
        File.WriteAllText(
            Path.Combine(steamAppsRoot, $"appmanifest_{SteamAppId}.acf"),
            $$"""
            "AppState"
            {
                "appid" "{{SteamAppId}}"
                "name" "Anvil Empires"
                "installdir" "{{installDirectory}}"
                "buildid" "{{SteamBuildId}}"
                "TargetBuildID" "{{SteamBuildId}}"
            }
            """);
        return gameRoot;
    }

    private static void WriteZipEntry(ZipArchive archive, string path, byte[] contents)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        stream.Write(contents);
    }

    private static void AssertManagedFiles(string gameRoot, bool isEnabled)
    {
        AssertManagedFile(gameRoot, FirstDestination, FirstPayload, isEnabled);
        AssertManagedFile(gameRoot, SecondDestination, SecondPayload, isEnabled);
    }

    private static void AssertManagedFile(
        string gameRoot,
        string destination,
        byte[] expected,
        bool isEnabled)
    {
        var active = ResolveManagedPath(gameRoot, destination);
        var disabled = $"{active}.nfg-disabled";
        var expectedPath = isEnabled ? active : disabled;
        var unexpectedPath = isEnabled ? disabled : active;
        Assert(File.Exists(expectedPath), $"Managed file '{expectedPath}' is missing.");
        Assert(!File.Exists(unexpectedPath) && !Directory.Exists(unexpectedPath),
            $"Managed file has a conflicting activation copy at '{unexpectedPath}'.");
        Assert(File.ReadAllBytes(expectedPath).AsSpan().SequenceEqual(expected),
            $"Managed file '{expectedPath}' has unexpected bytes.");
    }

    private static void AssertManagedFilesAbsent(string gameRoot)
    {
        foreach (var destination in new[] { FirstDestination, SecondDestination })
        {
            var active = ResolveManagedPath(gameRoot, destination);
            Assert(!File.Exists(active) && !File.Exists($"{active}.nfg-disabled"),
                $"Uninstall left managed destination '{destination}'.");
        }
    }

    private static void AssertMixedUninstallEvidence(
        string gameRoot,
        bool wasEnabled)
    {
        var firstSource = ResolveManagedPath(gameRoot, FirstDestination) +
                          (wasEnabled ? string.Empty : ".nfg-disabled");
        var secondSource = ResolveManagedPath(gameRoot, SecondDestination) +
                           (wasEnabled ? string.Empty : ".nfg-disabled");
        Assert(!File.Exists(firstSource),
            "First uninstall source still existed after its completed move.");
        var firstTombstones = Directory
            .EnumerateFiles(
                Path.GetDirectoryName(firstSource)!,
                $"{Path.GetFileName(firstSource)}.nfg-remove-*",
                SearchOption.TopDirectoryOnly)
            .ToArray();
        Assert(firstTombstones.Length == 1 &&
               File.ReadAllBytes(firstTombstones[0]).AsSpan().SequenceEqual(FirstPayload),
            "First uninstall move did not leave its verified tombstone.");
        Assert(File.Exists(secondSource) &&
               File.ReadAllBytes(secondSource).AsSpan().SequenceEqual(SecondPayload),
            "Second uninstall source moved before the first-move interruption.");
    }

    private static void AssertNoMutationArtifacts(string dataRoot, string gameRoot)
    {
        var transactionsRoot = Path.Combine(dataRoot, "state", "transactions");
        Assert(!Directory.Exists(transactionsRoot) ||
               !Directory.EnumerateFiles(transactionsRoot, "*", SearchOption.AllDirectories)
                   .Any(path =>
                       path.EndsWith(".mutation.json", StringComparison.OrdinalIgnoreCase) ||
                       path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)),
            "Completed state mutation retained a journal or atomic temp file.");

        string[] forbidden =
        [
            ".nfg-remove-",
            ".nfg-toggle-",
            ".nfg-enable-",
            ".nfg-disable-",
            ".nfg-mutation-"
        ];
        var leftovers = Directory
            .EnumerateFiles(gameRoot, "*", SearchOption.AllDirectories)
            .Where(path => forbidden.Any(marker =>
                Path.GetFileName(path).Contains(marker, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        Assert(leftovers.Length == 0,
            $"Completed state mutation left artifacts: {string.Join(", ", leftovers)}");
    }

    private static void AssertState(
        InstalledProductState? state,
        string productId,
        bool isEnabled,
        string expectedVersion = "1.0.0")
    {
        var actual = state
            ?? throw new InvalidOperationException("Expected installation state is missing.");
        Assert(actual.SchemaVersion == 2 && actual.InstallationKey == InstallationKey,
            "Mutation state lost its schema-v2 installation identity.");
        Assert(actual.ProductId == productId && actual.Version == expectedVersion,
            "Mutation state changed product identity or version.");
        Assert(actual.IsEnabled == isEnabled,
            "Mutation state has the wrong activation value.");
        Assert(actual.SteamAppId == SteamAppId,
            "Mutation state changed its Steam application identity.");
    }

    private static string SnapshotDirectory(string root) => string.Join(
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

    private static string ResolveManagedPath(string gameRoot, string destination) =>
        Path.Combine(
            gameRoot,
            destination.Replace('/', Path.DirectorySeparatorChar));

    private static string FindRemovalTombstone(string gameRoot, string destination)
    {
        var source = ResolveManagedPath(gameRoot, destination);
        return Directory.EnumerateFiles(
                Path.GetDirectoryName(source)!,
                $"{Path.GetFileName(source)}.nfg-remove-*",
                SearchOption.TopDirectoryOnly)
            .Single();
    }

    private static string GetMutationJournalPath(string dataRoot) => Path.Combine(
        dataRoot,
        "state",
        "transactions",
        $"{InstallationKey}.mutation.json");

    private static string GetUpdateJournalPath(string dataRoot) => Path.Combine(
        dataRoot,
        "state",
        "transactions",
        $"{InstallationKey}.update.json");

    private static string GetTerminalQuarantinePath(string path, string operationId) =>
        $"{path}.nfg-terminal-{operationId}.quarantine";

    private static string GetStatePath(string dataRoot) => Path.Combine(
        dataRoot,
        "state",
        "installations",
        $"{InstallationKey}.json");

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

    private static readonly ManagedInstallerPhase[] MutationBoundaries =
    [
        ManagedInstallerPhase.JournalDurable,
        ManagedInstallerPhase.FileMutationCompleted,
        ManagedInstallerPhase.ActivationComplete,
        ManagedInstallerPhase.StateCommitted
    ];

    private sealed record InstalledFixture(
        string DataRoot,
        string GameRoot,
        InstallationStateStore StateStore,
        PackageFixture Package);

    private sealed record PackageFixture(
        ProductManifest Product,
        ValidatedPackage Package);

    private sealed class PackageBytesHandler(byte[] packageBytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(packageBytes)
            });
    }
}
