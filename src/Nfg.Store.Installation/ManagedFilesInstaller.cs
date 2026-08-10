using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using Nfg.Store.Contracts;
using Nfg.Store.Core;

namespace Nfg.Store.Installation;

public sealed class ManagedFilesInstaller(IInstallationStateStore stateStore)
{
    private const string DisabledFileSuffix = ".nfg-disabled";
    private readonly ManagedUpdateJournalStore _updateJournalStore = new(stateStore);
    private readonly InstallationOperationLock _operationLock = new(stateStore.DataRoot);

    public async Task<ManagedInstallResult> InstallAsync(
        ValidatedPackage package,
        ProductManifest product,
        string gameRoot,
        CancellationToken cancellationToken = default)
    {
        return await InstallAsync(
            package,
            product,
            gameRoot,
            detectedSteamBuildId: null,
            cancellationToken);
    }

    public async Task<ManagedInstallResult> InstallAsync(
        ValidatedPackage package,
        ProductManifest product,
        string gameRoot,
        string? detectedSteamBuildId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        return await ExecuteLockedAsync(
            product.Id,
            cancellationToken,
            async () =>
            {
                await RecoverPendingUpdateCoreAsync(product.Id);
                return await InstallCoreAsync(
                    package,
                    product,
                    gameRoot,
                    detectedSteamBuildId,
                    cancellationToken);
            });
    }

    private async Task<ManagedInstallResult> InstallCoreAsync(
        ValidatedPackage package,
        ProductManifest product,
        string gameRoot,
        string? detectedSteamBuildId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        ValidateDetectedSteamBuildId(detectedSteamBuildId);

        var payload = product.Release.Payload
            ?? throw new ManagedFilesInstallException("The product does not declare a package payload.");
        ValidatePackageIdentity(package, product);

        var root = Path.GetFullPath(gameRoot);
        if (!Directory.Exists(root))
        {
            throw new ManagedFilesInstallException($"Game directory '{root}' does not exist.");
        }

        var plans = package.Files
            .Select(file => new FilePlan(file, ResolveTarget(root, file.DestinationRelativePath)))
            .ToArray();
        var existingState = await stateStore.LoadAsync(product.Id, cancellationToken);
        if (existingState is not null)
        {
            await VerifyExistingStateAsync(existingState, package, product, root, plans, cancellationToken);
            return new ManagedInstallResult(ManagedInstallOutcome.AlreadyInstalled, existingState);
        }

        var adoptedCount = 0;
        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(plan.TargetPath))
            {
                throw new ManagedFilesInstallException(
                    $"Package target '{plan.TargetPath}' is an existing directory.");
            }

            if (!File.Exists(plan.TargetPath))
            {
                continue;
            }

            RejectReparsePoint(plan.TargetPath);

            if (!await MatchesAsync(
                    plan.TargetPath,
                    plan.PackageFile.SizeBytes,
                    plan.PackageFile.Sha256,
                    cancellationToken))
            {
                throw new ManagedFilesInstallException(
                    $"Package target '{plan.TargetPath}' already exists and is not owned by NFG Hub.");
            }

            plan.Adopted = true;
            adoptedCount++;
        }

        var operationId = Guid.NewGuid().ToString("N");
        var stagedPaths = new List<string>();
        var createdTargets = new List<FilePlan>();
        try
        {
            await using var archiveStream = new FileStream(
                package.ArchivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await VerifyArchiveAsync(archiveStream, payload, cancellationToken);
            archiveStream.Position = 0;
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
            var entries = archive.Entries.ToDictionary(
                entry => entry.FullName,
                StringComparer.Ordinal);

            foreach (var plan in plans.Where(plan => !plan.Adopted))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entries.TryGetValue(plan.PackageFile.ArchiveEntryPath, out var entry))
                {
                    throw new ManagedFilesInstallException(
                        $"Package entry '{plan.PackageFile.ArchiveEntryPath}' disappeared after validation.");
                }

                var stagePath = $"{plan.TargetPath}.nfg-stage-{operationId}.disabled";
                await ExtractVerifiedAsync(entry, stagePath, plan.PackageFile, cancellationToken);
                plan.StagePath = stagePath;
                stagedPaths.Add(stagePath);
            }

            foreach (var plan in plans.Where(plan => !plan.Adopted))
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(plan.StagePath!, plan.TargetPath);
                stagedPaths.Remove(plan.StagePath!);
                createdTargets.Add(plan);
            }

            foreach (var plan in plans)
            {
                if (!await MatchesAsync(
                        plan.TargetPath,
                        plan.PackageFile.SizeBytes,
                        plan.PackageFile.Sha256,
                        cancellationToken))
                {
                    throw new ManagedFilesInstallException(
                        $"Installed file '{plan.TargetPath}' changed before installation state was committed.");
                }
            }

            var state = CreateState(
                package,
                product,
                payload,
                root,
                detectedSteamBuildId: detectedSteamBuildId);
            await stateStore.SaveAsync(state, cancellationToken);
            var outcome = adoptedCount == plans.Length
                ? ManagedInstallOutcome.Adopted
                : ManagedInstallOutcome.Installed;
            return new ManagedInstallResult(outcome, state);
        }
        catch (Exception installError)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var plan in createdTargets.AsEnumerable().Reverse())
            {
                try
                {
                    if (await MatchesAsync(
                            plan.TargetPath,
                            plan.PackageFile.SizeBytes,
                            plan.PackageFile.Sha256,
                            CancellationToken.None))
                    {
                        File.Delete(plan.TargetPath);
                    }
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add(rollbackError);
                }
            }

            if (rollbackErrors.Count != 0)
            {
                throw new ManagedFilesInstallException(
                    "Package installation failed and automatic cleanup was incomplete.",
                    new AggregateException([installError, .. rollbackErrors]));
            }

            throw;
        }
        finally
        {
            foreach (var stagedPath in stagedPaths)
            {
                if (File.Exists(stagedPath))
                {
                    File.Delete(stagedPath);
                }
            }
        }
    }

    public Task<ManagedInstallResult> UpdateAsync(
        ValidatedPackage package,
        ProductManifest product,
        string gameRoot,
        CancellationToken cancellationToken = default)
    {
        return SwitchVersionAsync(package, product, gameRoot, cancellationToken);
    }

    public Task<ManagedInstallResult> UpdateAsync(
        ValidatedPackage package,
        ProductManifest product,
        string gameRoot,
        string? detectedSteamBuildId,
        CancellationToken cancellationToken = default)
    {
        return SwitchVersionAsync(
            package,
            product,
            gameRoot,
            detectedSteamBuildId,
            cancellationToken);
    }

    public Task<ManagedInstallResult> SwitchVersionAsync(
        ValidatedPackage package,
        ProductManifest product,
        string gameRoot,
        CancellationToken cancellationToken = default)
    {
        return SwitchVersionAsync(
            package,
            product,
            gameRoot,
            detectedSteamBuildId: null,
            cancellationToken);
    }

    public async Task<ManagedInstallResult> SwitchVersionAsync(
        ValidatedPackage package,
        ProductManifest product,
        string gameRoot,
        string? detectedSteamBuildId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        return await ExecuteLockedAsync(
            product.Id,
            cancellationToken,
            async () =>
            {
                await RecoverPendingUpdateCoreAsync(product.Id);
                return await SwitchVersionCoreAsync(
                    package,
                    product,
                    gameRoot,
                    detectedSteamBuildId,
                    cancellationToken);
            });
    }

    private async Task<ManagedInstallResult> SwitchVersionCoreAsync(
        ValidatedPackage package,
        ProductManifest product,
        string gameRoot,
        string? detectedSteamBuildId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        ValidateDetectedSteamBuildId(detectedSteamBuildId);

        var payload = product.Release.Payload
            ?? throw new ManagedFilesInstallException("The product does not declare a package payload.");
        ValidatePackageIdentity(package, product);

        var root = Path.GetFullPath(gameRoot);
        if (!Directory.Exists(root))
        {
            throw new ManagedFilesInstallException($"Game directory '{root}' does not exist.");
        }

        var oldState = await stateStore.LoadAsync(product.Id, cancellationToken)
            ?? throw new ManagedFilesInstallException(
                $"Product '{product.Id}' is not installed through NFG Hub.");
        if (!PathsEqual(oldState.GameRoot, root))
        {
            throw new ManagedFilesInstallException(
                "The managed installation is located in a different game directory.");
        }

        if (!SemanticVersionComparer.TryCompare(
                product.Release.Version,
                oldState.Version,
                out _) ||
            product.Release.Version.Equals(
                oldState.Version,
                StringComparison.Ordinal))
        {
            throw new ManagedFilesInstallException(
                $"Version '{product.Release.Version}' is already installed.");
        }

        var oldPlans = CreateStateFilePlans(oldState);
        foreach (var oldPlan in oldPlans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            oldPlan.ExistingPath = await LocateManagedFileAsync(
                oldPlan,
                requireFile: true,
                cancellationToken);
            var expectedPath = GetStateFilePath(oldPlan, oldState.IsEnabled);
            if (!PathsEqual(oldPlan.ExistingPath!, expectedPath))
            {
                throw new ManagedFilesInstallException(
                    $"Managed file '{oldPlan.ExistingPath}' does not match the saved product state.");
            }
        }

        var newPlans = package.Files
            .Select(file => new FilePlan(file, ResolveTarget(root, file.DestinationRelativePath)))
            .ToArray();
        var newState = CreateState(
            package,
            product,
            payload,
            root,
            oldState.IsEnabled,
            detectedSteamBuildId ?? oldState.DetectedSteamBuildId);
        var unchangedDestinations = GetUnchangedDestinations(oldState, newState);
        var operationId = Guid.NewGuid().ToString("N");
        var journal = new ManagedUpdateJournal
        {
            SchemaVersion = 1,
            OperationId = operationId,
            ProductId = product.Id,
            OldState = oldState,
            NewState = newState
        };

        ValidateUpdatePaths(
            oldState,
            newState,
            oldPlans,
            newPlans,
            unchangedDestinations,
            operationId);
        await _updateJournalStore.SaveAsync(journal, cancellationToken);

        try
        {
            await using var archiveStream = new FileStream(
                package.ArchivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await VerifyArchiveAsync(archiveStream, payload, cancellationToken);
            archiveStream.Position = 0;
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
            var entries = archive.Entries.ToDictionary(
                entry => entry.FullName,
                StringComparer.Ordinal);

            foreach (var newPlan in newPlans.Where(plan =>
                         !unchangedDestinations.Contains(plan.PackageFile.DestinationRelativePath)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entries.TryGetValue(newPlan.PackageFile.ArchiveEntryPath, out var entry))
                {
                    throw new ManagedFilesInstallException(
                        $"Package entry '{newPlan.PackageFile.ArchiveEntryPath}' disappeared after validation.");
                }

                var finalPath = GetPackageFilePath(newPlan, newState.IsEnabled);
                var stagePath = GetUpdateStagePath(finalPath, operationId);
                await ExtractVerifiedAsync(entry, stagePath, newPlan.PackageFile, cancellationToken);
            }

            foreach (var oldPlan in oldPlans.Where(plan =>
                         !unchangedDestinations.Contains(plan.State.Destination)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var originalPath = GetStateFilePath(oldPlan, oldState.IsEnabled);
                if (!IsRegularFile(originalPath) ||
                    !await MatchesAsync(
                        originalPath,
                        oldPlan.State.SizeBytes,
                        oldPlan.State.Sha256,
                        cancellationToken))
                {
                    throw new ManagedFilesInstallException(
                        $"Managed file '{originalPath}' changed before update replacement.");
                }

                File.Move(originalPath, GetUpdateBackupPath(originalPath, operationId));
            }

            foreach (var newPlan in newPlans.Where(plan =>
                         !unchangedDestinations.Contains(plan.PackageFile.DestinationRelativePath)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var finalPath = GetPackageFilePath(newPlan, newState.IsEnabled);
                File.Move(GetUpdateStagePath(finalPath, operationId), finalPath);
            }

            await VerifyStateFilesAsync(newState, cancellationToken);
            await stateStore.SaveAsync(newState, cancellationToken);
        }
        catch (Exception updateError)
        {
            try
            {
                await RecoverUpdateJournalCoreAsync(journal);
            }
            catch (Exception rollbackError)
            {
                throw new ManagedFilesInstallException(
                    "Product update failed and automatic rollback was incomplete.",
                    new AggregateException(updateError, rollbackError));
            }

            throw;
        }

        try
        {
            await RecoverUpdateJournalCoreAsync(journal);
        }
        catch
        {
            // The new state is already committed. The retained journal makes cleanup retryable
            // during the next Hub startup or product operation.
        }

        return new ManagedInstallResult(ManagedInstallOutcome.Updated, newState);
    }

    public async Task<InstalledProductState> SetEnabledAsync(
        string productId,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteLockedAsync(
            productId,
            cancellationToken,
            async () =>
            {
                await RecoverPendingUpdateCoreAsync(productId);
                return await SetEnabledCoreAsync(productId, isEnabled, cancellationToken);
            });
    }

    private async Task<InstalledProductState> SetEnabledCoreAsync(
        string productId,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        var state = await stateStore.LoadAsync(productId, cancellationToken)
            ?? throw new ManagedFilesInstallException(
                $"Product '{productId}' is not installed through NFG Hub.");
        var plans = CreateStateFilePlans(state);

        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            plan.ExistingPath = await LocateManagedFileAsync(
                plan,
                requireFile: true,
                cancellationToken);
        }

        var moved = new List<(StateFilePlan Plan, string Source, string Destination)>();
        try
        {
            foreach (var plan in plans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = isEnabled ? plan.TargetPath : plan.DisabledPath;
                if (PathsEqual(plan.ExistingPath!, destination))
                {
                    continue;
                }

                if (!IsRegularFile(plan.ExistingPath!) ||
                    !await MatchesAsync(
                        plan.ExistingPath!,
                        plan.State.SizeBytes,
                        plan.State.Sha256,
                        cancellationToken))
                {
                    throw new ManagedFilesInstallException(
                        $"Managed file '{plan.ExistingPath}' changed before its state could be updated.");
                }

                if (File.Exists(destination) || Directory.Exists(destination))
                {
                    throw new ManagedFilesInstallException(
                        $"Managed file destination '{destination}' is already occupied.");
                }

                File.Move(plan.ExistingPath!, destination);
                moved.Add((plan, plan.ExistingPath!, destination));
            }

            var updatedState = state with { IsEnabled = isEnabled };
            await stateStore.SaveAsync(updatedState, cancellationToken);
            return updatedState;
        }
        catch (Exception operationError)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var move in moved.AsEnumerable().Reverse())
            {
                try
                {
                    if (!IsRegularFile(move.Destination) ||
                        File.Exists(move.Source) ||
                        Directory.Exists(move.Source) ||
                        !await MatchesAsync(
                            move.Destination,
                            move.Plan.State.SizeBytes,
                            move.Plan.State.Sha256,
                            CancellationToken.None))
                    {
                        throw new ManagedFilesInstallException(
                            $"Managed file '{move.Destination}' could not be restored automatically.");
                    }

                    File.Move(move.Destination, move.Source);
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add(rollbackError);
                }
            }

            if (rollbackErrors.Count != 0)
            {
                throw new ManagedFilesInstallException(
                    "Product state change failed and automatic rollback was incomplete.",
                    new AggregateException([operationError, .. rollbackErrors]));
            }

            throw;
        }
    }

    public async Task UninstallAsync(
        string productId,
        CancellationToken cancellationToken = default)
    {
        await ExecuteLockedAsync(
            productId,
            cancellationToken,
            async () =>
            {
                await RecoverPendingUpdateCoreAsync(productId);
                await UninstallCoreAsync(productId, cancellationToken);
                return true;
            });
    }

    private async Task UninstallCoreAsync(
        string productId,
        CancellationToken cancellationToken)
    {
        var state = await stateStore.LoadAsync(productId, cancellationToken);
        if (state is null)
        {
            return;
        }

        var targets = CreateStateFilePlans(state);

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            target.ExistingPath = await LocateManagedFileAsync(
                target,
                requireFile: false,
                cancellationToken);
        }

        var operationId = Guid.NewGuid().ToString("N");
        var moved = new List<(string Source, string Tombstone)>();
        try
        {
            foreach (var target in targets.Where(target => target.ExistingPath is not null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsRegularFile(target.ExistingPath!) ||
                    !await MatchesAsync(
                        target.ExistingPath!,
                        target.State.SizeBytes,
                        target.State.Sha256,
                        cancellationToken))
                {
                    throw new ManagedFilesInstallException(
                        $"Installed file '{target.ExistingPath}' changed before removal.");
                }

                var tombstone = $"{target.ExistingPath}.nfg-remove-{operationId}.disabled";
                File.Move(target.ExistingPath!, tombstone);
                moved.Add((target.ExistingPath!, tombstone));
            }

            stateStore.Delete(productId);
        }
        catch
        {
            foreach (var (source, tombstone) in moved.AsEnumerable().Reverse())
            {
                if (File.Exists(tombstone) && !File.Exists(source))
                {
                    File.Move(tombstone, source);
                }
            }

            throw;
        }

        foreach (var (_, tombstone) in moved)
        {
            if (File.Exists(tombstone))
            {
                File.Delete(tombstone);
            }
        }
    }

    public async Task RecoverPendingOperationsAsync(
        CancellationToken cancellationToken = default)
    {
        var journals = await _updateJournalStore.LoadAllAsync(cancellationToken);
        foreach (var productId in journals
                     .Select(journal => journal.ProductId)
                     .Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ExecuteLockedAsync(
                productId,
                cancellationToken,
                async () =>
                {
                    await RecoverPendingUpdateCoreAsync(productId);
                    return true;
                });
        }
    }

    private async Task RecoverPendingUpdateCoreAsync(string productId)
    {
        var journal = await _updateJournalStore.LoadAsync(
            productId,
            CancellationToken.None);
        if (journal is not null)
        {
            await RecoverUpdateJournalCoreAsync(journal);
        }
    }

    private async Task RecoverUpdateJournalCoreAsync(ManagedUpdateJournal journal)
    {
        var currentState = await stateStore.LoadAsync(
            journal.ProductId,
            CancellationToken.None);
        if (StatesEqual(currentState, journal.NewState))
        {
            await FinalizeCommittedUpdateAsync(journal);
            return;
        }

        if (!StatesEqual(currentState, journal.OldState))
        {
            throw new ManagedFilesInstallException(
                $"Update recovery for '{journal.ProductId}' cannot identify the committed installation state.");
        }

        await RollBackUncommittedUpdateAsync(journal);
    }

    private async Task FinalizeCommittedUpdateAsync(ManagedUpdateJournal journal)
    {
        await VerifyStateFilesAsync(journal.NewState, CancellationToken.None);
        var unchangedDestinations = GetUnchangedDestinations(
            journal.OldState,
            journal.NewState);

        foreach (var oldPlan in CreateStateFilePlans(journal.OldState).Where(plan =>
                     !unchangedDestinations.Contains(plan.State.Destination)))
        {
            var originalPath = GetStateFilePath(oldPlan, journal.OldState.IsEnabled);
            await DeleteVerifiedOwnedFileIfPresentAsync(
                GetUpdateBackupPath(originalPath, journal.OperationId),
                oldPlan.State.SizeBytes,
                oldPlan.State.Sha256);
        }

        foreach (var newPlan in CreateStateFilePlans(journal.NewState).Where(plan =>
                     !unchangedDestinations.Contains(plan.State.Destination)))
        {
            var finalPath = GetStateFilePath(newPlan, journal.NewState.IsEnabled);
            DeleteStoreTemporaryFileIfPresent(
                GetUpdateStagePath(finalPath, journal.OperationId));
        }

        _updateJournalStore.Delete(journal.ProductId);
    }

    private async Task RollBackUncommittedUpdateAsync(ManagedUpdateJournal journal)
    {
        var unchangedDestinations = GetUnchangedDestinations(
            journal.OldState,
            journal.NewState);
        var oldPlans = CreateStateFilePlans(journal.OldState);
        var oldByDestination = oldPlans.ToDictionary(
            plan => plan.State.Destination,
            StringComparer.OrdinalIgnoreCase);

        foreach (var newPlan in CreateStateFilePlans(journal.NewState).Where(plan =>
                     !unchangedDestinations.Contains(plan.State.Destination)))
        {
            var finalPath = GetStateFilePath(newPlan, journal.NewState.IsEnabled);
            var stagePath = GetUpdateStagePath(finalPath, journal.OperationId);
            DeleteStoreTemporaryFileIfPresent(stagePath);

            if (Directory.Exists(finalPath))
            {
                throw new ManagedFilesInstallException(
                    $"Update rollback found a directory at '{finalPath}'.");
            }

            if (!File.Exists(finalPath))
            {
                continue;
            }

            if (await MatchesAsync(
                    finalPath,
                    newPlan.State.SizeBytes,
                    newPlan.State.Sha256,
                    CancellationToken.None))
            {
                File.Delete(finalPath);
                continue;
            }

            if (!oldByDestination.TryGetValue(newPlan.State.Destination, out var oldPlan) ||
                !PathsEqual(finalPath, GetStateFilePath(oldPlan, journal.OldState.IsEnabled)) ||
                File.Exists(GetUpdateBackupPath(finalPath, journal.OperationId)) ||
                !await MatchesAsync(
                    finalPath,
                    oldPlan.State.SizeBytes,
                    oldPlan.State.Sha256,
                    CancellationToken.None))
            {
                throw new ManagedFilesInstallException(
                    $"Update rollback refused to remove an unrecognized file at '{finalPath}'.");
            }
        }

        foreach (var oldPlan in oldPlans
                     .Where(plan => !unchangedDestinations.Contains(plan.State.Destination))
                     .Reverse())
        {
            var originalPath = GetStateFilePath(oldPlan, journal.OldState.IsEnabled);
            var backupPath = GetUpdateBackupPath(originalPath, journal.OperationId);
            if (Directory.Exists(originalPath) || Directory.Exists(backupPath))
            {
                throw new ManagedFilesInstallException(
                    $"Update rollback found a directory at a managed file path for '{originalPath}'.");
            }

            if (File.Exists(backupPath))
            {
                if (!await MatchesAsync(
                        backupPath,
                        oldPlan.State.SizeBytes,
                        oldPlan.State.Sha256,
                        CancellationToken.None))
                {
                    throw new ManagedFilesInstallException(
                        $"Update backup '{backupPath}' was changed outside NFG Hub.");
                }

                if (File.Exists(originalPath))
                {
                    if (!await MatchesAsync(
                            originalPath,
                            oldPlan.State.SizeBytes,
                            oldPlan.State.Sha256,
                            CancellationToken.None))
                    {
                        throw new ManagedFilesInstallException(
                            $"Update rollback cannot restore occupied path '{originalPath}'.");
                    }

                    File.Delete(backupPath);
                }
                else
                {
                    File.Move(backupPath, originalPath);
                }
            }
            else if (!await MatchesAsync(
                         originalPath,
                         oldPlan.State.SizeBytes,
                         oldPlan.State.Sha256,
                         CancellationToken.None))
            {
                throw new ManagedFilesInstallException(
                    $"Update rollback cannot find the original file '{originalPath}'.");
            }
        }

        await VerifyStateFilesAsync(journal.OldState, CancellationToken.None);
        _updateJournalStore.Delete(journal.ProductId);
    }

    private static void ValidateUpdatePaths(
        InstalledProductState oldState,
        InstalledProductState newState,
        IReadOnlyList<StateFilePlan> oldPlans,
        IReadOnlyList<FilePlan> newPlans,
        IReadOnlySet<string> unchangedDestinations,
        string operationId)
    {
        var oldByDestination = oldPlans.ToDictionary(
            plan => plan.State.Destination,
            StringComparer.OrdinalIgnoreCase);

        foreach (var oldPlan in oldPlans.Where(plan =>
                     !unchangedDestinations.Contains(plan.State.Destination)))
        {
            var originalPath = GetStateFilePath(oldPlan, oldState.IsEnabled);
            EnsurePathIsVacant(GetUpdateBackupPath(originalPath, operationId));
        }

        foreach (var newPlan in newPlans.Where(plan =>
                     !unchangedDestinations.Contains(plan.PackageFile.DestinationRelativePath)))
        {
            var finalPath = GetPackageFilePath(newPlan, newState.IsEnabled);
            EnsurePathIsVacant(GetUpdateStagePath(finalPath, operationId));

            if (oldByDestination.TryGetValue(
                    newPlan.PackageFile.DestinationRelativePath,
                    out var oldPlan))
            {
                var oldPath = GetStateFilePath(oldPlan, oldState.IsEnabled);
                if (!PathsEqual(finalPath, oldPath))
                {
                    throw new ManagedFilesInstallException(
                        "An update cannot change the active state of a managed file during replacement.");
                }

                continue;
            }

            EnsurePathIsVacant(newPlan.TargetPath);
            EnsurePathIsVacant(GetDisabledPath(newPlan.TargetPath));
        }
    }

    private static async Task VerifyStateFilesAsync(
        InstalledProductState state,
        CancellationToken cancellationToken)
    {
        foreach (var plan in CreateStateFilePlans(state))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedPath = GetStateFilePath(plan, state.IsEnabled);
            var unexpectedPath = state.IsEnabled ? plan.DisabledPath : plan.TargetPath;
            if (File.Exists(unexpectedPath) ||
                Directory.Exists(unexpectedPath) ||
                !IsRegularFile(expectedPath) ||
                !await MatchesAsync(
                    expectedPath,
                    plan.State.SizeBytes,
                    plan.State.Sha256,
                    cancellationToken))
            {
                throw new ManagedFilesInstallException(
                    $"Managed file '{expectedPath}' is missing, changed, or has a conflicting copy.");
            }
        }
    }

    private static HashSet<string> GetUnchangedDestinations(
        InstalledProductState oldState,
        InstalledProductState newState)
    {
        var oldFiles = oldState.Files.ToDictionary(
            file => file.Destination,
            StringComparer.OrdinalIgnoreCase);
        return newState.Files
            .Where(file =>
                oldFiles.TryGetValue(file.Destination, out var oldFile) &&
                oldFile.SizeBytes == file.SizeBytes &&
                oldFile.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            .Select(file => file.Destination)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool StatesEqual(
        InstalledProductState? left,
        InstalledProductState right)
    {
        if (left is null ||
            left.SchemaVersion != right.SchemaVersion ||
            !string.Equals(
                left.InstallationKey,
                right.InstallationKey,
                StringComparison.Ordinal) ||
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

    private static async Task DeleteVerifiedOwnedFileIfPresentAsync(
        string path,
        long expectedSize,
        string expectedSha256)
    {
        if (Directory.Exists(path))
        {
            throw new ManagedFilesInstallException(
                $"Hub-owned temporary path '{path}' is occupied by a directory.");
        }

        if (!File.Exists(path))
        {
            return;
        }

        if (!IsRegularFile(path) ||
            !await MatchesAsync(
                path,
                expectedSize,
                expectedSha256,
                CancellationToken.None))
        {
            throw new ManagedFilesInstallException(
                $"Hub-owned temporary file '{path}' was changed outside NFG Hub.");
        }

        File.Delete(path);
    }

    private static void DeleteStoreTemporaryFileIfPresent(string path)
    {
        if (Directory.Exists(path))
        {
            throw new ManagedFilesInstallException(
                $"Hub-owned temporary path '{path}' is occupied by a directory.");
        }

        if (!File.Exists(path))
        {
            return;
        }

        if (!IsRegularFile(path))
        {
            throw new ManagedFilesInstallException(
                $"Hub-owned temporary file '{path}' is a reparse point.");
        }

        File.Delete(path);
    }

    private static void EnsurePathIsVacant(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new ManagedFilesInstallException(
                $"Update path '{path}' is already occupied.");
        }
    }

    private static string GetStateFilePath(StateFilePlan plan, bool isEnabled) =>
        isEnabled ? plan.TargetPath : plan.DisabledPath;

    private static string GetPackageFilePath(FilePlan plan, bool isEnabled) =>
        isEnabled ? plan.TargetPath : GetDisabledPath(plan.TargetPath);

    private static string GetUpdateStagePath(string finalPath, string operationId) =>
        $"{finalPath}.nfg-update-stage-{operationId}.disabled";

    private static string GetUpdateBackupPath(string originalPath, string operationId) =>
        $"{originalPath}.nfg-update-old-{operationId}.disabled";

    private async Task<T> ExecuteLockedAsync<T>(
        string productId,
        CancellationToken cancellationToken,
        Func<Task<T>> operation)
    {
        return await _operationLock.ExecuteAsync(
            productId,
            cancellationToken,
            operation);
    }

    private static void ValidatePackageIdentity(ValidatedPackage package, ProductManifest product)
    {
        if (!package.Manifest.ProductId.Equals(product.Id, StringComparison.Ordinal) ||
            !package.Manifest.Version.Equals(product.Release.Version, StringComparison.Ordinal) ||
            !package.Manifest.Strategy.Equals("managed-files", StringComparison.Ordinal))
        {
            throw new ManagedFilesInstallException("Validated package identity does not match the product.");
        }
    }

    private static async Task VerifyExistingStateAsync(
        InstalledProductState state,
        ValidatedPackage package,
        ProductManifest product,
        string gameRoot,
        IReadOnlyList<FilePlan> plans,
        CancellationToken cancellationToken)
    {
        var payload = product.Release.Payload!;
        if (!state.Version.Equals(product.Release.Version, StringComparison.Ordinal) ||
            state.PackageSizeBytes != payload.SizeBytes ||
            !state.PackageSha256.Equals(payload.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(state.GameRoot).Equals(gameRoot, StringComparison.OrdinalIgnoreCase) ||
            state.Files.Count != plans.Count)
        {
            throw new ManagedFilesInstallException(
                "A different product version is already managed. Use the product update operation.");
        }

        foreach (var plan in plans)
        {
            var stateFile = state.Files.SingleOrDefault(file =>
                file.Destination.Equals(
                    plan.PackageFile.DestinationRelativePath,
                    StringComparison.OrdinalIgnoreCase));
            var expectedPath = state.IsEnabled
                ? plan.TargetPath
                : GetDisabledPath(plan.TargetPath);
            var unexpectedPath = state.IsEnabled
                ? GetDisabledPath(plan.TargetPath)
                : plan.TargetPath;
            if (stateFile is null ||
                File.Exists(unexpectedPath) ||
                Directory.Exists(unexpectedPath) ||
                !IsRegularFile(expectedPath) ||
                !await MatchesAsync(
                    expectedPath,
                    stateFile.SizeBytes,
                    stateFile.Sha256,
                    cancellationToken))
            {
                throw new ManagedFilesInstallException(
                    $"Managed file '{expectedPath}' is missing, changed, or has a conflicting copy.");
            }
        }
    }

    private static InstalledProductState CreateState(
        ValidatedPackage package,
        ProductManifest product,
        ProductPayload payload,
        string gameRoot,
        bool isEnabled = true,
        string? detectedSteamBuildId = null) => new()
        {
            SchemaVersion = 2,
            InstallationKey = ProductInstallationKey.FromManifest(product),
            ProductId = product.Id,
            Version = product.Release.Version,
            PackageSizeBytes = payload.SizeBytes,
            PackageSha256 = payload.Sha256.ToLowerInvariant(),
            SteamAppId = package.Manifest.Steam.AppId,
            SteamBuildId = package.Manifest.Steam.BuildId,
            DetectedSteamBuildId = detectedSteamBuildId,
            GameRoot = gameRoot,
            InstalledAt = DateTimeOffset.UtcNow,
            IsEnabled = isEnabled,
            Files = package.Files.Select(file => new InstalledFileState
            {
                Destination = file.DestinationRelativePath,
                SizeBytes = file.SizeBytes,
                Sha256 = file.Sha256.ToLowerInvariant()
            }).ToArray()
        };

    private static void ValidateDetectedSteamBuildId(string? detectedSteamBuildId)
    {
        if (detectedSteamBuildId is not null &&
            (detectedSteamBuildId.Length == 0 ||
             !detectedSteamBuildId.All(char.IsAsciiDigit)))
        {
            throw new ManagedFilesInstallException(
                "Detected Steam build id must contain only ASCII digits.");
        }
    }

    private static async Task ExtractVerifiedAsync(
        ZipArchiveEntry entry,
        string stagePath,
        ValidatedPackageFile packageFile,
        CancellationToken cancellationToken)
    {
        await using var source = entry.Open();
        await using var destination = new FileStream(
            stagePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        long length = 0;

        try
        {
            int count;
            while ((count = await source.ReadAsync(
                       buffer.AsMemory(0, buffer.Length),
                       cancellationToken)) != 0)
            {
                length = checked(length + count);
                if (length > packageFile.SizeBytes)
                {
                    throw new ManagedFilesInstallException(
                        $"Package entry '{entry.FullName}' exceeded its declared size.");
                }

                hash.AppendData(buffer, 0, count);
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        await destination.FlushAsync(cancellationToken);
        var actualHash = Convert.ToHexString(hash.GetHashAndReset());
        if (length != packageFile.SizeBytes ||
            !actualHash.Equals(packageFile.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ManagedFilesInstallException(
                $"Package entry '{entry.FullName}' failed its final integrity check.");
        }
    }

    private static async Task VerifyArchiveAsync(
        FileStream stream,
        ProductPayload payload,
        CancellationToken cancellationToken)
    {
        if (stream.Length != payload.SizeBytes)
        {
            throw new ManagedFilesInstallException("Package changed after validation.");
        }

        var digest = await SHA256.HashDataAsync(stream, cancellationToken);
        if (!Convert.ToHexString(digest).Equals(payload.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ManagedFilesInstallException("Package changed after validation.");
        }
    }

    private static StateFilePlan[] CreateStateFilePlans(InstalledProductState state)
    {
        var root = Path.GetFullPath(state.GameRoot);
        return state.Files
            .Select(file => new StateFilePlan(file, ResolveTarget(root, file.Destination)))
            .ToArray();
    }

    private static async Task<string?> LocateManagedFileAsync(
        StateFilePlan plan,
        bool requireFile,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(plan.TargetPath) || Directory.Exists(plan.DisabledPath))
        {
            throw new ManagedFilesInstallException(
                $"Managed file location for '{plan.TargetPath}' is occupied by a directory.");
        }

        var targetExists = File.Exists(plan.TargetPath);
        var disabledExists = File.Exists(plan.DisabledPath);
        if (targetExists && disabledExists)
        {
            throw new ManagedFilesInstallException(
                $"Both active and disabled copies exist for '{plan.TargetPath}'.");
        }

        if (!targetExists && !disabledExists)
        {
            if (requireFile)
            {
                throw new ManagedFilesInstallException(
                    $"Managed file '{plan.TargetPath}' is missing.");
            }

            return null;
        }

        var existingPath = targetExists ? plan.TargetPath : plan.DisabledPath;
        if (!IsRegularFile(existingPath) ||
            !await MatchesAsync(
                existingPath,
                plan.State.SizeBytes,
                plan.State.Sha256,
                cancellationToken))
        {
            throw new ManagedFilesInstallException(
                $"Managed file '{existingPath}' was changed outside NFG Hub.");
        }

        return existingPath;
    }

    private static string GetDisabledPath(string targetPath) =>
        $"{targetPath}{DisabledFileSuffix}";

    private static bool PathsEqual(string left, string right) =>
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> MatchesAsync(
        string path,
        long expectedSize,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != expectedSize)
        {
            return false;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(digest).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRegularFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
    }

    private static void RejectReparsePoint(string path)
    {
        if (!IsRegularFile(path))
        {
            throw new ManagedFilesInstallException(
                $"Managed file '{path}' cannot be a reparse point.");
        }
    }

    private static string ResolveTarget(string gameRoot, string relativePath)
    {
        ValidateRelativePath(relativePath);
        var root = Path.GetFullPath(gameRoot);
        var target = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(root, target);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new ManagedFilesInstallException("Package target resolves outside the game directory.");
        }

        var parent = Path.GetDirectoryName(target)
            ?? throw new ManagedFilesInstallException("Package target does not have a parent directory.");
        if (!Directory.Exists(parent))
        {
            throw new ManagedFilesInstallException(
                $"Package target directory '{parent}' does not exist.");
        }

        EnsureNoReparsePoints(root, parent);
        return target;
    }

    private static void EnsureNoReparsePoints(string root, string parent)
    {
        var relativeParent = Path.GetRelativePath(root, parent);
        var current = root;
        foreach (var segment in relativeParent.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new ManagedFilesInstallException(
                    $"Package target directory '{current}' is a reparse point.");
            }
        }
    }

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Contains('\\') ||
            path.StartsWith('/') ||
            Path.IsPathRooted(path))
        {
            throw new ManagedFilesInstallException("Managed destination is not a safe relative path.");
        }

        var segments = path.Split('/');
        if (segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) ||
                segment is "." or ".." ||
                segment.EndsWith(' ') ||
                segment.EndsWith('.') ||
                IsReservedDeviceName(segment) ||
                segment.Any(character =>
                    char.IsControl(character) || character is '<' or '>' or '"' or ':' or '|' or '?' or '*')))
        {
            throw new ManagedFilesInstallException("Managed destination contains an unsafe path segment.");
        }
    }

    private static bool IsReservedDeviceName(string segment)
    {
        var name = segment.Split('.', 2)[0];
        return name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               IsNumberedDevice(name, "COM") ||
               IsNumberedDevice(name, "LPT");
    }

    private static bool IsNumberedDevice(string value, string prefix) =>
        value.Length == prefix.Length + 1 &&
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        value[^1] is >= '1' and <= '9';

    private sealed record FilePlan(ValidatedPackageFile PackageFile, string TargetPath)
    {
        public bool Adopted { get; set; }

        public string? StagePath { get; set; }
    }

    private sealed record StateFilePlan(InstalledFileState State, string TargetPath)
    {
        public string DisabledPath { get; } = GetDisabledPath(TargetPath);

        public string? ExistingPath { get; set; }
    }
}
