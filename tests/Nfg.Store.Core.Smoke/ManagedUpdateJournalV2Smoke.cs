using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nfg.Store.Installation;

internal static class ManagedUpdateJournalV2Smoke
{
    private const string InstallationKey = "nfg.anvil-empires.ru";
    private const string RuProductId = "nfg.anvil-empires.ru";
    private const string EsProductId = "nfg.anvil-empires.es";
    private const string Destination = "Anvil/Content/Paks/Test-Language.pak";

    private static readonly JsonSerializerOptions LegacySerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task RunAsync(string testRoot)
    {
        await CheckSchemaV2RoundTripAsync(Path.Combine(testRoot, "journal-v2-roundtrip"));
        await CheckSchemaV2VariantTransitionAsync(Path.Combine(testRoot, "journal-v2-variant"));
        await CheckRejectedSchemaV2TransitionsAsync(
            Path.Combine(testRoot, "journal-v2-rejected-transitions"));
        await CheckCommittedCleanupRetryAsync(
            Path.Combine(testRoot, "journal-v2-cleanup-retry"));
        await CheckLegacySchemaV1LoadAsync(Path.Combine(testRoot, "journal-v1-load"));
        await CheckLegacySourceBackupCollisionAsync(
            Path.Combine(testRoot, "journal-v1-source-backup-collision"));
        await CheckLegacyRollbackTerminalRetryAsync(
            Path.Combine(testRoot, "journal-v1-rollback-terminal-retry"));
    }

    private static async Task CheckSchemaV2RoundTripAsync(string dataRoot)
    {
        var stateStore = new InstallationStateStore(dataRoot);
        var journalStore = new ManagedUpdateJournalStore(stateStore);
        var newState = CreateState(
            schemaVersion: 2,
            installationKey: InstallationKey,
            productId: RuProductId,
            version: "1.0.0",
            gameRoot: Path.Combine(dataRoot, "game"));
        var journal = new ManagedUpdateJournal
        {
            SchemaVersion = 2,
            OperationId = new string('a', 32),
            InstallationKey = InstallationKey,
            OldState = null,
            NewState = newState,
            PreservedDestinations = [Destination]
        };

        await journalStore.SaveAsync(journal);
        var path = GetJournalPath(dataRoot, InstallationKey);
        using (var json = JsonDocument.Parse(await File.ReadAllBytesAsync(path)))
        {
            var properties = json.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal);
            Assert(properties.SetEquals(
                [
                    "schemaVersion",
                    "operationId",
                    "installationKey",
                    "oldState",
                    "newState",
                    "preservedDestinations"
                ]), "Schema-v2 journal persisted a mixed or incomplete top-level shape.");
            Assert(
                json.RootElement.GetProperty("oldState").ValueKind == JsonValueKind.Null,
                "Fresh-install journal did not persist an explicit null oldState.");
        }

        var loaded = await journalStore.LoadAsync(InstallationKey)
            ?? throw new InvalidOperationException("Schema-v2 journal was not reloaded.");
        Assert(loaded.SchemaVersion == 2, "Schema-v2 journal changed schema during reload.");
        Assert(
            loaded.EffectiveInstallationKey == InstallationKey,
            "Schema-v2 journal exposed the wrong effective installation key.");
        Assert(loaded.ProductId is null, "Schema-v2 journal retained a legacy top-level ProductId.");
        Assert(loaded.OldState is null, "Fresh-install journal unexpectedly gained an old state.");
        Assert(
            loaded.PreservedDestinations.SequenceEqual([Destination], StringComparer.Ordinal),
            "Schema-v2 preserved destinations did not round-trip.");

        await AssertThrowsAsync<InstallationStateException>(
            () => journalStore.SaveAsync(journal),
            "A pending schema-v2 journal was overwritten.");
    }

    private static async Task CheckSchemaV2VariantTransitionAsync(string dataRoot)
    {
        var journalStore = new ManagedUpdateJournalStore(new InstallationStateStore(dataRoot));
        var gameRoot = Path.Combine(dataRoot, "game");
        var oldRu = CreateState(2, InstallationKey, RuProductId, "1.0.0", gameRoot);
        var newEs = CreateState(2, InstallationKey, EsProductId, "1.0.0", gameRoot);
        var variantSwitch = new ManagedUpdateJournal
        {
            SchemaVersion = 2,
            OperationId = new string('b', 32),
            InstallationKey = InstallationKey,
            OldState = oldRu,
            NewState = newEs,
            PreservedDestinations = []
        };

        await journalStore.SaveAsync(variantSwitch);
        var loaded = await journalStore.LoadAsync(InstallationKey)
            ?? throw new InvalidOperationException("Variant-switch journal was not reloaded.");
        Assert(
            loaded.OldState?.ProductId == RuProductId && loaded.NewState.ProductId == EsProductId,
            "Variant-switch product identities did not round-trip.");
        Assert(
            loaded.OldState!.Version == loaded.NewState.Version,
            "The same SemVer variant transition was unexpectedly changed.");

        journalStore.Delete(InstallationKey);
        var sameProductSameVersion = variantSwitch with
        {
            OperationId = new string('c', 32),
            NewState = oldRu with { InstalledAt = oldRu.InstalledAt.AddSeconds(1) }
        };
        await AssertThrowsAsync<InstallationStateException>(
            () => journalStore.SaveAsync(sameProductSameVersion),
            "A same-product journal without a version transition was accepted.");
        Assert(
            !File.Exists(GetJournalPath(dataRoot, InstallationKey)),
            "Rejected same-product journal left a persisted transaction.");
    }

    private static async Task CheckLegacySchemaV1LoadAsync(string dataRoot)
    {
        var gameRoot = Path.Combine(dataRoot, "game");
        var oldState = CreateState(1, installationKey: null, RuProductId, "1.0.0", gameRoot);
        var newState = CreateState(1, installationKey: null, RuProductId, "1.1.0", gameRoot);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                schemaVersion = 1,
                operationId = new string('d', 32),
                productId = RuProductId,
                oldState,
                newState
            },
            LegacySerializerOptions);
        var path = GetJournalPath(dataRoot, RuProductId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);

        var journalStore = new ManagedUpdateJournalStore(new InstallationStateStore(dataRoot));
        var loaded = await journalStore.LoadAsync(RuProductId)
            ?? throw new InvalidOperationException("Legacy schema-v1 journal was not loaded.");
        Assert(loaded.SchemaVersion == 1, "Legacy journal did not retain schema version 1.");
        Assert(loaded.ProductId == RuProductId, "Legacy top-level ProductId was not retained.");
        Assert(loaded.InstallationKey is null, "Legacy journal unexpectedly gained a v2 field.");
        Assert(
            loaded.EffectiveInstallationKey == RuProductId,
            "Legacy journal exposed the wrong effective installation key.");
        Assert(
            loaded.OldState?.SchemaVersion == 1 && loaded.NewState.SchemaVersion == 1,
            "Legacy journal states were not retained as schema v1.");
        Assert(
            loaded.PreservedDestinations.Count == 0,
            "Legacy journal unexpectedly gained preserved destinations.");

        journalStore.Delete(RuProductId);
        await AssertThrowsAsync<InstallationStateException>(
            () => journalStore.SaveAsync(loaded),
            "ManagedUpdateJournalStore.SaveAsync accepted a legacy journal.");
    }

    private static async Task CheckRejectedSchemaV2TransitionsAsync(string root)
    {
        var gameRoot = Path.Combine(root, "game");
        var oldRu = CreateState(2, InstallationKey, RuProductId, "1.0.0", gameRoot);
        var newEs = CreateState(2, InstallationKey, EsProductId, "1.0.0", gameRoot);

        await AssertRejectedSchemaV2JournalAsync(
            Path.Combine(root, "is-enabled"),
            CreateJournal(oldRu, newEs with { IsEnabled = false }, 'e'),
            "An update journal changed IsEnabled across a variant transition.");
        await AssertRejectedSchemaV2JournalAsync(
            Path.Combine(root, "steam-app-id"),
            CreateJournal(oldRu, newEs with { SteamAppId = "9999999" }, 'f'),
            "An update journal changed SteamAppId across a variant transition.");
        await AssertRejectedSchemaV2JournalAsync(
            Path.Combine(root, "fresh-disabled"),
            CreateJournal(oldState: null, newEs with { IsEnabled = false }, '1'),
            "A fresh-install update journal accepted disabled NewState.");
        await AssertRejectedSchemaV2JournalAsync(
            Path.Combine(root, "blank-steam-app-id"),
            CreateJournal(oldRu, newEs with { SteamAppId = "" }, '2'),
            "An update journal accepted malformed SteamAppId.",
            preserveExistingEvidence: true);
        await AssertRejectedSchemaV2JournalAsync(
            Path.Combine(root, "non-numeric-steam-app-id"),
            CreateJournal(oldRu, newEs with { SteamAppId = "steam-app" }, '4'),
            "An update journal accepted non-numeric SteamAppId.");
        await AssertRejectedCleanupPreparedShapeAsync(
            Path.Combine(root, "fresh-cleanup-prepared"),
            newEs);
    }

    private static ManagedUpdateJournal CreateJournal(
        InstalledProductState? oldState,
        InstalledProductState newState,
        char operationIdCharacter) => new()
        {
            SchemaVersion = 2,
            OperationId = new string(operationIdCharacter, 32),
            InstallationKey = InstallationKey,
            OldState = oldState,
            NewState = newState,
            PreservedDestinations = oldState is null ? [Destination] : []
        };

    private static async Task AssertRejectedSchemaV2JournalAsync(
        string dataRoot,
        ManagedUpdateJournal journal,
        string message,
        bool preserveExistingEvidence = false)
    {
        var path = GetJournalPath(dataRoot, InstallationKey);
        byte[]? originalEvidence = null;
        if (preserveExistingEvidence)
        {
            originalEvidence = Encoding.UTF8.GetBytes("pre-existing journal evidence");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, originalEvidence);
        }

        await AssertThrowsAsync<InstallationStateException>(
            () => new ManagedUpdateJournalStore(new InstallationStateStore(dataRoot))
                .SaveAsync(journal),
            message);

        if (originalEvidence is null)
        {
            Assert(!File.Exists(path),
                "Rejected update journal was partially persisted.");
        }
        else
        {
            Assert(File.Exists(path) &&
                   File.ReadAllBytes(path).AsSpan().SequenceEqual(originalEvidence),
                "Rejected update journal changed pre-existing evidence.");
        }
    }

    private static async Task AssertRejectedCleanupPreparedShapeAsync(
        string dataRoot,
        InstalledProductState newState)
    {
        var path = GetJournalPath(dataRoot, InstallationKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                schemaVersion = 2,
                operationId = new string('5', 32),
                installationKey = InstallationKey,
                oldState = (InstalledProductState?)null,
                newState,
                preservedDestinations = new[] { Destination },
                cleanupPrepared = true
            },
            LegacySerializerOptions);
        await File.WriteAllBytesAsync(path, bytes);

        await AssertThrowsAsync<InstallationStateException>(
            () => new ManagedUpdateJournalStore(new InstallationStateStore(dataRoot))
                .LoadAsync(InstallationKey),
            "A fresh-install journal accepted cleanupPrepared=true.");
        Assert(File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes),
            "Rejected cleanupPrepared journal evidence was changed.");
    }

    private static async Task CheckCommittedCleanupRetryAsync(string root)
    {
        await CheckCommittedCleanupRetryAtPhaseAsync(
            Path.Combine(root, "prepared"),
            ManagedInstallerPhase.CleanupPrepared,
            operationIdCharacter: '6');
        await CheckCommittedCleanupRetryAtPhaseAsync(
            Path.Combine(root, "first-delete"),
            ManagedInstallerPhase.FileCleanupCompleted,
            operationIdCharacter: '7');
    }

    private static async Task CheckCommittedCleanupRetryAtPhaseAsync(
        string dataRoot,
        ManagedInstallerPhase interruptionPhase,
        char operationIdCharacter)
    {
        var gameRoot = Path.Combine(dataRoot, "game");
        var oldPayloads = new[]
        {
            Encoding.UTF8.GetBytes("cleanup retry old first"),
            Encoding.UTF8.GetBytes("cleanup retry old second")
        };
        var newPayloads = new[]
        {
            Encoding.UTF8.GetBytes("cleanup retry new first"),
            Encoding.UTF8.GetBytes("cleanup retry new second")
        };
        var destinations = new[]
        {
            Destination,
            "Anvil/Content/Paks/Test-Language-Second.pak"
        };
        var oldState = CreateMultiFileState(
            "1.0.0",
            gameRoot,
            destinations,
            oldPayloads);
        var newState = CreateMultiFileState(
            "1.1.0",
            gameRoot,
            destinations,
            newPayloads);
        var stateStore = new InstallationStateStore(dataRoot);
        await stateStore.SaveAsync(newState);
        var operationId = new string(operationIdCharacter, 32);
        var journal = new ManagedUpdateJournal
        {
            SchemaVersion = 2,
            OperationId = operationId,
            InstallationKey = InstallationKey,
            OldState = oldState,
            NewState = newState,
            PreservedDestinations = []
        };
        await new ManagedUpdateJournalStore(stateStore).SaveAsync(journal);

        var backupPaths = new List<string>();
        for (var index = 0; index < destinations.Length; index++)
        {
            var livePath = Path.Combine(
                gameRoot,
                destinations[index].Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(livePath)!);
            await File.WriteAllBytesAsync(livePath, newPayloads[index]);
            var backupPath = $"{livePath}.nfg-update-old-{operationId}.disabled";
            await File.WriteAllBytesAsync(backupPath, oldPayloads[index]);
            backupPaths.Add(backupPath);
        }

        var completedCleanupFiles = 0;
        var interrupted = new ManagedFilesInstaller(stateStore)
        {
            PhaseObserver = phase =>
            {
                if (phase != interruptionPhase)
                {
                    return Task.CompletedTask;
                }

                if (phase == ManagedInstallerPhase.FileCleanupCompleted &&
                    ++completedCleanupFiles != 1)
                {
                    return Task.CompletedTask;
                }

                throw new ManagedInstallerInterruptionException(
                    $"Injected committed cleanup interruption at {phase}.");
            }
        };
        await AssertThrowsAsync<ManagedInstallerInterruptionException>(
            () => interrupted.RecoverPendingOperationsAsync(),
            $"Committed cleanup did not stop at {interruptionPhase}.");

        var journalPath = GetJournalPath(dataRoot, InstallationKey);
        using (var document = JsonDocument.Parse(await File.ReadAllBytesAsync(journalPath)))
        {
            Assert(document.RootElement.GetProperty("cleanupPrepared").GetBoolean(),
                "Committed cleanup interruption did not retain prepared journal state.");
        }

        Assert(backupPaths.All(path => !File.Exists(path)),
            "Prepared cleanup retained a pre-prepare backup path.");
        var cleanupPaths = backupPaths.Select(path => $"{path}.cleanup").ToArray();
        var expectedCleanupCount = interruptionPhase == ManagedInstallerPhase.CleanupPrepared
            ? 2
            : 1;
        Assert(cleanupPaths.Count(File.Exists) == expectedCleanupCount,
            "Committed cleanup interruption left the wrong number of owned cleanup files.");

        var externalBackup = backupPaths[0];
        await File.WriteAllBytesAsync(externalBackup, oldPayloads[0]);
        await new ManagedFilesInstaller(stateStore).RecoverPendingOperationsAsync();

        Assert(File.Exists(externalBackup) &&
               File.ReadAllBytes(externalBackup).AsSpan().SequenceEqual(oldPayloads[0]),
            "Prepared cleanup retry deleted an external exact backup recreated at B.");
        Assert(cleanupPaths.All(path => !File.Exists(path)),
            "Prepared cleanup retry retained an owned Q file.");
        Assert(!File.Exists(journalPath),
            "Prepared cleanup retry retained its completed journal.");
        Assert(InstallationStateMigrator.StatesEqual(
                await stateStore.LoadAsync(InstallationKey)
                    ?? throw new InvalidOperationException("Committed cleanup lost new state."),
                newState),
            "Prepared cleanup retry changed the committed new state.");
        for (var index = 0; index < destinations.Length; index++)
        {
            var livePath = Path.Combine(
                gameRoot,
                destinations[index].Replace('/', Path.DirectorySeparatorChar));
            Assert(File.ReadAllBytes(livePath).AsSpan().SequenceEqual(newPayloads[index]),
                "Prepared cleanup retry changed a committed live file.");
        }

        await new ManagedFilesInstaller(stateStore).RecoverPendingOperationsAsync();
        Assert(File.Exists(externalBackup),
            "Repeated prepared cleanup recovery deleted external backup evidence.");
    }

    private static InstalledProductState CreateMultiFileState(
        string version,
        string gameRoot,
        IReadOnlyList<string> destinations,
        IReadOnlyList<byte[]> payloads) => CreateState(
            schemaVersion: 2,
            installationKey: InstallationKey,
            productId: RuProductId,
            version,
            gameRoot) with
        {
            PackageSizeBytes = payloads.Sum(payload => payload.LongLength),
            PackageSha256 = Sha256(payloads.SelectMany(payload => payload).ToArray()),
            Files = destinations
                .Select((destination, index) => new InstalledFileState
                {
                    Destination = destination,
                    SizeBytes = payloads[index].LongLength,
                    Sha256 = Sha256(payloads[index])
                })
                .ToArray()
        };

    private static async Task CheckLegacySourceBackupCollisionAsync(string dataRoot)
    {
        const string operationId = "33333333333333333333333333333333";
        var gameRoot = Path.Combine(dataRoot, "game");
        var sourcePath = Path.Combine(
            gameRoot,
            Destination.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        var oldPayload = Encoding.UTF8.GetBytes("legacy source and external exact backup");
        var newPayload = Encoding.UTF8.GetBytes("legacy replacement payload");
        var oldState = CreateFileBackedState("1.0.0", gameRoot, oldPayload);
        var newState = CreateFileBackedState("1.1.0", gameRoot, newPayload);
        var stateStore = new InstallationStateStore(dataRoot);
        var statePath = GetStatePath(dataRoot, RuProductId);
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        var stateBytes = JsonSerializer.SerializeToUtf8Bytes(
            oldState,
            LegacySerializerOptions);
        await File.WriteAllBytesAsync(statePath, stateBytes);
        await File.WriteAllBytesAsync(sourcePath, oldPayload);

        var journalPath = GetJournalPath(dataRoot, RuProductId);
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        var journalBytes = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                schemaVersion = 1,
                operationId,
                productId = RuProductId,
                oldState,
                newState
            },
            LegacySerializerOptions);
        await File.WriteAllBytesAsync(journalPath, journalBytes);
        var backupPath = $"{sourcePath}.nfg-update-old-{operationId}.disabled";
        await File.WriteAllBytesAsync(backupPath, oldPayload);

        await AssertThrowsAsync<ManagedFilesInstallException>(
            () => new ManagedFilesInstaller(stateStore).RecoverPendingOperationsAsync(),
            "Legacy recovery accepted simultaneous source and external exact backup.");

        Assert(File.ReadAllBytes(sourcePath).AsSpan().SequenceEqual(oldPayload),
            "Legacy recovery changed the original source during backup collision.");
        Assert(File.ReadAllBytes(backupPath).AsSpan().SequenceEqual(oldPayload),
            "Legacy recovery deleted or changed an external exact backup collision.");
        Assert(File.ReadAllBytes(journalPath).AsSpan().SequenceEqual(journalBytes),
            "Legacy recovery rewrote or discarded ambiguous journal evidence.");
        Assert(File.ReadAllBytes(statePath).AsSpan().SequenceEqual(stateBytes),
            "Legacy recovery rewrote persisted state during backup collision.");
    }

    private static async Task CheckLegacyRollbackTerminalRetryAsync(string dataRoot)
    {
        const string operationId = "88888888888888888888888888888888";
        const string newOnlyDestination =
            "Anvil/Content/Paks/Test-Language-New-Only.pak";
        var gameRoot = Path.Combine(dataRoot, "game");
        var oldPayload = Encoding.UTF8.GetBytes("legacy terminal retry old payload");
        var newOnlyPayload = Encoding.UTF8.GetBytes(
            "legacy terminal retry external exact new-only payload");
        var oldState = CreateFileBackedState("1.0.0", gameRoot, oldPayload);
        var newState = oldState with
        {
            Version = "1.1.0",
            PackageSizeBytes = oldPayload.LongLength + newOnlyPayload.LongLength,
            PackageSha256 = Sha256([.. oldPayload, .. newOnlyPayload]),
            InstalledAt = oldState.InstalledAt.AddMinutes(1),
            Files =
            [
                oldState.Files[0],
                new InstalledFileState
                {
                    Destination = newOnlyDestination,
                    SizeBytes = newOnlyPayload.LongLength,
                    Sha256 = Sha256(newOnlyPayload)
                }
            ]
        };
        var stateStore = new InstallationStateStore(dataRoot);
        var statePath = GetStatePath(dataRoot, RuProductId);
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        var stateBytes = JsonSerializer.SerializeToUtf8Bytes(
            oldState,
            LegacySerializerOptions);
        await File.WriteAllBytesAsync(statePath, stateBytes);

        var oldPath = Path.Combine(
            gameRoot,
            Destination.Replace('/', Path.DirectorySeparatorChar));
        var newOnlyPath = Path.Combine(
            gameRoot,
            newOnlyDestination.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!);
        await File.WriteAllBytesAsync(oldPath, oldPayload);

        var journalPath = GetJournalPath(dataRoot, RuProductId);
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        var journalBytes = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                schemaVersion = 1,
                operationId,
                productId = RuProductId,
                oldState,
                newState
            },
            LegacySerializerOptions);
        await File.WriteAllBytesAsync(journalPath, journalBytes);

        var stagePath = $"{newOnlyPath}.nfg-update-stage-{operationId}.disabled";
        var markerPath = $"{stagePath}.not-activated-owner";
        await File.WriteAllBytesAsync(stagePath, newOnlyPayload);
        var interrupted = new ManagedFilesInstaller(stateStore)
        {
            PhaseObserver = phase => phase == ManagedInstallerPhase.RollbackCleanupCompleted
                ? throw new ManagedInstallerInterruptionException(
                    "Injected interruption after durable legacy rollback cleanup.")
                : Task.CompletedTask
        };
        await AssertThrowsAsync<ManagedInstallerInterruptionException>(
            () => interrupted.RecoverPendingOperationsAsync(),
            "Legacy rollback did not stop after durable terminal cleanup evidence.");

        using (var terminalJournal = JsonDocument.Parse(
                   await File.ReadAllBytesAsync(journalPath)))
        {
            Assert(
                terminalJournal.RootElement.GetProperty("rollbackComplete").GetBoolean(),
                "Legacy rollback did not persist terminal rollback evidence.");
        }

        Assert(!File.Exists(stagePath) && !File.Exists(markerPath),
            "Legacy rollback terminal phase retained Hub staging evidence.");
        Assert(File.ReadAllBytes(statePath).AsSpan().SequenceEqual(stateBytes),
            "Legacy rollback terminal phase changed persisted old state.");
        Assert(File.ReadAllBytes(oldPath).AsSpan().SequenceEqual(oldPayload),
            "Legacy rollback terminal phase changed the old managed file.");

        await File.WriteAllBytesAsync(newOnlyPath, newOnlyPayload);
        await new ManagedFilesInstaller(stateStore).RecoverPendingOperationsAsync();

        Assert(File.Exists(newOnlyPath) &&
               File.ReadAllBytes(newOnlyPath).AsSpan().SequenceEqual(newOnlyPayload),
            "Legacy terminal retry deleted an external exact new-only file.");
        Assert(!File.Exists(journalPath) &&
               !File.Exists(stagePath) &&
               !File.Exists(markerPath),
            "Legacy terminal retry retained Hub transaction evidence.");
        Assert(File.ReadAllBytes(statePath).AsSpan().SequenceEqual(stateBytes),
            "Legacy terminal retry changed persisted old state.");
        Assert(File.ReadAllBytes(oldPath).AsSpan().SequenceEqual(oldPayload),
            "Legacy terminal retry changed the old managed file.");

        await new ManagedFilesInstaller(stateStore).RecoverPendingOperationsAsync();
        Assert(File.Exists(newOnlyPath) &&
               File.ReadAllBytes(newOnlyPath).AsSpan().SequenceEqual(newOnlyPayload),
            "Repeated legacy terminal recovery deleted external evidence.");
    }

    private static InstalledProductState CreateFileBackedState(
        string version,
        string gameRoot,
        byte[] payload) => CreateState(
            schemaVersion: 1,
            installationKey: null,
            productId: RuProductId,
            version,
            gameRoot) with
        {
            PackageSizeBytes = payload.LongLength,
            PackageSha256 = Sha256(payload),
            Files =
            [
                new InstalledFileState
                {
                    Destination = Destination,
                    SizeBytes = payload.LongLength,
                    Sha256 = Sha256(payload)
                }
            ]
        };

    private static InstalledProductState CreateState(
        int schemaVersion,
        string? installationKey,
        string productId,
        string version,
        string gameRoot) => new()
        {
            SchemaVersion = schemaVersion,
            InstallationKey = installationKey,
            ProductId = productId,
            Version = version,
            PackageSizeBytes = 128,
            PackageSha256 = new string('a', 64),
            SteamAppId = "1373910",
            SteamBuildId = "24370001",
            DetectedSteamBuildId = "24378492",
            GameRoot = gameRoot,
            InstalledAt = DateTimeOffset.Parse("2026-08-10T12:00:00+03:00"),
            IsEnabled = true,
            Files =
            [
                new InstalledFileState
                {
                    Destination = Destination,
                    SizeBytes = 64,
                    Sha256 = new string('b', 64)
                }
            ]
        };

    private static string GetJournalPath(string dataRoot, string installationKey) =>
        Path.Combine(
            dataRoot,
            "state",
            "transactions",
            $"{installationKey}.update.json");

    private static string GetStatePath(string dataRoot, string productId) =>
        Path.Combine(
            dataRoot,
            "state",
            "installations",
            $"{productId}.json");

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
}
