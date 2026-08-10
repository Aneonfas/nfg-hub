using Nfg.Store.Contracts;
using Nfg.Store.Core;

namespace Nfg.Store.Installation;

public sealed class InstallationStateMigrator
{
    private readonly InstallationStateStore _stateStore;
    private readonly InstallationOperationLock _operationLock;
    private readonly string _backupRoot;

    public InstallationStateMigrator(InstallationStateStore stateStore)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        _stateStore = stateStore;
        _operationLock = new InstallationOperationLock(stateStore.DataRoot);
        _backupRoot = Path.Combine(
            stateStore.DataRoot,
            "state",
            "migrations",
            "installations-v1");
    }

    public async Task<IReadOnlyList<InstalledProductState>> MigrateAsync(
        StoreCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var products = catalog.Products.ToDictionary(
            product => product.Id,
            StringComparer.Ordinal);
        var plans = await CreatePlansAsync(products, cancellationToken);

        foreach (var plan in plans.OrderBy(plan => plan.InstallationKey, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _operationLock.ExecuteAsync(
                plan.InstallationKey,
                cancellationToken,
                async () =>
                {
                    await ExecutePlanAsync(plan, cancellationToken);
                    return true;
                });
        }

        var migrated = await _stateStore.LoadAllAsync(cancellationToken);
        if (migrated.Any(state => state.SchemaVersion != 2))
        {
            throw new InstallationStateException(
                "Installation-state migration did not reach schema version 2.");
        }

        return migrated;
    }

    private async Task<IReadOnlyList<MigrationPlan>> CreatePlansAsync(
        IReadOnlyDictionary<string, ProductManifest> products,
        CancellationToken cancellationToken)
    {
        var entries = new List<MigrationEntry>();
        foreach (var snapshot in await _stateStore.ReadSnapshotsAsync(cancellationToken))
        {
            if (!products.TryGetValue(snapshot.State.ProductId, out var product))
            {
                throw new InstallationStateException(
                    $"Installed product '{snapshot.State.ProductId}' is missing from the loaded catalog.");
            }

            var installationKey = ProductInstallationKey.FromManifest(product);
            InstallationStateStore.SanitizeInstallationKey(installationKey);
            if (snapshot.State.SchemaVersion == 2 &&
                (!string.Equals(
                     snapshot.State.InstallationKey,
                     installationKey,
                     StringComparison.Ordinal) ||
                 !string.Equals(snapshot.FileKey, installationKey, StringComparison.Ordinal)))
            {
                throw new InstallationStateException(
                    $"Installation state for '{snapshot.State.ProductId}' is ambiguous for slot " +
                    $"'{installationKey}'.");
            }

            entries.Add(new MigrationEntry(
                snapshot,
                installationKey,
                snapshot.State.SchemaVersion == 1
                    ? ConvertState(snapshot.State, installationKey)
                    : snapshot.State));
        }

        var plans = new List<MigrationPlan>();
        foreach (var slot in entries.GroupBy(
                     entry => entry.InstallationKey,
                     StringComparer.OrdinalIgnoreCase))
        {
            if (slot
                    .Select(entry => entry.InstallationKey)
                    .Distinct(StringComparer.Ordinal)
                    .Skip(1)
                    .Any())
            {
                throw new InstallationStateException(
                    $"Installation slot '{slot.Key}' is referenced with ambiguous casing.");
            }

            var legacy = slot.Where(entry => entry.Snapshot.State.SchemaVersion == 1).ToArray();
            var current = slot.Where(entry => entry.Snapshot.State.SchemaVersion == 2).ToArray();
            if (legacy.Length > 1)
            {
                throw new InstallationStateException(
                    $"More than one legacy installation maps to slot '{slot.Key}'.");
            }

            if (current.Length > 1)
            {
                throw new InstallationStateException(
                    $"More than one current installation maps to slot '{slot.Key}'.");
            }

            if (legacy.Length == 0)
            {
                continue;
            }

            var legacyEntry = legacy[0];
            var backupPath = GetBackupPath(legacyEntry.Snapshot.State.ProductId);
            ValidateExistingBackup(backupPath, legacyEntry.Snapshot.Bytes);

            if (current.Length == 1)
            {
                var currentEntry = current[0];
                if (!File.Exists(backupPath) ||
                    !StatesEqual(currentEntry.Snapshot.State, legacyEntry.ConvertedState) ||
                    string.Equals(
                        legacyEntry.Snapshot.FileKey,
                        legacyEntry.InstallationKey,
                        StringComparison.Ordinal))
                {
                    throw new InstallationStateException(
                        $"Legacy and current installation state conflict for slot '{slot.Key}'.");
                }

                plans.Add(new MigrationPlan(
                    legacyEntry.InstallationKey,
                    legacyEntry.Snapshot,
                    legacyEntry.ConvertedState,
                    backupPath,
                    CleanupOnly: true));
                continue;
            }

            plans.Add(new MigrationPlan(
                legacyEntry.InstallationKey,
                legacyEntry.Snapshot,
                legacyEntry.ConvertedState,
                backupPath,
                CleanupOnly: false));
        }

        return plans;
    }

    private async Task ExecutePlanAsync(
        MigrationPlan plan,
        CancellationToken cancellationToken)
    {
        var sourcePath = plan.Source.Path;
        if (!File.Exists(sourcePath))
        {
            var completed = await _stateStore.LoadAsync(
                plan.InstallationKey,
                cancellationToken);
            if (completed is not null && StatesEqual(completed, plan.ConvertedState))
            {
                RequireExactBackup(plan.BackupPath, plan.Source.Bytes);
                return;
            }

            throw new InstallationStateException(
                $"Legacy installation state '{sourcePath}' changed during migration.");
        }

        var sourceBytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
        if (!sourceBytes.AsSpan().SequenceEqual(plan.Source.Bytes))
        {
            var targetPath = _stateStore.GetStatePath(plan.InstallationKey);
            if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                var completed = await _stateStore.LoadAsync(
                    plan.InstallationKey,
                    cancellationToken);
                if (completed is not null && StatesEqual(completed, plan.ConvertedState))
                {
                    RequireExactBackup(plan.BackupPath, plan.Source.Bytes);
                    return;
                }
            }

            throw new InstallationStateException(
                $"Legacy installation state '{sourcePath}' changed during migration.");
        }

        await EnsureExactBackupAsync(plan.BackupPath, sourceBytes, cancellationToken);
        if (!plan.CleanupOnly)
        {
            var targetPath = _stateStore.GetStatePath(plan.InstallationKey);
            if (!string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(targetPath))
            {
                var current = await _stateStore.LoadAsync(plan.InstallationKey, cancellationToken);
                if (current is null || !StatesEqual(current, plan.ConvertedState))
                {
                    throw new InstallationStateException(
                        $"Installation slot '{plan.InstallationKey}' became occupied during migration.");
                }
            }
            else
            {
                if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    await _stateStore.SaveAsync(plan.ConvertedState, cancellationToken);
                }
                else
                {
                    try
                    {
                        await _stateStore.SaveNewAsync(plan.ConvertedState, cancellationToken);
                    }
                    catch (IOException) when (File.Exists(targetPath))
                    {
                        var concurrentlyCommitted = await _stateStore.LoadAsync(
                            plan.InstallationKey,
                            cancellationToken);
                        if (concurrentlyCommitted is null ||
                            !StatesEqual(concurrentlyCommitted, plan.ConvertedState))
                        {
                            throw new InstallationStateException(
                                $"Installation slot '{plan.InstallationKey}' became occupied during migration.");
                        }
                    }
                }
            }
        }

        var migratedState = await _stateStore.LoadAsync(plan.InstallationKey, cancellationToken);
        if (migratedState is null || !StatesEqual(migratedState, plan.ConvertedState))
        {
            throw new InstallationStateException(
                $"Installation slot '{plan.InstallationKey}' was not committed safely.");
        }

        var target = _stateStore.GetStatePath(plan.InstallationKey);
        if (!string.Equals(sourcePath, target, StringComparison.OrdinalIgnoreCase))
        {
            var finalSourceBytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
            if (!finalSourceBytes.AsSpan().SequenceEqual(plan.Source.Bytes))
            {
                throw new InstallationStateException(
                    $"Legacy installation state '{sourcePath}' changed before cleanup.");
            }

            File.Delete(sourcePath);
        }
    }

    private async Task EnsureExactBackupAsync(
        string backupPath,
        byte[] sourceBytes,
        CancellationToken cancellationToken)
    {
        if (File.Exists(backupPath))
        {
            ValidateExistingBackup(backupPath, sourceBytes);
            return;
        }

        try
        {
            await InstallationStateStore.WriteBytesAtomicAsync(
                backupPath,
                sourceBytes,
                overwrite: false,
                cancellationToken);
        }
        catch (IOException exception) when (File.Exists(backupPath))
        {
            try
            {
                ValidateExistingBackup(backupPath, sourceBytes);
            }
            catch (InstallationStateException validationError)
            {
                throw new InstallationStateException(
                    $"Migration backup '{backupPath}' was created concurrently with different contents.",
                    new AggregateException(exception, validationError));
            }
        }
    }

    private string GetBackupPath(string productId) =>
        Path.Combine(
            _backupRoot,
            $"{InstallationStateStore.SanitizeProductId(productId)}.json");

    private static void ValidateExistingBackup(string backupPath, byte[] expectedBytes)
    {
        if (!File.Exists(backupPath))
        {
            return;
        }

        var backupBytes = File.ReadAllBytes(backupPath);
        if (!backupBytes.AsSpan().SequenceEqual(expectedBytes))
        {
            throw new InstallationStateException(
                $"Migration backup '{backupPath}' does not exactly match its legacy source.");
        }
    }

    private static void RequireExactBackup(string backupPath, byte[] expectedBytes)
    {
        if (!File.Exists(backupPath))
        {
            throw new InstallationStateException(
                $"Required migration backup '{backupPath}' is missing.");
        }

        ValidateExistingBackup(backupPath, expectedBytes);
    }

    private static InstalledProductState ConvertState(
        InstalledProductState legacyState,
        string installationKey) =>
        legacyState with
        {
            SchemaVersion = 2,
            InstallationKey = installationKey
        };

    internal static bool StatesEqual(
        InstalledProductState left,
        InstalledProductState right)
    {
        if (left.SchemaVersion != right.SchemaVersion ||
            !string.Equals(left.InstallationKey, right.InstallationKey, StringComparison.Ordinal) ||
            !left.ProductId.Equals(right.ProductId, StringComparison.Ordinal) ||
            !left.Version.Equals(right.Version, StringComparison.Ordinal) ||
            left.PackageSizeBytes != right.PackageSizeBytes ||
            !left.PackageSha256.Equals(right.PackageSha256, StringComparison.OrdinalIgnoreCase) ||
            !left.SteamAppId.Equals(right.SteamAppId, StringComparison.Ordinal) ||
            !left.SteamBuildId.Equals(right.SteamBuildId, StringComparison.Ordinal) ||
            !string.Equals(
                left.DetectedSteamBuildId,
                right.DetectedSteamBuildId,
                StringComparison.Ordinal) ||
            !PathsEqual(left.GameRoot, right.GameRoot) ||
            left.InstalledAt != right.InstalledAt ||
            left.IsEnabled != right.IsEnabled ||
            left.Files.Count != right.Files.Count)
        {
            return false;
        }

        var rightFiles = right.Files.ToDictionary(
            file => file.Destination,
            StringComparer.OrdinalIgnoreCase);
        return left.Files.All(file =>
            rightFiles.TryGetValue(file.Destination, out var rightFile) &&
            file.SizeBytes == rightFile.SizeBytes &&
            file.Sha256.Equals(rightFile.Sha256, StringComparison.OrdinalIgnoreCase));
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return Path.GetFullPath(left).Equals(
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InstallationStateException(
                "Installation state contains an invalid game root.",
                exception);
        }
    }

    private sealed record MigrationEntry(
        InstallationStateSnapshot Snapshot,
        string InstallationKey,
        InstalledProductState ConvertedState);

    private sealed record MigrationPlan(
        string InstallationKey,
        InstallationStateSnapshot Source,
        InstalledProductState ConvertedState,
        string BackupPath,
        bool CleanupOnly);
}
