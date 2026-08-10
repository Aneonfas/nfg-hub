using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Nfg.Store.Contracts;
using Nfg.Store.Core;

namespace Nfg.Store.Installation;

public sealed class ManagedFilesInstaller
{
    private const string DisabledFileSuffix = ".nfg-disabled";

    private readonly IInstallationStateStore _stateStore;
    private readonly ManagedUpdateJournalStore _journalStore;
    private readonly InstallationOperationLock _operationLock;

    public ManagedFilesInstaller(IInstallationStateStore stateStore)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        _stateStore = stateStore;
        _journalStore = new ManagedUpdateJournalStore(stateStore);
        _operationLock = new InstallationOperationLock(stateStore.DataRoot);
    }

    internal Func<ManagedInstallerPhase, Task>? PhaseObserver { get; init; }

    public Task<ManagedInstallResult> InstallAsync(
        ValidatedPackage package,
        ProductManifest product,
        string gameRoot,
        CancellationToken cancellationToken = default) =>
        InstallAsync(
            package,
            product,
            gameRoot,
            detectedSteamBuildId: null,
            cancellationToken);

    public Task<ManagedInstallResult> InstallAsync(
        ValidatedPackage package,
        ProductManifest product,
        string gameRoot,
        string? detectedSteamBuildId,
        CancellationToken cancellationToken = default) =>
        ApplyVariantLockedAsync(
            package,
            product,
            gameRoot,
            expectedProductId: null,
            detectedSteamBuildId,
            enforceExpectedProduct: false,
            allowAlreadyInstalled: true,
            cancellationToken);

    public Task<ManagedInstallResult> ApplyVariantAsync(
        ValidatedPackage package,
        ProductManifest product,
        string gameRoot,
        string? expectedProductId,
        CancellationToken cancellationToken = default) =>
        ApplyVariantAsync(
            package,
            product,
            gameRoot,
            expectedProductId,
            detectedSteamBuildId: null,
            cancellationToken);

    public Task<ManagedInstallResult> ApplyVariantAsync(
        ValidatedPackage package,
        ProductManifest product,
        string gameRoot,
        string? expectedProductId,
        string? detectedSteamBuildId,
        CancellationToken cancellationToken = default) =>
        ApplyVariantLockedAsync(
            package,
            product,
            gameRoot,
            expectedProductId,
            detectedSteamBuildId,
            enforceExpectedProduct: true,
            allowAlreadyInstalled: false,
            cancellationToken);

    public Task<ManagedInstallResult> UpdateAsync(
        ValidatedPackage package,
        ProductManifest product,
        string gameRoot,
        CancellationToken cancellationToken = default) =>
        SwitchVersionAsync(package, product, gameRoot, cancellationToken);

    public Task<ManagedInstallResult> UpdateAsync(
        ValidatedPackage package,
        ProductManifest product,
        string gameRoot,
        string? detectedSteamBuildId,
        CancellationToken cancellationToken = default) =>
        SwitchVersionAsync(
            package,
            product,
            gameRoot,
            detectedSteamBuildId,
            cancellationToken);

    public Task<ManagedInstallResult> SwitchVersionAsync(
        ValidatedPackage package,
        ProductManifest product,
        string gameRoot,
        CancellationToken cancellationToken = default) =>
        SwitchVersionAsync(
            package,
            product,
            gameRoot,
            detectedSteamBuildId: null,
            cancellationToken);

    public Task<ManagedInstallResult> SwitchVersionAsync(
        ValidatedPackage package,
        ProductManifest product,
        string gameRoot,
        string? detectedSteamBuildId,
        CancellationToken cancellationToken = default) =>
        ApplyVariantLockedAsync(
            package,
            product,
            gameRoot,
            expectedProductId: product.Id,
            detectedSteamBuildId,
            enforceExpectedProduct: true,
            allowAlreadyInstalled: false,
            cancellationToken);

    public Task<InstalledProductState> SetEnabledAsync(
        string productId,
        bool isEnabled,
        CancellationToken cancellationToken = default) =>
        SetEnabledAsync(productId, productId, isEnabled, cancellationToken);

    public async Task<InstalledProductState> SetEnabledAsync(
        string installationKey,
        string expectedProductId,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        return await _operationLock.ExecuteAsync(
            installationKey,
            cancellationToken,
            async () =>
            {
                await ObserveAsync(ManagedInstallerPhase.LockAcquired);
                await RecoverPendingOperationCoreAsync(installationKey);
                return await SetEnabledCoreAsync(
                    installationKey,
                    expectedProductId,
                    isEnabled,
                    cancellationToken);
            });
    }

    public Task UninstallAsync(
        string productId,
        CancellationToken cancellationToken = default) =>
        UninstallAsync(productId, productId, cancellationToken);

    public async Task UninstallAsync(
        string installationKey,
        string expectedProductId,
        CancellationToken cancellationToken = default)
    {
        await _operationLock.ExecuteAsync(
            installationKey,
            cancellationToken,
            async () =>
            {
                await ObserveAsync(ManagedInstallerPhase.LockAcquired);
                await RecoverPendingOperationCoreAsync(installationKey);
                await UninstallCoreAsync(
                    installationKey,
                    expectedProductId,
                    cancellationToken);
                return true;
            });
    }

    public async Task RecoverPendingOperationsAsync(
        CancellationToken cancellationToken = default)
    {
        var journals = await _journalStore.LoadAllAsync(cancellationToken);
        foreach (var installationKey in journals
                     .Select(journal => journal.EffectiveInstallationKey)
                     .Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _operationLock.ExecuteAsync(
                installationKey,
                cancellationToken,
                async () =>
                {
                    await RecoverPendingOperationCoreAsync(installationKey);
                    return true;
                });
        }
    }

    private async Task<ManagedInstallResult> ApplyVariantLockedAsync(
        ValidatedPackage package,
        ProductManifest product,
        string gameRoot,
        string? expectedProductId,
        string? detectedSteamBuildId,
        bool enforceExpectedProduct,
        bool allowAlreadyInstalled,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(product);
        var installationKey = ProductInstallationKey.FromManifest(product);
        return await _operationLock.ExecuteAsync(
            installationKey,
            cancellationToken,
            async () =>
            {
                await ObserveAsync(ManagedInstallerPhase.LockAcquired);
                await RecoverPendingOperationCoreAsync(installationKey);
                return await ApplyVariantCoreAsync(
                    package,
                    product,
                    installationKey,
                    gameRoot,
                    expectedProductId,
                    detectedSteamBuildId,
                    enforceExpectedProduct,
                    allowAlreadyInstalled,
                    cancellationToken);
            });
    }

    private async Task<ManagedInstallResult> ApplyVariantCoreAsync(
        ValidatedPackage package,
        ProductManifest product,
        string installationKey,
        string gameRoot,
        string? expectedProductId,
        string? detectedSteamBuildId,
        bool enforceExpectedProduct,
        bool allowAlreadyInstalled,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        ValidateDetectedSteamBuildId(detectedSteamBuildId);
        var payload = product.Release.Payload
            ?? throw new ManagedFilesInstallException(
                "The product does not declare a package payload.");
        ValidatePackageIdentity(package, product);

        var root = Path.GetFullPath(gameRoot);
        if (!Directory.Exists(root))
        {
            throw new ManagedFilesInstallException(
                $"Game directory '{root}' does not exist.");
        }

        var oldState = await _stateStore.LoadAsync(installationKey, cancellationToken);
        if (enforceExpectedProduct &&
            !string.Equals(oldState?.ProductId, expectedProductId, StringComparison.Ordinal))
        {
            if (allowAlreadyInstalled &&
                oldState is not null &&
                oldState.ProductId.Equals(product.Id, StringComparison.Ordinal))
            {
                // The compatibility InstallAsync overload remains idempotent for
                // the exact active product. Cross-product changes require the
                // guarded ApplyVariantAsync API.
            }
            else
            {
                throw new ManagedFilesInstallException(
                    GetExpectedProductError(installationKey, expectedProductId, oldState?.ProductId));
            }
        }

        if (!enforceExpectedProduct &&
            oldState is not null &&
            !oldState.ProductId.Equals(product.Id, StringComparison.Ordinal))
        {
            throw new ManagedFilesInstallException(
                "Cross-product changes require ApplyVariantAsync with the expected active product.");
        }

        if (oldState is not null)
        {
            if (oldState.SchemaVersion != 2 ||
                !string.Equals(
                    oldState.InstallationKey,
                    installationKey,
                    StringComparison.Ordinal))
            {
                throw new ManagedFilesInstallException(
                    $"Installation slot '{installationKey}' must be migrated before mutation.");
            }

            if (!PathsEqual(oldState.GameRoot, root))
            {
                throw new ManagedFilesInstallException(
                    "The managed installation is located in a different game directory.");
            }

            if (!oldState.SteamAppId.Equals(
                    package.Manifest.Steam.AppId,
                    StringComparison.Ordinal))
            {
                throw new ManagedFilesInstallException(
                    "The selected variant targets a different Steam application.");
            }

            if (oldState.ProductId.Equals(product.Id, StringComparison.Ordinal) &&
                oldState.Version.Equals(product.Release.Version, StringComparison.Ordinal))
            {
                var samePlans = CreatePackageFilePlans(package, root);
                await VerifyExistingStateAsync(
                    oldState,
                    package,
                    product,
                    installationKey,
                    root,
                    samePlans,
                    cancellationToken);
                if (allowAlreadyInstalled)
                {
                    return new ManagedInstallResult(
                        ManagedInstallOutcome.AlreadyInstalled,
                        oldState);
                }

                throw new ManagedFilesInstallException(
                    $"Product '{product.Id}' version '{product.Release.Version}' is already installed.");
            }

            if (oldState.ProductId.Equals(product.Id, StringComparison.Ordinal) &&
                !SemanticVersionComparer.TryCompare(
                    product.Release.Version,
                    oldState.Version,
                    out _))
            {
                throw new ManagedFilesInstallException(
                    "A same-product transition requires valid semantic versions.");
            }
        }

        var oldPlans = oldState is null
            ? []
            : CreateStateFilePlans(oldState);
        foreach (var oldPlan in oldPlans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            oldPlan.ExistingPath = await LocateManagedFileAsync(
                oldPlan,
                requireFile: true,
                cancellationToken);
            var expectedPath = GetStateFilePath(oldPlan, oldState!.IsEnabled);
            if (!PathsEqual(oldPlan.ExistingPath!, expectedPath))
            {
                throw new ManagedFilesInstallException(
                    $"Managed file '{oldPlan.ExistingPath}' does not match the saved slot state.");
            }
        }

        var newPlans = CreatePackageFilePlans(package, root);
        var preservedDestinations = oldState is null
            ? await FindAdoptedDestinationsAsync(newPlans, cancellationToken)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newState = CreateState(
            package,
            product,
            payload,
            installationKey,
            root,
            oldState?.IsEnabled ?? true,
            detectedSteamBuildId ?? oldState?.DetectedSteamBuildId);
        var unchangedDestinations = GetUnchangedDestinations(oldState, newState);
        var retainedDestinations = unchangedDestinations
            .Concat(preservedDestinations)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var operationId = Guid.NewGuid().ToString("N");

        ValidateOperationPaths(
            oldState,
            newState,
            oldPlans,
            newPlans,
            unchangedDestinations,
            retainedDestinations,
            operationId);

        var journal = new ManagedUpdateJournal
        {
            SchemaVersion = 2,
            OperationId = operationId,
            InstallationKey = installationKey,
            ProductId = null,
            OldState = oldState,
            NewState = newState,
            PreservedDestinations = preservedDestinations
                .Order(StringComparer.Ordinal)
                .ToArray()
        };
        await _journalStore.SaveAsync(journal, cancellationToken);

        try
        {
            await ObserveAsync(ManagedInstallerPhase.JournalDurable);
            await StagePackageFilesAsync(
                package,
                payload,
                newState,
                newPlans,
                retainedDestinations,
                operationId,
                cancellationToken);
            await ObserveAsync(ManagedInstallerPhase.StagingComplete);

            foreach (var oldPlan in oldPlans.Where(plan =>
                         !unchangedDestinations.Contains(plan.State.Destination)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var originalPath = GetStateFilePath(oldPlan, oldState!.IsEnabled);
                if (!IsRegularFile(originalPath) ||
                    !await MatchesAsync(
                        originalPath,
                        oldPlan.State.SizeBytes,
                        oldPlan.State.Sha256,
                        cancellationToken))
                {
                    throw new ManagedFilesInstallException(
                        $"Managed file '{originalPath}' changed before replacement.");
                }

                File.Move(
                    originalPath,
                    GetUpdateBackupPath(originalPath, operationId));
            }

            foreach (var newPlan in newPlans.Where(plan =>
                         !retainedDestinations.Contains(
                             plan.PackageFile.DestinationRelativePath)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var finalPath = GetPackageFilePath(newPlan, newState.IsEnabled);
                File.Move(GetUpdateStagePath(finalPath, operationId), finalPath);
            }

            await VerifyStateFilesAsync(newState, cancellationToken);
            await ObserveAsync(ManagedInstallerPhase.ActivationComplete);
            await _stateStore.SaveAsync(newState, cancellationToken);
            await ObserveAsync(ManagedInstallerPhase.StateCommitted);
        }
        catch (Exception operationError) when (
            operationError is not ManagedInstallerInterruptionException)
        {
            try
            {
                await RecoverJournalCoreAsync(journal);
            }
            catch (Exception recoveryError)
            {
                throw new ManagedFilesInstallException(
                    "Variant application failed and automatic recovery was incomplete.",
                    new AggregateException(operationError, recoveryError));
            }

            throw;
        }

        try
        {
            await RecoverJournalCoreAsync(journal);
        }
        catch
        {
            // The state commit is authoritative. The retained journal makes
            // cleanup retryable on startup or the next slot mutation.
        }

        var outcome = oldState is null
            ? preservedDestinations.Count == newPlans.Length
                ? ManagedInstallOutcome.Adopted
                : ManagedInstallOutcome.Installed
            : ManagedInstallOutcome.Updated;
        return new ManagedInstallResult(outcome, newState);
    }

    private async Task StagePackageFilesAsync(
        ValidatedPackage package,
        ProductPayload payload,
        InstalledProductState newState,
        IReadOnlyList<FilePlan> newPlans,
        IReadOnlySet<string> retainedDestinations,
        string operationId,
        CancellationToken cancellationToken)
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
                     !retainedDestinations.Contains(
                         plan.PackageFile.DestinationRelativePath)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(newPlan.PackageFile.ArchiveEntryPath, out var entry))
            {
                throw new ManagedFilesInstallException(
                    $"Package entry '{newPlan.PackageFile.ArchiveEntryPath}' disappeared after validation.");
            }

            var finalPath = GetPackageFilePath(newPlan, newState.IsEnabled);
            var stagePath = GetUpdateStagePath(finalPath, operationId);
            var partialPath = GetUpdatePartialPath(stagePath);
            var extractingMarkerPath = GetUpdateExtractingMarkerPath(stagePath);
            var readyMarkerPath = GetUpdateReadyMarkerPath(stagePath);
            EnsurePathIsVacant(partialPath);
            EnsurePathIsVacant(extractingMarkerPath);
            EnsurePathIsVacant(readyMarkerPath);
            await using (var partialStream = new FileStream(
                             partialPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await WriteStageMarkerAsync(
                    extractingMarkerPath,
                    operationId,
                    newPlan.PackageFile.DestinationRelativePath,
                    newPlan.PackageFile.SizeBytes,
                    newPlan.PackageFile.Sha256,
                    "extracting",
                    CancellationToken.None);
                await ExtractVerifiedAsync(
                    entry,
                    partialStream,
                    newPlan.PackageFile,
                    cancellationToken);
            }

            File.Move(partialPath, stagePath);
            await WriteStageMarkerAsync(
                readyMarkerPath,
                operationId,
                newPlan.PackageFile.DestinationRelativePath,
                newPlan.PackageFile.SizeBytes,
                newPlan.PackageFile.Sha256,
                "ready",
                CancellationToken.None);
            await DeleteVerifiedStageMarkerAsync(
                extractingMarkerPath,
                operationId,
                newPlan.PackageFile.DestinationRelativePath,
                newPlan.PackageFile.SizeBytes,
                newPlan.PackageFile.Sha256,
                "extracting");
        }
    }

    private async Task<HashSet<string>> FindAdoptedDestinationsAsync(
        IReadOnlyList<FilePlan> plans,
        CancellationToken cancellationToken)
    {
        var adopted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var disabledPath = GetDisabledPath(plan.TargetPath);
            if (Directory.Exists(plan.TargetPath) || Directory.Exists(disabledPath))
            {
                throw new ManagedFilesInstallException(
                    $"Package target '{plan.TargetPath}' is occupied by a directory.");
            }

            if (File.Exists(disabledPath))
            {
                throw new ManagedFilesInstallException(
                    $"Disabled package target '{disabledPath}' is already occupied.");
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

            adopted.Add(plan.PackageFile.DestinationRelativePath);
        }

        return adopted;
    }

    private async Task<InstalledProductState> SetEnabledCoreAsync(
        string installationKey,
        string expectedProductId,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        var state = await _stateStore.LoadAsync(installationKey, cancellationToken)
            ?? throw new ManagedFilesInstallException(
                $"Installation slot '{installationKey}' is not installed through NFG Hub.");
        EnsureExpectedProduct(state, installationKey, expectedProductId);
        var plans = CreateStateFilePlans(state);

        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            plan.ExistingPath = await LocateManagedFileAsync(
                plan,
                requireFile: true,
                cancellationToken);
        }

        if (state.IsEnabled == isEnabled)
        {
            await VerifyStateFilesAsync(state, cancellationToken);
            return state;
        }

        var moved = new List<(StateFilePlan Plan, string Source, string Destination)>();
        try
        {
            foreach (var plan in plans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = isEnabled ? plan.TargetPath : plan.DisabledPath;
                if (!IsRegularFile(plan.ExistingPath!) ||
                    !await MatchesAsync(
                        plan.ExistingPath!,
                        plan.State.SizeBytes,
                        plan.State.Sha256,
                        cancellationToken))
                {
                    throw new ManagedFilesInstallException(
                        $"Managed file '{plan.ExistingPath}' changed before activation update.");
                }

                EnsurePathIsVacant(destination);
                File.Move(plan.ExistingPath!, destination);
                moved.Add((plan, plan.ExistingPath!, destination));
            }

            var updatedState = state with { IsEnabled = isEnabled };
            await _stateStore.SaveAsync(updatedState, cancellationToken);
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

    private async Task UninstallCoreAsync(
        string installationKey,
        string expectedProductId,
        CancellationToken cancellationToken)
    {
        var state = await _stateStore.LoadAsync(installationKey, cancellationToken);
        if (state is null)
        {
            return;
        }

        EnsureExpectedProduct(state, installationKey, expectedProductId);
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
                EnsurePathIsVacant(tombstone);
                File.Move(target.ExistingPath!, tombstone);
                moved.Add((target.ExistingPath!, tombstone));
            }

            _stateStore.Delete(installationKey);
        }
        catch
        {
            foreach (var (source, tombstone) in moved.AsEnumerable().Reverse())
            {
                if (File.Exists(tombstone) &&
                    !File.Exists(source) &&
                    !Directory.Exists(source))
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

    private async Task RecoverPendingOperationCoreAsync(string installationKey)
    {
        var journal = await _journalStore.LoadAsync(
            installationKey,
            CancellationToken.None);
        if (journal is not null)
        {
            await RecoverJournalCoreAsync(journal);
        }
    }

    private async Task RecoverJournalCoreAsync(ManagedUpdateJournal journal)
    {
        var installationKey = journal.EffectiveInstallationKey;
        var currentState = await _stateStore.LoadAsync(
            installationKey,
            CancellationToken.None);
        if (StatesEqual(currentState, journal.NewState))
        {
            await FinalizeCommittedOperationAsync(journal);
            return;
        }

        if (!StatesEqual(currentState, journal.OldState))
        {
            throw new ManagedFilesInstallException(
                $"Recovery for '{installationKey}' cannot identify the committed installation state.");
        }

        await RollBackUncommittedOperationAsync(journal);
    }

    private async Task FinalizeCommittedOperationAsync(ManagedUpdateJournal journal)
    {
        await VerifyStateFilesAsync(journal.NewState, CancellationToken.None);
        var unchanged = GetUnchangedDestinations(journal.OldState, journal.NewState);
        var retained = unchanged
            .Concat(journal.PreservedDestinations)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var newPlan in CreateStateFilePlans(journal.NewState).Where(plan =>
                     !retained.Contains(plan.State.Destination)))
        {
            var finalPath = GetStateFilePath(newPlan, journal.NewState.IsEnabled);
            var stagePath = GetUpdateStagePath(finalPath, journal.OperationId);
            await FinalizeStagingEvidenceAsync(journal, newPlan, stagePath);
        }

        if (journal.OldState is not null)
        {
            foreach (var oldPlan in CreateStateFilePlans(journal.OldState).Where(plan =>
                         !unchanged.Contains(plan.State.Destination)))
            {
                var originalPath = GetStateFilePath(oldPlan, journal.OldState.IsEnabled);
                await DeleteVerifiedOwnedFileIfPresentAsync(
                    GetUpdateBackupPath(originalPath, journal.OperationId),
                    oldPlan.State.SizeBytes,
                    oldPlan.State.Sha256);
            }
        }

        _journalStore.Delete(journal.EffectiveInstallationKey);
    }

    private static async Task FinalizeStagingEvidenceAsync(
        ManagedUpdateJournal journal,
        StateFilePlan newPlan,
        string stagePath)
    {
        if (journal.SchemaVersion == 1)
        {
            DeleteLegacyStageFileIfPresent(stagePath);
            return;
        }

        var partialPath = GetUpdatePartialPath(stagePath);
        var extractingMarkerPath = GetUpdateExtractingMarkerPath(stagePath);
        var readyMarkerPath = GetUpdateReadyMarkerPath(stagePath);
        EnsureTemporaryPathIsNotDirectory(stagePath);
        EnsureTemporaryPathIsNotDirectory(partialPath);
        EnsureTemporaryPathIsNotDirectory(extractingMarkerPath);
        EnsureTemporaryPathIsNotDirectory(readyMarkerPath);

        var hasExtractingMarker = File.Exists(extractingMarkerPath);
        var hasReadyMarker = File.Exists(readyMarkerPath);
        if (hasExtractingMarker)
        {
            await VerifyStageMarkerAsync(
                extractingMarkerPath,
                journal.OperationId,
                newPlan.State.Destination,
                newPlan.State.SizeBytes,
                newPlan.State.Sha256,
                "extracting");
        }

        if (hasReadyMarker)
        {
            await VerifyStageMarkerAsync(
                readyMarkerPath,
                journal.OperationId,
                newPlan.State.Destination,
                newPlan.State.SizeBytes,
                newPlan.State.Sha256,
                "ready");
        }

        if (File.Exists(stagePath) ||
            File.Exists(partialPath) ||
            hasExtractingMarker)
        {
            throw new ManagedFilesInstallException(
                $"Committed recovery found ambiguous staging evidence for '{newPlan.State.Destination}'.");
        }

        if (hasReadyMarker)
        {
            await DeleteVerifiedStageMarkerAsync(
                readyMarkerPath,
                journal.OperationId,
                newPlan.State.Destination,
                newPlan.State.SizeBytes,
                newPlan.State.Sha256,
                "ready");
        }
    }

    private static async Task<RollbackStagingEvidence> RollBackStagingEvidenceAsync(
        ManagedUpdateJournal journal,
        StateFilePlan newPlan,
        string stagePath)
    {
        if (journal.SchemaVersion == 1)
        {
            EnsureTemporaryPathIsNotDirectory(stagePath);
            var notActivatedMarkerPath = GetUpdateNotActivatedMarkerPath(stagePath);
            EnsureTemporaryPathIsNotDirectory(notActivatedMarkerPath);
            var hasNotActivatedMarker = File.Exists(notActivatedMarkerPath);
            if (hasNotActivatedMarker)
            {
                await VerifyStageMarkerAsync(
                    notActivatedMarkerPath,
                    journal.OperationId,
                    newPlan.State.Destination,
                    newPlan.State.SizeBytes,
                    newPlan.State.Sha256,
                    "not-activated");
            }

            if (File.Exists(stagePath) && !hasNotActivatedMarker)
            {
                await WriteStageMarkerAsync(
                    notActivatedMarkerPath,
                    journal.OperationId,
                    newPlan.State.Destination,
                    newPlan.State.SizeBytes,
                    newPlan.State.Sha256,
                    "not-activated",
                    CancellationToken.None);
                hasNotActivatedMarker = true;
            }

            if (File.Exists(stagePath))
            {
                DeleteLegacyStageFileIfPresent(stagePath);
            }

            return new RollbackStagingEvidence(
                MayHaveActivatedFinal: !hasNotActivatedMarker,
                ReadyMarkerPath: null,
                DeferredNotActivatedMarkerPath:
                    hasNotActivatedMarker ? notActivatedMarkerPath : null);
        }

        var partialPath = GetUpdatePartialPath(stagePath);
        var extractingMarkerPath = GetUpdateExtractingMarkerPath(stagePath);
        var readyMarkerPath = GetUpdateReadyMarkerPath(stagePath);
        EnsureTemporaryPathIsNotDirectory(stagePath);
        EnsureTemporaryPathIsNotDirectory(partialPath);
        EnsureTemporaryPathIsNotDirectory(extractingMarkerPath);
        EnsureTemporaryPathIsNotDirectory(readyMarkerPath);

        var hasStage = File.Exists(stagePath);
        var hasPartial = File.Exists(partialPath);
        var hasExtractingMarker = File.Exists(extractingMarkerPath);
        var hasReadyMarker = File.Exists(readyMarkerPath);
        if (hasExtractingMarker)
        {
            await VerifyStageMarkerAsync(
                extractingMarkerPath,
                journal.OperationId,
                newPlan.State.Destination,
                newPlan.State.SizeBytes,
                newPlan.State.Sha256,
                "extracting");
        }

        if (hasReadyMarker)
        {
            await VerifyStageMarkerAsync(
                readyMarkerPath,
                journal.OperationId,
                newPlan.State.Destination,
                newPlan.State.SizeBytes,
                newPlan.State.Sha256,
                "ready");
        }

        if (hasPartial && (!hasExtractingMarker || hasReadyMarker || hasStage) ||
            hasStage && !hasExtractingMarker && !hasReadyMarker ||
            hasExtractingMarker && hasReadyMarker && !hasStage)
        {
            throw new ManagedFilesInstallException(
                $"Rollback cannot prove ownership of staging evidence for '{newPlan.State.Destination}'.");
        }

        if (hasPartial)
        {
            DeleteRegularTemporaryFile(partialPath);
        }

        if (hasStage)
        {
            if (hasReadyMarker && !hasExtractingMarker)
            {
                await WriteStageMarkerAsync(
                    extractingMarkerPath,
                    journal.OperationId,
                    newPlan.State.Destination,
                    newPlan.State.SizeBytes,
                    newPlan.State.Sha256,
                    "extracting",
                    CancellationToken.None);
                hasExtractingMarker = true;
            }

            if (hasReadyMarker)
            {
                await DeleteVerifiedStageMarkerAsync(
                    readyMarkerPath,
                    journal.OperationId,
                    newPlan.State.Destination,
                    newPlan.State.SizeBytes,
                    newPlan.State.Sha256,
                    "ready");
            }

            await DeleteVerifiedOwnedFileIfPresentAsync(
                stagePath,
                newPlan.State.SizeBytes,
                newPlan.State.Sha256);
            if (hasExtractingMarker)
            {
                await DeleteVerifiedStageMarkerAsync(
                    extractingMarkerPath,
                    journal.OperationId,
                    newPlan.State.Destination,
                    newPlan.State.SizeBytes,
                    newPlan.State.Sha256,
                    "extracting");
            }

            return new RollbackStagingEvidence(
                MayHaveActivatedFinal: false,
                ReadyMarkerPath: null,
                DeferredNotActivatedMarkerPath: null);
        }

        if (hasExtractingMarker)
        {
            await DeleteVerifiedStageMarkerAsync(
                extractingMarkerPath,
                journal.OperationId,
                newPlan.State.Destination,
                newPlan.State.SizeBytes,
                newPlan.State.Sha256,
                "extracting");
            return new RollbackStagingEvidence(
                MayHaveActivatedFinal: false,
                ReadyMarkerPath: null,
                DeferredNotActivatedMarkerPath: null);
        }

        return new RollbackStagingEvidence(
            MayHaveActivatedFinal: hasReadyMarker,
            ReadyMarkerPath: hasReadyMarker ? readyMarkerPath : null,
            DeferredNotActivatedMarkerPath: null);
    }

    private static async Task DeleteReadyMarkerAfterRollbackAsync(
        ManagedUpdateJournal journal,
        StateFilePlan newPlan,
        string stagePath,
        RollbackStagingEvidence evidence)
    {
        if (evidence.ReadyMarkerPath is null)
        {
            return;
        }

        if (!string.Equals(
                evidence.ReadyMarkerPath,
                GetUpdateReadyMarkerPath(stagePath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ManagedFilesInstallException("Rollback staging marker path changed unexpectedly.");
        }

        await DeleteVerifiedStageMarkerAsync(
            evidence.ReadyMarkerPath,
            journal.OperationId,
            newPlan.State.Destination,
            newPlan.State.SizeBytes,
            newPlan.State.Sha256,
            "ready");
    }

    private async Task RollBackUncommittedOperationAsync(ManagedUpdateJournal journal)
    {
        var oldState = journal.OldState;
        var unchanged = GetUnchangedDestinations(oldState, journal.NewState);
        var preserved = journal.PreservedDestinations.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var retained = unchanged
            .Concat(preserved)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var oldPlans = oldState is null ? [] : CreateStateFilePlans(oldState);
        var oldByDestination = oldPlans.ToDictionary(
            plan => plan.State.Destination,
            StringComparer.OrdinalIgnoreCase);
        var deferredNotActivatedMarkers =
            new List<(StateFilePlan Plan, string Path)>();

        foreach (var newPlan in CreateStateFilePlans(journal.NewState).Where(plan =>
                     !retained.Contains(plan.State.Destination)))
        {
            var finalPath = GetStateFilePath(newPlan, journal.NewState.IsEnabled);
            var stagePath = GetUpdateStagePath(finalPath, journal.OperationId);
            var stagingEvidence = await RollBackStagingEvidenceAsync(
                journal,
                newPlan,
                stagePath);
            if (stagingEvidence.DeferredNotActivatedMarkerPath is { } markerPath)
            {
                deferredNotActivatedMarkers.Add((newPlan, markerPath));
            }

            if (!stagingEvidence.MayHaveActivatedFinal)
            {
                continue;
            }

            if (Directory.Exists(finalPath))
            {
                throw new ManagedFilesInstallException(
                    $"Rollback found a directory at '{finalPath}'.");
            }

            if (File.Exists(finalPath) &&
                await MatchesAsync(
                    finalPath,
                    newPlan.State.SizeBytes,
                    newPlan.State.Sha256,
                    CancellationToken.None))
            {
                File.Delete(finalPath);
            }
            else if (File.Exists(finalPath) &&
                     (!oldByDestination.TryGetValue(newPlan.State.Destination, out var oldPlan) ||
                      !PathsEqual(finalPath, GetStateFilePath(oldPlan, oldState!.IsEnabled)) ||
                      File.Exists(GetUpdateBackupPath(finalPath, journal.OperationId)) ||
                      !await MatchesAsync(
                          finalPath,
                          oldPlan.State.SizeBytes,
                          oldPlan.State.Sha256,
                          CancellationToken.None)))
            {
                throw new ManagedFilesInstallException(
                    $"Rollback refused to remove an unrecognized file at '{finalPath}'.");
            }

            await DeleteReadyMarkerAfterRollbackAsync(
                journal,
                newPlan,
                stagePath,
                stagingEvidence);
        }

        if (oldState is not null)
        {
            foreach (var oldPlan in oldPlans
                         .Where(plan => !unchanged.Contains(plan.State.Destination))
                         .Reverse())
            {
                var originalPath = GetStateFilePath(oldPlan, oldState.IsEnabled);
                var backupPath = GetUpdateBackupPath(originalPath, journal.OperationId);
                if (Directory.Exists(originalPath) || Directory.Exists(backupPath))
                {
                    throw new ManagedFilesInstallException(
                        $"Rollback found a directory at a managed path for '{originalPath}'.");
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
                                $"Rollback cannot restore occupied path '{originalPath}'.");
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
                        $"Rollback cannot find the original file '{originalPath}'.");
                }
            }

            await VerifyStateFilesAsync(oldState, CancellationToken.None);
        }
        else
        {
            await VerifyPreservedFilesAsync(
                journal.NewState,
                preserved,
                CancellationToken.None);
        }

        _journalStore.Delete(journal.EffectiveInstallationKey);
        foreach (var (plan, markerPath) in deferredNotActivatedMarkers)
        {
            await DeleteVerifiedStageMarkerAsync(
                markerPath,
                journal.OperationId,
                plan.State.Destination,
                plan.State.SizeBytes,
                plan.State.Sha256,
                "not-activated");
        }
    }

    private static void ValidateOperationPaths(
        InstalledProductState? oldState,
        InstalledProductState newState,
        IReadOnlyList<StateFilePlan> oldPlans,
        IReadOnlyList<FilePlan> newPlans,
        IReadOnlySet<string> unchangedDestinations,
        IReadOnlySet<string> retainedDestinations,
        string operationId)
    {
        var oldByDestination = oldPlans.ToDictionary(
            plan => plan.State.Destination,
            StringComparer.OrdinalIgnoreCase);
        foreach (var oldPlan in oldPlans.Where(plan =>
                     !unchangedDestinations.Contains(plan.State.Destination)))
        {
            var originalPath = GetStateFilePath(oldPlan, oldState!.IsEnabled);
            EnsurePathIsVacant(GetUpdateBackupPath(originalPath, operationId));
        }

        foreach (var newPlan in newPlans.Where(plan =>
                     !retainedDestinations.Contains(
                         plan.PackageFile.DestinationRelativePath)))
        {
            var finalPath = GetPackageFilePath(newPlan, newState.IsEnabled);
            var stagePath = GetUpdateStagePath(finalPath, operationId);
            EnsurePathIsVacant(stagePath);
            EnsurePathIsVacant(GetUpdatePartialPath(stagePath));
            EnsurePathIsVacant(GetUpdateExtractingMarkerPath(stagePath));
            EnsurePathIsVacant(GetUpdateReadyMarkerPath(stagePath));
            if (oldByDestination.TryGetValue(
                    newPlan.PackageFile.DestinationRelativePath,
                    out var oldPlan))
            {
                var oldPath = GetStateFilePath(oldPlan, oldState!.IsEnabled);
                if (!PathsEqual(finalPath, oldPath))
                {
                    throw new ManagedFilesInstallException(
                        "A variant transition cannot change activation state during replacement.");
                }

                continue;
            }

            EnsurePathIsVacant(newPlan.TargetPath);
            EnsurePathIsVacant(GetDisabledPath(newPlan.TargetPath));
        }
    }

    private static HashSet<string> GetUnchangedDestinations(
        InstalledProductState? oldState,
        InstalledProductState newState)
    {
        if (oldState is null)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

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
        InstalledProductState? right) =>
        left is null
            ? right is null
            : right is not null && InstallationStateMigrator.StatesEqual(left, right);

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

    private static async Task VerifyPreservedFilesAsync(
        InstalledProductState newState,
        IReadOnlySet<string> preservedDestinations,
        CancellationToken cancellationToken)
    {
        foreach (var plan in CreateStateFilePlans(newState).Where(plan =>
                     preservedDestinations.Contains(plan.State.Destination)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedPath = GetStateFilePath(plan, newState.IsEnabled);
            if (!IsRegularFile(expectedPath) ||
                !await MatchesAsync(
                    expectedPath,
                    plan.State.SizeBytes,
                    plan.State.Sha256,
                    cancellationToken))
            {
                throw new ManagedFilesInstallException(
                    $"Adopted file '{expectedPath}' changed during rollback.");
            }
        }
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

    private static async Task WriteStageMarkerAsync(
        string path,
        string operationId,
        string destination,
        long sizeBytes,
        string sha256,
        string phase,
        CancellationToken cancellationToken)
    {
        var bytes = CreateStageMarkerBytes(
            operationId,
            destination,
            sizeBytes,
            sha256,
            phase);
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task VerifyStageMarkerAsync(
        string path,
        string operationId,
        string destination,
        long sizeBytes,
        string sha256,
        string phase)
    {
        var expected = CreateStageMarkerBytes(
            operationId,
            destination,
            sizeBytes,
            sha256,
            phase);
        EnsureTemporaryPathIsNotDirectory(path);
        if (!IsRegularFile(path) || new FileInfo(path).Length != expected.Length)
        {
            throw new ManagedFilesInstallException(
                $"Staging ownership marker '{path}' is missing or unrecognized.");
        }

        var actual = await File.ReadAllBytesAsync(path, CancellationToken.None);
        if (!actual.AsSpan().SequenceEqual(expected))
        {
            throw new ManagedFilesInstallException(
                $"Staging ownership marker '{path}' was changed outside NFG Hub.");
        }
    }

    private static async Task DeleteVerifiedStageMarkerAsync(
        string path,
        string operationId,
        string destination,
        long sizeBytes,
        string sha256,
        string phase)
    {
        await VerifyStageMarkerAsync(
            path,
            operationId,
            destination,
            sizeBytes,
            sha256,
            phase);
        File.Delete(path);
    }

    private static byte[] CreateStageMarkerBytes(
        string operationId,
        string destination,
        long sizeBytes,
        string sha256,
        string phase)
    {
        var descriptor = string.Join(
            '\0',
            "nfg-stage-owner/1",
            operationId,
            destination,
            sizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            sha256.ToLowerInvariant(),
            phase);
        return SHA256.HashData(Encoding.UTF8.GetBytes(descriptor));
    }

    private static bool DeleteLegacyStageFileIfPresent(string path)
    {
        EnsureTemporaryPathIsNotDirectory(path);
        if (!File.Exists(path))
        {
            return false;
        }

        DeleteRegularTemporaryFile(path);
        return true;
    }

    private static void DeleteRegularTemporaryFile(string path)
    {
        EnsureTemporaryPathIsNotDirectory(path);
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

    private static void EnsureTemporaryPathIsNotDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            throw new ManagedFilesInstallException(
                $"Hub-owned temporary path '{path}' is occupied by a directory.");
        }
    }

    private static void EnsureExpectedProduct(
        InstalledProductState state,
        string installationKey,
        string expectedProductId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedProductId);
        if (!state.ProductId.Equals(expectedProductId, StringComparison.Ordinal))
        {
            throw new ManagedFilesInstallException(
                GetExpectedProductError(
                    installationKey,
                    expectedProductId,
                    state.ProductId));
        }
    }

    private static string GetExpectedProductError(
        string installationKey,
        string? expectedProductId,
        string? actualProductId) =>
        $"Installation slot '{installationKey}' changed: expected active product " +
        $"'{expectedProductId ?? "<none>"}', actual '{actualProductId ?? "<none>"}'.";

    private static void ValidatePackageIdentity(
        ValidatedPackage package,
        ProductManifest product)
    {
        if (!package.Manifest.ProductId.Equals(product.Id, StringComparison.Ordinal) ||
            !package.Manifest.Version.Equals(product.Release.Version, StringComparison.Ordinal) ||
            !package.Manifest.Strategy.Equals("managed-files", StringComparison.Ordinal))
        {
            throw new ManagedFilesInstallException(
                "Validated package identity does not match the product.");
        }
    }

    private static async Task VerifyExistingStateAsync(
        InstalledProductState state,
        ValidatedPackage package,
        ProductManifest product,
        string installationKey,
        string gameRoot,
        IReadOnlyList<FilePlan> plans,
        CancellationToken cancellationToken)
    {
        var payload = product.Release.Payload!;
        if (!string.Equals(state.InstallationKey, installationKey, StringComparison.Ordinal) ||
            !state.ProductId.Equals(product.Id, StringComparison.Ordinal) ||
            !state.Version.Equals(product.Release.Version, StringComparison.Ordinal) ||
            state.PackageSizeBytes != payload.SizeBytes ||
            !state.PackageSha256.Equals(payload.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !PathsEqual(state.GameRoot, gameRoot) ||
            state.Files.Count != plans.Count)
        {
            throw new ManagedFilesInstallException(
                "A different product or version is already managed in this installation slot.");
        }

        await VerifyStateFilesAsync(state, cancellationToken);
        foreach (var plan in plans)
        {
            if (!state.Files.Any(file => file.Destination.Equals(
                    plan.PackageFile.DestinationRelativePath,
                    StringComparison.OrdinalIgnoreCase)))
            {
                throw new ManagedFilesInstallException(
                    "The package destination set differs from the installed state.");
            }
        }
    }

    private static InstalledProductState CreateState(
        ValidatedPackage package,
        ProductManifest product,
        ProductPayload payload,
        string installationKey,
        string gameRoot,
        bool isEnabled,
        string? detectedSteamBuildId) => new()
        {
            SchemaVersion = 2,
            InstallationKey = installationKey,
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

    private static FilePlan[] CreatePackageFilePlans(
        ValidatedPackage package,
        string gameRoot) =>
        package.Files
            .Select(file => new FilePlan(
                file,
                ResolveTarget(gameRoot, file.DestinationRelativePath)))
            .ToArray();

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

    private static async Task ExtractVerifiedAsync(
        ZipArchiveEntry entry,
        FileStream destination,
        ValidatedPackageFile packageFile,
        CancellationToken cancellationToken)
    {
        await using var source = entry.Open();
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
        if (!Convert.ToHexString(digest).Equals(
                payload.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ManagedFilesInstallException("Package changed after validation.");
        }
    }

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

    private async Task ObserveAsync(ManagedInstallerPhase phase)
    {
        if (PhaseObserver is not null)
        {
            await PhaseObserver(phase);
        }
    }

    private static string GetStateFilePath(StateFilePlan plan, bool isEnabled) =>
        isEnabled ? plan.TargetPath : plan.DisabledPath;

    private static string GetPackageFilePath(FilePlan plan, bool isEnabled) =>
        isEnabled ? plan.TargetPath : GetDisabledPath(plan.TargetPath);

    private static string GetDisabledPath(string targetPath) =>
        $"{targetPath}{DisabledFileSuffix}";

    private static string GetUpdateStagePath(string finalPath, string operationId) =>
        $"{finalPath}.nfg-update-stage-{operationId}.disabled";

    private static string GetUpdatePartialPath(string stagePath) =>
        $"{stagePath}.partial";

    private static string GetUpdateExtractingMarkerPath(string stagePath) =>
        $"{stagePath}.extracting-owner";

    private static string GetUpdateReadyMarkerPath(string stagePath) =>
        $"{stagePath}.ready-owner";

    private static string GetUpdateNotActivatedMarkerPath(string stagePath) =>
        $"{stagePath}.not-activated-owner";

    private static string GetUpdateBackupPath(string originalPath, string operationId) =>
        $"{originalPath}.nfg-update-old-{operationId}.disabled";

    private static void EnsurePathIsVacant(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new ManagedFilesInstallException(
                $"Operation path '{path}' is already occupied.");
        }
    }

    private static bool PathsEqual(string left, string right) =>
        Path.GetFullPath(left).Equals(
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

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
        return Convert.ToHexString(digest).Equals(
            expectedSha256,
            StringComparison.OrdinalIgnoreCase);
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
        if (relative.StartsWith("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new ManagedFilesInstallException(
                "Package target resolves outside the game directory.");
        }

        var parent = Path.GetDirectoryName(target)
            ?? throw new ManagedFilesInstallException(
                "Package target does not have a parent directory.");
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
            throw new ManagedFilesInstallException(
                "Managed destination is not a safe relative path.");
        }

        var segments = path.Split('/');
        if (segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) ||
                segment is "." or ".." ||
                segment.EndsWith(' ') ||
                segment.EndsWith('.') ||
                IsReservedDeviceName(segment) ||
                segment.Any(character =>
                    char.IsControl(character) ||
                    character is '<' or '>' or '"' or ':' or '|' or '?' or '*')))
        {
            throw new ManagedFilesInstallException(
                "Managed destination contains an unsafe path segment.");
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

    private sealed record FilePlan(
        ValidatedPackageFile PackageFile,
        string TargetPath);

    private sealed record StateFilePlan(
        InstalledFileState State,
        string TargetPath)
    {
        public string DisabledPath { get; } = GetDisabledPath(TargetPath);

        public string? ExistingPath { get; set; }
    }

    private sealed record RollbackStagingEvidence(
        bool MayHaveActivatedFinal,
        string? ReadyMarkerPath,
        string? DeferredNotActivatedMarkerPath);
}

internal enum ManagedInstallerPhase
{
    LockAcquired,
    JournalDurable,
    StagingComplete,
    ActivationComplete,
    StateCommitted
}

internal sealed class ManagedInstallerInterruptionException(string message)
    : Exception(message);
