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
        await CheckCommittedReadyMarkerOwnershipAsync(
            Path.Combine(testRoot, "journal-v2-ready-marker-ownership"));
        await CheckLegacySchemaV1LoadAsync(Path.Combine(testRoot, "journal-v1-load"));
        await CheckLegacyCommittedBackupRecreationAsync(
            Path.Combine(testRoot, "journal-v1-committed-backup-recreation"));
        await CheckLegacyRollbackMarkerOwnershipAsync(
            Path.Combine(testRoot, "journal-v1-rollback-marker-ownership"));
        await CheckCanonicalRollbackOwnershipAsync(
            Path.Combine(testRoot, "journal-v2-canonical-rollback-ownership"));
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
        await CheckCommittedCleanupRetryAtPhaseAsync(
            Path.Combine(root, "first-quarantine"),
            ManagedInstallerPhase.FileQuarantined,
            operationIdCharacter: '8');
        await CheckCommittedCleanupRetryAtPhaseAsync(
            Path.Combine(root, "authority-revoked"),
            ManagedInstallerPhase.RecoveryAuthorityRevoked,
            operationIdCharacter: '9');
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

        var completedTargetPhase = 0;
        var authorityRaceBytes = Encoding.UTF8.GetBytes(
            "external arbitrary quarantine replacement after authority revoke");
        var cleanupPaths = backupPaths.Select(path => $"{path}.cleanup").ToArray();
        var terminalPaths = cleanupPaths
            .Select(path => GetTerminalQuarantinePath(path, operationId))
            .ToArray();
        var interrupted = new ManagedFilesInstaller(stateStore)
        {
            PhaseObserver = async phase =>
            {
                if (phase != interruptionPhase)
                {
                    return;
                }

                if (phase == ManagedInstallerPhase.RecoveryAuthorityRevoked)
                {
                    Assert(!File.Exists(GetJournalPath(dataRoot, InstallationKey)),
                        "Recovery authority was reported revoked before journal deletion.");
                    Assert(terminalPaths.All(File.Exists),
                        "Authority-revoked observer did not see all terminal quarantines.");
                    await File.WriteAllBytesAsync(terminalPaths[0], authorityRaceBytes);
                    return;
                }

                if (++completedTargetPhase != 1)
                {
                    return;
                }

                throw new ManagedInstallerInterruptionException(
                    $"Injected committed cleanup interruption at {phase}.");
            }
        };
        if (interruptionPhase == ManagedInstallerPhase.RecoveryAuthorityRevoked)
        {
            await interrupted.RecoverPendingOperationsAsync();
        }
        else
        {
            await AssertThrowsAsync<ManagedInstallerInterruptionException>(
                () => interrupted.RecoverPendingOperationsAsync(),
                $"Committed cleanup did not stop at {interruptionPhase}.");
        }

        var journalPath = GetJournalPath(dataRoot, InstallationKey);
        Assert(backupPaths.All(path => !File.Exists(path)),
            "Prepared cleanup retained a pre-prepare backup path.");
        if (interruptionPhase == ManagedInstallerPhase.CleanupPrepared)
        {
            using (var document = JsonDocument.Parse(await File.ReadAllBytesAsync(journalPath)))
            {
                Assert(document.RootElement.GetProperty("cleanupPrepared").GetBoolean(),
                    "Committed cleanup interruption did not retain prepared journal state.");
            }

            Assert(cleanupPaths.All(File.Exists),
                "CleanupPrepared interruption did not retain both owned Q files.");
            var externalBackup = backupPaths[0];
            await File.WriteAllBytesAsync(externalBackup, oldPayloads[0]);
            await new ManagedFilesInstaller(stateStore).RecoverPendingOperationsAsync();

            Assert(File.Exists(externalBackup) &&
                   File.ReadAllBytes(externalBackup).AsSpan().SequenceEqual(oldPayloads[0]),
                "Prepared cleanup retry deleted an external exact backup recreated at B.");
            Assert(cleanupPaths.All(path => !File.Exists(path)),
                "Ordinary prepared cleanup retry retained an owned Q file.");
            Assert(terminalPaths.All(path => !File.Exists(path)),
                "Ordinary prepared cleanup retry retained a terminal quarantine.");
            Assert(!File.Exists(journalPath),
                "Ordinary prepared cleanup retry retained its completed journal.");
            await new ManagedFilesInstaller(stateStore).RecoverPendingOperationsAsync();
            Assert(File.Exists(externalBackup),
                "Repeated prepared cleanup recovery deleted external backup evidence.");
        }
        else if (interruptionPhase == ManagedInstallerPhase.FileQuarantined)
        {
            Assert(File.Exists(journalPath),
                "Q quarantine lost recovery authority after its first move.");
            Assert(!File.Exists(cleanupPaths[0]) && File.Exists(terminalPaths[0]) &&
                   File.Exists(cleanupPaths[1]) && !File.Exists(terminalPaths[1]),
                "First-quarantine interruption left the wrong multi-file Q state.");

            await File.WriteAllBytesAsync(cleanupPaths[0], oldPayloads[0]);
            var evidencePaths = cleanupPaths
                .Concat(terminalPaths)
                .Append(journalPath)
                .ToArray();
            var evidenceBefore = SnapshotPaths(evidencePaths);
            await AssertThrowsAsync<ManagedFilesInstallException>(
                () => new ManagedFilesInstaller(stateStore).RecoverPendingOperationsAsync(),
                "Q retry accepted ambiguous source+terminal quarantine evidence.");
            await AssertThrowsAsync<ManagedFilesInstallException>(
                () => new ManagedFilesInstaller(stateStore).RecoverPendingOperationsAsync(),
                "Repeated Q retry accepted ambiguous source+terminal evidence.");
            Assert(SnapshotPaths(evidencePaths) == evidenceBefore,
                "Fail-closed Q retry changed transaction evidence.");
            Assert(File.Exists(journalPath),
                "Fail-closed Q retry discarded recovery authority.");
        }
        else if (interruptionPhase == ManagedInstallerPhase.FileCleanupCompleted)
        {
            Assert(!File.Exists(journalPath),
                "Terminal cleanup retained recovery authority after its first delete.");
            Assert(cleanupPaths.All(path => !File.Exists(path)) &&
                   !File.Exists(terminalPaths[0]) && File.Exists(terminalPaths[1]),
                "First-delete interruption left the wrong terminal quarantine state.");

            await File.WriteAllBytesAsync(terminalPaths[0], oldPayloads[0]);
            await File.WriteAllBytesAsync(cleanupPaths[0], oldPayloads[0]);
            var remainingQuarantineBytes = await File.ReadAllBytesAsync(terminalPaths[1]);
            await new ManagedFilesInstaller(stateStore).RecoverPendingOperationsAsync();
            await new ManagedFilesInstaller(stateStore).RecoverPendingOperationsAsync();

            Assert(File.Exists(cleanupPaths[0]) &&
                   File.ReadAllBytes(cleanupPaths[0]).AsSpan().SequenceEqual(oldPayloads[0]),
                "Recovery deleted a recreated source Q after authority revoke.");
            Assert(File.Exists(terminalPaths[0]) &&
                   File.ReadAllBytes(terminalPaths[0]).AsSpan().SequenceEqual(oldPayloads[0]),
                "Recovery deleted a byte-identical terminal Q recreated after delete.");
            Assert(File.Exists(terminalPaths[1]) &&
                   File.ReadAllBytes(terminalPaths[1]).AsSpan()
                       .SequenceEqual(remainingQuarantineBytes),
                "Recovery deleted terminal Q evidence after authority was revoked.");
        }
        else
        {
            Assert(!File.Exists(journalPath) && cleanupPaths.All(path => !File.Exists(path)),
                "Authority-revoked cleanup retained journal or source Q evidence.");
            Assert(File.Exists(terminalPaths[0]) &&
                   File.ReadAllBytes(terminalPaths[0]).AsSpan()
                       .SequenceEqual(authorityRaceBytes),
                "Cleanup deleted an arbitrary replacement raced after authority revoke.");
            Assert(!File.Exists(terminalPaths[1]),
                "Cleanup retained an unchanged terminal quarantine after authority revoke.");
            await new ManagedFilesInstaller(stateStore).RecoverPendingOperationsAsync();
            await new ManagedFilesInstaller(stateStore).RecoverPendingOperationsAsync();
            Assert(File.ReadAllBytes(terminalPaths[0]).AsSpan()
                    .SequenceEqual(authorityRaceBytes),
                "Repeated recovery deleted authority-revoked external evidence.");
        }

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

    private static async Task CheckLegacyCommittedBackupRecreationAsync(string root)
    {
        await CheckLegacyCommittedBackupRecreationAtPhaseAsync(
            Path.Combine(root, "first-quarantine"),
            ManagedInstallerPhase.FileQuarantined,
            'a');
        await CheckLegacyCommittedBackupRecreationAtPhaseAsync(
            Path.Combine(root, "first-delete"),
            ManagedInstallerPhase.FileCleanupCompleted,
            'b');
    }

    private static async Task CheckLegacyCommittedBackupRecreationAtPhaseAsync(
        string dataRoot,
        ManagedInstallerPhase interruptionPhase,
        char operationIdCharacter)
    {
        var operationId = new string(operationIdCharacter, 32);
        var gameRoot = Path.Combine(dataRoot, "game");
        var destinations = new[]
        {
            Destination,
            "Anvil/Content/Paks/Test-Language-Second.pak"
        };
        var oldPayloads = new[]
        {
            Encoding.UTF8.GetBytes("legacy committed old first"),
            Encoding.UTF8.GetBytes("legacy committed old second")
        };
        var newPayloads = new[]
        {
            Encoding.UTF8.GetBytes("legacy committed new first"),
            Encoding.UTF8.GetBytes("legacy committed new second")
        };
        var oldState = CreateMultiFileState(
            "1.0.0",
            gameRoot,
            destinations,
            oldPayloads) with
        {
            SchemaVersion = 1,
            InstallationKey = null
        };
        var newState = CreateMultiFileState(
            "1.1.0",
            gameRoot,
            destinations,
            newPayloads) with
        {
            SchemaVersion = 1,
            InstallationKey = null
        };
        var statePath = GetStatePath(dataRoot, RuProductId);
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        var stateBytes = JsonSerializer.SerializeToUtf8Bytes(
            newState,
            LegacySerializerOptions);
        await File.WriteAllBytesAsync(statePath, stateBytes);

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

        var journalPath = GetJournalPath(dataRoot, RuProductId);
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        await File.WriteAllBytesAsync(
            journalPath,
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    schemaVersion = 1,
                    operationId,
                    productId = RuProductId,
                    oldState,
                    newState
                },
                LegacySerializerOptions));

        var completedTargetPhase = 0;
        var interrupted = new ManagedFilesInstaller(new InstallationStateStore(dataRoot))
        {
            PhaseObserver = phase =>
            {
                if (phase == interruptionPhase && ++completedTargetPhase == 1)
                {
                    throw new ManagedInstallerInterruptionException(
                        $"Injected legacy backup interruption at {phase}.");
                }

                return Task.CompletedTask;
            }
        };
        await AssertThrowsAsync<ManagedInstallerInterruptionException>(
            () => interrupted.RecoverPendingOperationsAsync(),
            $"Legacy committed cleanup did not stop at {interruptionPhase}.");

        var terminalPaths = backupPaths
            .Select(path => GetTerminalQuarantinePath(path, operationId))
            .ToArray();
        if (interruptionPhase == ManagedInstallerPhase.FileQuarantined)
        {
            Assert(File.Exists(journalPath),
                "Legacy backup quarantine lost recovery authority after its first move.");
            Assert(!File.Exists(backupPaths[0]) && File.Exists(terminalPaths[0]) &&
                   File.Exists(backupPaths[1]) && !File.Exists(terminalPaths[1]),
                "Legacy first-quarantine interruption left the wrong backup evidence.");
            await File.WriteAllBytesAsync(backupPaths[0], oldPayloads[0]);
        }
        else
        {
            Assert(!File.Exists(journalPath),
                "Legacy committed cleanup retained authority after its first delete.");
            Assert(backupPaths.All(path => !File.Exists(path)) &&
                   !File.Exists(terminalPaths[0]) && File.Exists(terminalPaths[1]),
                "Legacy first-delete interruption left the wrong terminal evidence.");
            await File.WriteAllBytesAsync(terminalPaths[0], oldPayloads[0]);
            await File.WriteAllBytesAsync(backupPaths[0], oldPayloads[0]);
        }

        var remainingEvidencePath = interruptionPhase == ManagedInstallerPhase.FileQuarantined
            ? terminalPaths[0]
            : terminalPaths[1];
        var remainingEvidenceBytes = await File.ReadAllBytesAsync(remainingEvidencePath);

        if (interruptionPhase == ManagedInstallerPhase.FileQuarantined)
        {
            var evidencePaths = backupPaths
                .Concat(terminalPaths)
                .Append(journalPath)
                .ToArray();
            var evidenceBefore = SnapshotPaths(evidencePaths);
            await AssertThrowsAsync<ManagedFilesInstallException>(
                () => new ManagedFilesInstaller(new InstallationStateStore(dataRoot))
                    .RecoverPendingOperationsAsync(),
                "Legacy backup retry accepted ambiguous source+quarantine evidence.");
            await AssertThrowsAsync<ManagedFilesInstallException>(
                () => new ManagedFilesInstaller(new InstallationStateStore(dataRoot))
                    .RecoverPendingOperationsAsync(),
                "Repeated legacy backup retry accepted ambiguous evidence.");
            Assert(SnapshotPaths(evidencePaths) == evidenceBefore,
                "Fail-closed legacy backup retry changed transaction evidence.");
            Assert(File.Exists(journalPath),
                "Fail-closed legacy backup retry discarded recovery authority.");
        }
        else
        {
            await new ManagedFilesInstaller(new InstallationStateStore(dataRoot))
                .RecoverPendingOperationsAsync();
            await new ManagedFilesInstaller(new InstallationStateStore(dataRoot))
                .RecoverPendingOperationsAsync();
            Assert(File.Exists(backupPaths[0]) &&
                   File.ReadAllBytes(backupPaths[0]).AsSpan().SequenceEqual(oldPayloads[0]),
                "Recovery deleted a recreated legacy backup source after revoke.");
            Assert(File.Exists(terminalPaths[0]) &&
                   File.ReadAllBytes(terminalPaths[0]).AsSpan().SequenceEqual(oldPayloads[0]),
                "Recovery deleted a recreated terminal legacy-backup quarantine.");
            Assert(File.Exists(terminalPaths[1]) &&
                   File.ReadAllBytes(terminalPaths[1]).AsSpan()
                       .SequenceEqual(remainingEvidenceBytes),
                "Recovery deleted remaining legacy-backup evidence after authority revoke.");
        }
        Assert(File.ReadAllBytes(statePath).AsSpan().SequenceEqual(stateBytes),
            "Repeated legacy committed recovery changed persisted new state.");
        for (var index = 0; index < destinations.Length; index++)
        {
            var livePath = Path.Combine(
                gameRoot,
                destinations[index].Replace('/', Path.DirectorySeparatorChar));
            Assert(File.ReadAllBytes(livePath).AsSpan().SequenceEqual(newPayloads[index]),
                "Repeated legacy committed recovery changed a live new file.");
        }
    }

    private static async Task CheckCommittedReadyMarkerOwnershipAsync(string root)
    {
        await ExerciseRecoveryOwnershipWindowAsync(
            await CreateCommittedReadyMarkerFixtureAsync(
                Path.Combine(root, "first-quarantine"),
                'c'),
            ManagedInstallerPhase.FileQuarantined,
            targetIndex: 0,
            "committed v2 ready marker");
        await ExerciseRecoveryOwnershipWindowAsync(
            await CreateCommittedReadyMarkerFixtureAsync(
                Path.Combine(root, "first-delete"),
                'd'),
            ManagedInstallerPhase.FileCleanupCompleted,
            targetIndex: 0,
            "committed v2 ready marker");
    }

    private static async Task CheckLegacyRollbackMarkerOwnershipAsync(string root)
    {
        await ExerciseRecoveryOwnershipWindowAsync(
            await CreateLegacyRollbackMarkerFixtureAsync(
                Path.Combine(root, "first-quarantine"),
                'c'),
            ManagedInstallerPhase.FileQuarantined,
            targetIndex: 0,
            "legacy rollbackComplete not-activated marker");
        await ExerciseRecoveryOwnershipWindowAsync(
            await CreateLegacyRollbackMarkerFixtureAsync(
                Path.Combine(root, "first-delete"),
                'd'),
            ManagedInstallerPhase.FileCleanupCompleted,
            targetIndex: 0,
            "legacy rollbackComplete not-activated marker");
    }

    private static async Task CheckCanonicalRollbackOwnershipAsync(string root)
    {
        // Each new-only destination quarantines its ready marker before its canonical
        // final. Index 1 is therefore the first canonical final in the ownership list.
        await ExerciseRecoveryOwnershipWindowAsync(
            await CreateCanonicalRollbackFixtureAsync(
                Path.Combine(root, "first-quarantine"),
                'c'),
            ManagedInstallerPhase.FileQuarantined,
            targetIndex: 1,
            "rollback new-only canonical final",
            allowsRecreatedSource: true);
        await ExerciseRecoveryOwnershipWindowAsync(
            await CreateCanonicalRollbackFixtureAsync(
                Path.Combine(root, "first-delete"),
                'd'),
            ManagedInstallerPhase.FileCleanupCompleted,
            targetIndex: 1,
            "rollback new-only canonical final");
        await ExerciseRecoveryOwnershipWindowAsync(
            await CreateCanonicalRollbackFixtureAsync(
                Path.Combine(root, "authority-revoked"),
                'e'),
            ManagedInstallerPhase.RecoveryAuthorityRevoked,
            targetIndex: 1,
            "rollback new-only canonical final");
    }

    private static async Task ExerciseRecoveryOwnershipWindowAsync(
        RecoveryOwnershipFixture fixture,
        ManagedInstallerPhase interruptionPhase,
        int targetIndex,
        string evidenceName,
        bool allowsRecreatedSource = false)
    {
        var terminalPaths = fixture.ArtifactPaths
            .Select(path => GetTerminalQuarantinePath(path, fixture.OperationId))
            .ToArray();
        var targetOccurrence = targetIndex + 1;
        var observedTargetPhase = 0;
        var arbitraryRaceBytes = Encoding.UTF8.GetBytes(
            $"external arbitrary replacement for {evidenceName}");
        var installer = new ManagedFilesInstaller(fixture.StateStore)
        {
            PhaseObserver = async phase =>
            {
                if (phase != interruptionPhase)
                {
                    return;
                }

                if (phase == ManagedInstallerPhase.RecoveryAuthorityRevoked)
                {
                    Assert(!File.Exists(fixture.JournalPath),
                        $"{evidenceName} reported revoked authority before journal deletion.");
                    Assert(fixture.ArtifactPaths.All(path => !File.Exists(path)),
                        $"{evidenceName} retained a canonical source when authority was revoked.");
                    Assert(terminalPaths.All(File.Exists),
                        $"{evidenceName} authority race did not see all terminal quarantines.");
                    await File.WriteAllBytesAsync(
                        terminalPaths[targetIndex],
                        arbitraryRaceBytes);
                    return;
                }

                if (++observedTargetPhase == targetOccurrence)
                {
                    throw new ManagedInstallerInterruptionException(
                        $"Injected {evidenceName} interruption at {phase}.");
                }
            }
        };

        if (interruptionPhase == ManagedInstallerPhase.RecoveryAuthorityRevoked)
        {
            await installer.RecoverPendingOperationsAsync();
        }
        else
        {
            await AssertThrowsAsync<ManagedInstallerInterruptionException>(
                () => installer.RecoverPendingOperationsAsync(),
                $"{evidenceName} did not stop at {interruptionPhase} occurrence " +
                $"{targetOccurrence}.");
        }

        if (interruptionPhase == ManagedInstallerPhase.FileQuarantined)
        {
            Assert(File.Exists(fixture.JournalPath),
                $"{evidenceName} lost recovery authority after quarantine.");
            for (var index = 0; index < fixture.ArtifactPaths.Count; index++)
            {
                var wasQuarantined = index <= targetIndex;
                Assert(File.Exists(fixture.ArtifactPaths[index]) != wasQuarantined &&
                       File.Exists(terminalPaths[index]) == wasQuarantined,
                    $"{evidenceName} left unexpected evidence at index {index}.");
            }

            await File.WriteAllBytesAsync(
                fixture.ArtifactPaths[targetIndex],
                fixture.ArtifactBytes[targetIndex]);
            if (!allowsRecreatedSource)
            {
                var evidencePaths = fixture.ArtifactPaths
                    .Concat(terminalPaths)
                    .Append(fixture.JournalPath)
                    .ToArray();
                var evidenceBefore = SnapshotPaths(evidencePaths);
                await AssertThrowsAsync<ManagedFilesInstallException>(
                    () => new ManagedFilesInstaller(fixture.StateStore)
                        .RecoverPendingOperationsAsync(),
                    $"Retry accepted ambiguous source+quarantine {evidenceName} evidence.");
                await AssertThrowsAsync<ManagedFilesInstallException>(
                    () => new ManagedFilesInstaller(fixture.StateStore)
                        .RecoverPendingOperationsAsync(),
                    $"Repeated retry accepted ambiguous {evidenceName} evidence.");
                Assert(SnapshotPaths(evidencePaths) == evidenceBefore,
                    $"Fail-closed {evidenceName} retry changed transaction evidence.");
                Assert(File.Exists(fixture.JournalPath),
                    $"Fail-closed {evidenceName} retry discarded recovery authority.");
                await fixture.AssertStableAsync();
                return;
            }

            await new ManagedFilesInstaller(fixture.StateStore)
                .RecoverPendingOperationsAsync();
            await new ManagedFilesInstaller(fixture.StateStore)
                .RecoverPendingOperationsAsync();

            Assert(File.Exists(fixture.ArtifactPaths[targetIndex]) &&
                   File.ReadAllBytes(fixture.ArtifactPaths[targetIndex]).AsSpan()
                       .SequenceEqual(fixture.ArtifactBytes[targetIndex]),
                $"Retry deleted external {evidenceName} recreated after quarantine.");
            Assert(fixture.ArtifactPaths
                    .Where((_, index) => index != targetIndex)
                    .All(path => !File.Exists(path)) &&
                   terminalPaths.All(path => !File.Exists(path)),
                $"{evidenceName} retry retained Hub-owned quarantine evidence.");
            Assert(!File.Exists(fixture.JournalPath),
                $"{evidenceName} retry retained its completed journal.");
        }
        else if (interruptionPhase == ManagedInstallerPhase.FileCleanupCompleted)
        {
            Assert(!File.Exists(fixture.JournalPath) &&
                   fixture.ArtifactPaths.All(path => !File.Exists(path)),
                $"{evidenceName} retained source or journal after authority revoke.");
            for (var index = 0; index < terminalPaths.Length; index++)
            {
                Assert(File.Exists(terminalPaths[index]) == (index > targetIndex),
                    $"{evidenceName} left unexpected terminal evidence at index {index}.");
            }

            await File.WriteAllBytesAsync(
                terminalPaths[targetIndex],
                fixture.ArtifactBytes[targetIndex]);
            await File.WriteAllBytesAsync(
                fixture.ArtifactPaths[targetIndex],
                fixture.ArtifactBytes[targetIndex]);
            var remainingEvidence = terminalPaths
                .Select((path, index) => index > targetIndex
                    ? File.ReadAllBytes(path)
                    : null)
                .ToArray();
            await new ManagedFilesInstaller(fixture.StateStore)
                .RecoverPendingOperationsAsync();
            await new ManagedFilesInstaller(fixture.StateStore)
                .RecoverPendingOperationsAsync();

            Assert(File.Exists(fixture.ArtifactPaths[targetIndex]) &&
                   File.ReadAllBytes(fixture.ArtifactPaths[targetIndex]).AsSpan()
                       .SequenceEqual(fixture.ArtifactBytes[targetIndex]),
                $"Recovery deleted recreated source {evidenceName} evidence.");
            Assert(File.Exists(terminalPaths[targetIndex]) &&
                   File.ReadAllBytes(terminalPaths[targetIndex]).AsSpan()
                       .SequenceEqual(fixture.ArtifactBytes[targetIndex]),
                $"Recovery deleted recreated terminal {evidenceName} evidence.");
            for (var index = targetIndex + 1; index < terminalPaths.Length; index++)
            {
                Assert(File.Exists(terminalPaths[index]) &&
                       File.ReadAllBytes(terminalPaths[index]).AsSpan()
                           .SequenceEqual(remainingEvidence[index]),
                    $"Recovery deleted remaining {evidenceName} evidence after revoke.");
            }
        }
        else
        {
            Assert(!File.Exists(fixture.JournalPath) &&
                   fixture.ArtifactPaths.All(path => !File.Exists(path)),
                $"{evidenceName} authority-race cleanup retained source or journal.");
            Assert(File.Exists(terminalPaths[targetIndex]) &&
                   File.ReadAllBytes(terminalPaths[targetIndex]).AsSpan()
                       .SequenceEqual(arbitraryRaceBytes),
                $"Cleanup deleted changed {evidenceName} after authority revoke.");
            Assert(terminalPaths
                    .Where((_, index) => index != targetIndex)
                    .All(path => !File.Exists(path)),
                $"Cleanup retained unchanged {evidenceName} quarantine evidence.");
            await new ManagedFilesInstaller(fixture.StateStore)
                .RecoverPendingOperationsAsync();
            await new ManagedFilesInstaller(fixture.StateStore)
                .RecoverPendingOperationsAsync();
            Assert(File.ReadAllBytes(terminalPaths[targetIndex]).AsSpan()
                    .SequenceEqual(arbitraryRaceBytes),
                $"Repeated recovery deleted changed {evidenceName} evidence.");
        }

        await fixture.AssertStableAsync();
    }

    private static async Task<RecoveryOwnershipFixture>
        CreateCommittedReadyMarkerFixtureAsync(string dataRoot, char operationIdCharacter)
    {
        var operationId = new string(operationIdCharacter, 32);
        var gameRoot = Path.Combine(dataRoot, "game");
        var destinations = new[]
        {
            Destination,
            "Anvil/Content/Paks/Test-Language-Second.pak"
        };
        var payloads = new[]
        {
            Encoding.UTF8.GetBytes("committed ready marker first payload"),
            Encoding.UTF8.GetBytes("committed ready marker second payload")
        };
        var newState = CreateMultiFileState(
            "1.0.0",
            gameRoot,
            destinations,
            payloads);
        var stateStore = new InstallationStateStore(dataRoot);
        await stateStore.SaveAsync(newState);
        var journal = new ManagedUpdateJournal
        {
            SchemaVersion = 2,
            OperationId = operationId,
            InstallationKey = InstallationKey,
            OldState = null,
            NewState = newState,
            PreservedDestinations = []
        };
        await new ManagedUpdateJournalStore(stateStore).SaveAsync(journal);

        var markerPaths = new List<string>();
        var markerBytes = new List<byte[]>();
        for (var index = 0; index < destinations.Length; index++)
        {
            var livePath = Path.Combine(
                gameRoot,
                destinations[index].Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(livePath)!);
            await File.WriteAllBytesAsync(livePath, payloads[index]);
            var markerPath =
                $"{livePath}.nfg-update-stage-{operationId}.disabled.ready-owner";
            var bytes = CreateStageMarkerBytes(
                operationId,
                destinations[index],
                payloads[index],
                "ready");
            await File.WriteAllBytesAsync(markerPath, bytes);
            markerPaths.Add(markerPath);
            markerBytes.Add(bytes);
        }

        return new RecoveryOwnershipFixture(
            stateStore,
            GetJournalPath(dataRoot, InstallationKey),
            operationId,
            markerPaths,
            markerBytes,
            async () =>
            {
                Assert(InstallationStateMigrator.StatesEqual(
                        await stateStore.LoadAsync(InstallationKey)
                            ?? throw new InvalidOperationException(
                                "Ready-marker recovery lost committed new state."),
                        newState),
                    "Ready-marker recovery changed committed new state.");
                for (var index = 0; index < destinations.Length; index++)
                {
                    var livePath = Path.Combine(
                        gameRoot,
                        destinations[index].Replace('/', Path.DirectorySeparatorChar));
                    Assert(File.ReadAllBytes(livePath).AsSpan().SequenceEqual(payloads[index]),
                        "Ready-marker recovery changed a committed live file.");
                }
            });
    }

    private static async Task<RecoveryOwnershipFixture>
        CreateLegacyRollbackMarkerFixtureAsync(string dataRoot, char operationIdCharacter)
    {
        var operationId = new string(operationIdCharacter, 32);
        var gameRoot = Path.Combine(dataRoot, "game");
        var oldPayload = Encoding.UTF8.GetBytes("legacy marker old managed payload");
        var newOnlyDestinations = new[]
        {
            "Anvil/Content/Paks/Legacy-Marker-New-A.pak",
            "Anvil/Content/Paks/Legacy-Marker-New-B.pak"
        };
        var newOnlyPayloads = new[]
        {
            Encoding.UTF8.GetBytes("legacy marker new-only first"),
            Encoding.UTF8.GetBytes("legacy marker new-only second")
        };
        var oldState = CreateFileBackedState("1.0.0", gameRoot, oldPayload);
        var newState = oldState with
        {
            Version = "1.1.0",
            PackageSizeBytes = oldPayload.LongLength +
                               newOnlyPayloads.Sum(payload => payload.LongLength),
            PackageSha256 = Sha256(
                oldPayload.Concat(newOnlyPayloads.SelectMany(payload => payload)).ToArray()),
            InstalledAt = oldState.InstalledAt.AddMinutes(1),
            Files =
            [
                oldState.Files[0],
                .. newOnlyDestinations.Select((destination, index) =>
                    new InstalledFileState
                    {
                        Destination = destination,
                        SizeBytes = newOnlyPayloads[index].LongLength,
                        Sha256 = Sha256(newOnlyPayloads[index])
                    })
            ]
        };
        var statePath = GetStatePath(dataRoot, RuProductId);
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        var stateBytes = JsonSerializer.SerializeToUtf8Bytes(
            oldState,
            LegacySerializerOptions);
        await File.WriteAllBytesAsync(statePath, stateBytes);
        var oldPath = Path.Combine(
            gameRoot,
            Destination.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!);
        await File.WriteAllBytesAsync(oldPath, oldPayload);

        var journalPath = GetJournalPath(dataRoot, RuProductId);
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        await File.WriteAllBytesAsync(
            journalPath,
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    schemaVersion = 1,
                    operationId,
                    productId = RuProductId,
                    oldState,
                    newState,
                    rollbackComplete = true
                },
                LegacySerializerOptions));

        var markerPaths = new List<string>();
        var markerBytes = new List<byte[]>();
        for (var index = 0; index < newOnlyDestinations.Length; index++)
        {
            var finalPath = Path.Combine(
                gameRoot,
                newOnlyDestinations[index].Replace('/', Path.DirectorySeparatorChar));
            var markerPath =
                $"{finalPath}.nfg-update-stage-{operationId}.disabled.not-activated-owner";
            var bytes = CreateStageMarkerBytes(
                operationId,
                newOnlyDestinations[index],
                newOnlyPayloads[index],
                "not-activated");
            await File.WriteAllBytesAsync(markerPath, bytes);
            markerPaths.Add(markerPath);
            markerBytes.Add(bytes);
        }

        var stateStore = new InstallationStateStore(dataRoot);
        return new RecoveryOwnershipFixture(
            stateStore,
            journalPath,
            operationId,
            markerPaths,
            markerBytes,
            () =>
            {
                Assert(File.ReadAllBytes(statePath).AsSpan().SequenceEqual(stateBytes),
                    "Legacy marker recovery changed persisted old state.");
                Assert(File.ReadAllBytes(oldPath).AsSpan().SequenceEqual(oldPayload),
                    "Legacy marker recovery changed the old managed file.");
                return Task.CompletedTask;
            });
    }

    private static async Task<RecoveryOwnershipFixture>
        CreateCanonicalRollbackFixtureAsync(string dataRoot, char operationIdCharacter)
    {
        var operationId = new string(operationIdCharacter, 32);
        var gameRoot = Path.Combine(dataRoot, "game");
        var oldPayload = Encoding.UTF8.GetBytes("canonical rollback old managed payload");
        var newOnlyDestinations = new[]
        {
            "Anvil/Content/Paks/Canonical-New-A.pak",
            "Anvil/Content/Paks/Canonical-New-B.pak"
        };
        var newOnlyPayloads = new[]
        {
            Encoding.UTF8.GetBytes("canonical rollback new-only first"),
            Encoding.UTF8.GetBytes("canonical rollback new-only second")
        };
        var oldState = CreateFileBackedState("1.0.0", gameRoot, oldPayload) with
        {
            SchemaVersion = 2,
            InstallationKey = InstallationKey
        };
        var newState = oldState with
        {
            Version = "1.1.0",
            PackageSizeBytes = oldPayload.LongLength +
                               newOnlyPayloads.Sum(payload => payload.LongLength),
            PackageSha256 = Sha256(
                oldPayload.Concat(newOnlyPayloads.SelectMany(payload => payload)).ToArray()),
            InstalledAt = oldState.InstalledAt.AddMinutes(1),
            Files =
            [
                oldState.Files[0],
                .. newOnlyDestinations.Select((destination, index) =>
                    new InstalledFileState
                    {
                        Destination = destination,
                        SizeBytes = newOnlyPayloads[index].LongLength,
                        Sha256 = Sha256(newOnlyPayloads[index])
                    })
            ]
        };
        var stateStore = new InstallationStateStore(dataRoot);
        await stateStore.SaveAsync(oldState);
        var oldPath = Path.Combine(
            gameRoot,
            Destination.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!);
        await File.WriteAllBytesAsync(oldPath, oldPayload);
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

        var artifactPaths = new List<string>();
        var artifactBytes = new List<byte[]>();
        for (var index = 0; index < newOnlyDestinations.Length; index++)
        {
            var finalPath = Path.Combine(
                gameRoot,
                newOnlyDestinations[index].Replace('/', Path.DirectorySeparatorChar));
            await File.WriteAllBytesAsync(finalPath, newOnlyPayloads[index]);
            var markerPath =
                $"{finalPath}.nfg-update-stage-{operationId}.disabled.ready-owner";
            var bytes = CreateStageMarkerBytes(
                operationId,
                newOnlyDestinations[index],
                newOnlyPayloads[index],
                "ready");
            await File.WriteAllBytesAsync(markerPath, bytes);
            artifactPaths.Add(markerPath);
            artifactBytes.Add(bytes);
            artifactPaths.Add(finalPath);
            artifactBytes.Add(newOnlyPayloads[index]);
        }

        return new RecoveryOwnershipFixture(
            stateStore,
            GetJournalPath(dataRoot, InstallationKey),
            operationId,
            artifactPaths,
            artifactBytes,
            async () =>
            {
                Assert(InstallationStateMigrator.StatesEqual(
                        await stateStore.LoadAsync(InstallationKey)
                            ?? throw new InvalidOperationException(
                                "Canonical rollback lost persisted old state."),
                        oldState),
                    "Canonical rollback changed persisted old state.");
                Assert(File.ReadAllBytes(oldPath).AsSpan().SequenceEqual(oldPayload),
                    "Canonical rollback changed the old managed file.");
            });
    }

    private static byte[] CreateStageMarkerBytes(
        string operationId,
        string destination,
        byte[] payload,
        string phase)
    {
        var descriptor = string.Join(
            '\0',
            "nfg-stage-owner/1",
            operationId,
            destination,
            payload.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Sha256(payload),
            phase);
        return SHA256.HashData(Encoding.UTF8.GetBytes(descriptor));
    }

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
        var gameRoot = Path.Combine(dataRoot, "game");
        var oldPayload = Encoding.UTF8.GetBytes("legacy terminal retry old payload");
        var newOnlyDestinations = new[]
        {
            "Anvil/Content/Paks/Test-Language-New-Only-A.pak",
            "Anvil/Content/Paks/Test-Language-New-Only-B.pak"
        };
        var newOnlyPayloads = new[]
        {
            Encoding.UTF8.GetBytes(
                "legacy terminal retry external exact new-only first payload"),
            Encoding.UTF8.GetBytes("legacy terminal retry new-only second payload")
        };
        var oldState = CreateFileBackedState("1.0.0", gameRoot, oldPayload);
        var newState = oldState with
        {
            Version = "1.1.0",
            PackageSizeBytes = oldPayload.LongLength +
                               newOnlyPayloads.Sum(payload => payload.LongLength),
            PackageSha256 = Sha256(
                oldPayload.Concat(newOnlyPayloads.SelectMany(payload => payload)).ToArray()),
            InstalledAt = oldState.InstalledAt.AddMinutes(1),
            Files =
            [
                oldState.Files[0],
                .. newOnlyDestinations.Select((destination, index) =>
                    new InstalledFileState
                    {
                        Destination = destination,
                        SizeBytes = newOnlyPayloads[index].LongLength,
                        Sha256 = Sha256(newOnlyPayloads[index])
                    })
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

        var newOnlyPaths = newOnlyDestinations
            .Select(destination => Path.Combine(
                gameRoot,
                destination.Replace('/', Path.DirectorySeparatorChar)))
            .ToArray();
        var stagePaths = newOnlyPaths
            .Select(path => $"{path}.nfg-update-stage-{operationId}.disabled")
            .ToArray();
        var markerPaths = stagePaths
            .Select(path => $"{path}.not-activated-owner")
            .ToArray();
        for (var index = 0; index < stagePaths.Length; index++)
        {
            await File.WriteAllBytesAsync(stagePaths[index], newOnlyPayloads[index]);
        }
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

        var terminalPaths = stagePaths
            .Concat(markerPaths)
            .Select(path => GetTerminalQuarantinePath(path, operationId))
            .ToArray();
        Assert(stagePaths.All(path => !File.Exists(path)) &&
               markerPaths.All(path => !File.Exists(path)) &&
               terminalPaths.All(File.Exists),
            "Legacy rollback terminal phase retained Hub staging evidence.");
        Assert(File.ReadAllBytes(statePath).AsSpan().SequenceEqual(stateBytes),
            "Legacy rollback terminal phase changed persisted old state.");
        Assert(File.ReadAllBytes(oldPath).AsSpan().SequenceEqual(oldPayload),
            "Legacy rollback terminal phase changed the old managed file.");

        await File.WriteAllBytesAsync(newOnlyPaths[0], newOnlyPayloads[0]);
        await new ManagedFilesInstaller(stateStore).RecoverPendingOperationsAsync();

        Assert(File.Exists(newOnlyPaths[0]) &&
               File.ReadAllBytes(newOnlyPaths[0]).AsSpan().SequenceEqual(newOnlyPayloads[0]),
            "Legacy terminal retry deleted an external exact new-only file.");
        Assert(!File.Exists(journalPath) &&
               stagePaths.All(path => !File.Exists(path)) &&
               markerPaths.All(path => !File.Exists(path)) &&
               terminalPaths.All(path => !File.Exists(path)),
            "Legacy terminal retry retained Hub transaction evidence.");
        Assert(File.ReadAllBytes(statePath).AsSpan().SequenceEqual(stateBytes),
            "Legacy terminal retry changed persisted old state.");
        Assert(File.ReadAllBytes(oldPath).AsSpan().SequenceEqual(oldPayload),
            "Legacy terminal retry changed the old managed file.");

        await new ManagedFilesInstaller(stateStore).RecoverPendingOperationsAsync();
        Assert(File.Exists(newOnlyPaths[0]) &&
               File.ReadAllBytes(newOnlyPaths[0]).AsSpan().SequenceEqual(newOnlyPayloads[0]),
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

    private static string GetTerminalQuarantinePath(string path, string operationId) =>
        $"{path}.nfg-terminal-{operationId}.quarantine";

    private static string SnapshotPaths(IEnumerable<string> paths) => string.Join(
        "\n",
        paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(path => File.Exists(path)
                ? $"{path}|{new FileInfo(path).Length}|{Sha256(File.ReadAllBytes(path))}"
                : $"{path}|missing"));

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

    private sealed record RecoveryOwnershipFixture(
        InstallationStateStore StateStore,
        string JournalPath,
        string OperationId,
        IReadOnlyList<string> ArtifactPaths,
        IReadOnlyList<byte[]> ArtifactBytes,
        Func<Task> AssertStableAsync);
}
