using System.IO;
using System.Net.Http;
using Nfg.Store.Contracts;
using Nfg.Store.Core;
using Nfg.Store.Installation;
using Nfg.Store.Platform.Windows;
using Nfg.Store.App.Localization;

namespace Nfg.Store.App.Services;

public sealed class ProductInstallationCoordinator(
    HttpClient httpClient,
    string dataRoot,
    InstallationStateStore stateStore,
    SteamGameLocator? steamLocator = null)
{
    private readonly PackageDownloader _downloader = new(httpClient);
    private readonly PackageArchiveService _archiveService = new();
    private readonly ManagedFilesInstaller _installer = new(stateStore);
    private readonly SteamGameLocator _steamLocator = steamLocator ?? new SteamGameLocator();
    private readonly string _downloadRoot = Path.Combine(dataRoot, "downloads");

    public Task<ManagedInstallResult> InstallAsync(
        ProductManifest product,
        IProgress<ProductInstallProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ApplyVariantAsync(
            product,
            expectedProductId: null,
            progress,
            cancellationToken);

    public Task<ManagedInstallResult> UpdateAsync(
        ProductManifest product,
        IProgress<ProductInstallProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        SwitchVersionAsync(product, progress, cancellationToken);

    public async Task<ManagedInstallResult> SwitchVersionAsync(
        ProductManifest product,
        IProgress<ProductInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        var installationKey = ProductInstallationKey.FromManifest(product);
        var installedState = await stateStore.LoadAsync(installationKey, cancellationToken)
            ?? throw new ProductInstallationException(
                LocalizationService.Instance.Format(
                    "Install.SlotNotInstalled",
                    installationKey));
        return await ApplyVariantAsync(
            product,
            installedState.ProductId,
            progress,
            cancellationToken);
    }

    public async Task<ManagedInstallResult> ApplyVariantAsync(
        ProductManifest product,
        string? expectedProductId,
        IProgress<ProductInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        var installationKey = ProductInstallationKey.FromManifest(product);

        return await ExecuteReportingPersistedOutcomeAsync(
            installationKey,
            async () =>
            {
                var currentState = await stateStore.LoadAsync(
                    installationKey,
                    cancellationToken);
                progress?.Report(new ProductInstallProgress(
                    0,
                    LocalizationService.Instance.Get("Install.FindGame")));
                var installation = SelectInstallation(
                    product,
                    currentState?.GameRoot);

                var downloadProgress = new Progress<PackageDownloadProgress>(value =>
                    progress?.Report(new ProductInstallProgress(
                        8 + value.Percentage * 0.66,
                        LocalizationService.Instance.Format("Install.Download", value.Percentage))));
                var archivePath = await _downloader.DownloadAsync(
                    product,
                    _downloadRoot,
                    downloadProgress,
                    cancellationToken);

                progress?.Report(new ProductInstallProgress(
                    76,
                    LocalizationService.Instance.Get("Install.Verify")));
                var package = await _archiveService.ValidateAsync(
                    archivePath,
                    product,
                    cancellationToken);

                progress?.Report(new ProductInstallProgress(
                    88,
                    LocalizationService.Instance.Get("Install.Recheck")));
                installation = RequireInstallationAtRoot(
                    product,
                    installation.GameRoot);

                progress?.Report(new ProductInstallProgress(
                    92,
                    LocalizationService.Instance.Get("Install.Apply")));
                var result = expectedProductId is null
                    ? await _installer.InstallAsync(
                        package,
                        product,
                        installation.GameRoot,
                        installation.BuildId,
                        cancellationToken)
                    : await _installer.ApplyVariantAsync(
                        package,
                        product,
                        installation.GameRoot,
                        expectedProductId,
                        installation.BuildId,
                        cancellationToken);

                progress?.Report(new ProductInstallProgress(
                    100,
                    LocalizationService.Instance.Get("Install.Applied")));
                return result;
            });
    }

    public ProductGameInstallation? DetectGameInstallation(
        ProductManifest product,
        string? preferredGameRoot = null)
    {
        ArgumentNullException.ThrowIfNull(product);
        var appId = TryGetSteamAppId(product);
        if (appId is null)
        {
            return null;
        }

        var candidates = _steamLocator.Find(appId);
        if (candidates.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(preferredGameRoot))
        {
            var preferred = candidates.FirstOrDefault(candidate =>
                PathsEqual(candidate.GameRoot, preferredGameRoot));
            return preferred is null ? null : ToProductInstallation(preferred);
        }

        var expectedBuildId = GetExpectedSteamBuildId(product);
        var selected = candidates
            .OrderBy(candidate => candidate.BuildId.Equals(
                expectedBuildId,
                StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(candidate => candidate.GameRoot, StringComparer.OrdinalIgnoreCase)
            .First();
        return ToProductInstallation(selected);
    }

    public Task RecoverPendingOperationsAsync(
        CancellationToken cancellationToken = default) =>
        _installer.RecoverPendingOperationsAsync(cancellationToken);

    public Task<InstalledProductState?> LoadInstalledStateAsync(
        string installationKey,
        CancellationToken cancellationToken = default) =>
        stateStore.LoadAsync(installationKey, cancellationToken);

    public async Task<ManagedFamilyInventory> ReconcileFamilyAsync(
        IReadOnlyList<ProductManifest> products,
        CancellationToken cancellationToken = default)
    {
        var family = ValidateFamily(products);
        var installationKey = ProductInstallationKey.FromManifest(family[0]);
        var currentState = await stateStore.LoadAsync(installationKey, cancellationToken);
        var installation = SelectInstallation(
            family.FirstOrDefault(product => product.Id.Equals(
                currentState?.ProductId,
                StringComparison.Ordinal)) ?? family[0],
            currentState?.GameRoot);
        var packages = await LoadCachedFamilyPackagesAsync(
            family,
            cancellationToken);
        return await _installer.ReconcileFamilyAsync(
            family,
            packages,
            installation.GameRoot,
            installation.BuildId,
            cancellationToken);
    }

    public async Task<ManagedFamilyInventory> RemoveFamilyVariantAsync(
        IReadOnlyList<ProductManifest> products,
        string selectedProductId,
        ManagedVariantRemovalScope scope,
        CancellationToken cancellationToken = default)
    {
        var family = ValidateFamily(products);
        var installationKey = ProductInstallationKey.FromManifest(family[0]);
        var currentState = await stateStore.LoadAsync(installationKey, cancellationToken);
        var installation = SelectInstallation(
            family.FirstOrDefault(product => product.Id.Equals(
                currentState?.ProductId,
                StringComparison.Ordinal)) ?? family[0],
            currentState?.GameRoot);
        var packages = await LoadCachedFamilyPackagesAsync(
            family,
            cancellationToken);
        return await _installer.RemoveFamilyVariantAsync(
            family,
            packages,
            installation.GameRoot,
            selectedProductId,
            scope,
            installation.BuildId,
            cancellationToken);
    }

    public Task RemoveFromDeviceAsync(
        string productId,
        CancellationToken cancellationToken = default) =>
        RemoveFromDeviceAsync(productId, productId, cancellationToken);

    public async Task RemoveFromDeviceAsync(
        string installationKey,
        string expectedProductId,
        CancellationToken cancellationToken = default)
    {
        await ExecuteReportingPersistedOutcomeAsync(
            installationKey,
            async () =>
            {
                await _installer.UninstallAsync(
                    installationKey,
                    expectedProductId,
                    cancellationToken);
                return true;
            });
    }

    public Task<InstalledProductState> SetEnabledAsync(
        string productId,
        bool isEnabled,
        CancellationToken cancellationToken = default) =>
        SetEnabledAsync(productId, productId, isEnabled, cancellationToken);

    public Task<InstalledProductState> SetEnabledAsync(
        string installationKey,
        string expectedProductId,
        bool isEnabled,
        CancellationToken cancellationToken = default) =>
        ExecuteReportingPersistedOutcomeAsync(
            installationKey,
            () => _installer.SetEnabledAsync(
                installationKey,
                expectedProductId,
                isEnabled,
                cancellationToken));

    private async Task<T> ExecuteReportingPersistedOutcomeAsync<T>(
        string installationKey,
        Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (OperationCanceledException)
        {
            _ = await ReadActualOutcomeAsync(installationKey);
            throw;
        }
        catch (Exception exception)
        {
            var outcome = await ReadActualOutcomeAsync(installationKey);
            var actualOutcome = !outcome.IsReadable
                ? LocalizationService.Instance.Get("Install.ActualReadFailed")
                : outcome.State is null
                    ? LocalizationService.Instance.Get("Install.ActualNotInstalled")
                    : LocalizationService.Instance.Format(
                        "Install.ActualInstalled",
                        outcome.State.ProductId,
                        outcome.State.Version,
                        outcome.State.IsEnabled
                            ? LocalizationService.Instance.Get("State.ActualEnabled")
                            : LocalizationService.Instance.Get("State.ActualDisabled"));
            var message = exception is ActiveLocalizationConflictException conflict
                ? LocalizationService.Instance.Format(
                    "State.LocalizationConflictDetails",
                    string.Join(Environment.NewLine, conflict.Paths))
                : exception.Message;
            throw new ProductInstallationException(
                $"{message} {actualOutcome}",
                exception,
                outcome.State,
                actualStateUnreadable: !outcome.IsReadable);
        }
    }

    private async Task<IReadOnlyList<ManagedVariantPackage>> LoadCachedFamilyPackagesAsync(
        IReadOnlyList<ProductManifest> products,
        CancellationToken cancellationToken)
    {
        var packages = new List<ManagedVariantPackage>();
        foreach (var product in products)
        {
            // Inventory and removal must not depend on another locale's download.
            // Cached history can identify a manual old release even without Hub state.
            foreach (var release in ProductReleaseCatalog.GetAvailableReleases(product)
                         .Where(ProductReleaseCatalog.IsPublished))
            {
                var candidate = ProductReleaseCatalog.CreateManifestForRelease(product, release);
                try
                {
                    var archivePath = await _downloader.TryGetCachedAsync(
                        candidate,
                        _downloadRoot,
                        cancellationToken);
                    if (archivePath is null)
                    {
                        continue;
                    }

                    var package = await _archiveService.ValidateAsync(
                        archivePath,
                        candidate,
                        cancellationToken);
                    packages.Add(new ManagedVariantPackage(candidate, package));
                }
                catch (Exception exception) when (
                    exception is PackageValidationException or IOException or UnauthorizedAccessException)
                {
                    // Cache is optional evidence. Stored, hash-guarded installation
                    // state remains usable when a cached ZIP is corrupt or unavailable.
                }
            }
        }

        return packages;
    }

    private static IReadOnlyList<ProductManifest> ValidateFamily(
        IReadOnlyList<ProductManifest> products)
    {
        ArgumentNullException.ThrowIfNull(products);
        if (products.Count == 0)
        {
            throw new ArgumentException(
                "A product family must contain at least one product.",
                nameof(products));
        }

        var installationKey = ProductInstallationKey.FromManifest(products[0]);
        if (products.Any(product =>
                !ProductInstallationKey.FromManifest(product).Equals(
                    installationKey,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new ProductInstallationException(
                "Product family members do not share one installation slot.");
        }

        return products;
    }

    private async Task<PersistedOutcome> ReadActualOutcomeAsync(
        string installationKey)
    {
        try
        {
            return new PersistedOutcome(
                IsReadable: true,
                await stateStore.LoadAsync(
                    installationKey,
                    CancellationToken.None));
        }
        catch
        {
            return new PersistedOutcome(IsReadable: false, State: null);
        }
    }

    private SteamGameInstallation SelectInstallation(
        ProductManifest product,
        string? preferredGameRoot = null)
    {
        var appId = GetSteamAppId(product);
        var candidates = _steamLocator.Find(appId);
        if (candidates.Count == 0)
        {
            throw new ProductInstallationException(
                LocalizationService.Instance.Format("Install.GameNotFound", appId));
        }

        if (!string.IsNullOrWhiteSpace(preferredGameRoot))
        {
            return candidates.FirstOrDefault(candidate =>
                       PathsEqual(candidate.GameRoot, preferredGameRoot))
                   ?? throw new ProductInstallationException(
                       LocalizationService.Instance.Format(
                           "Install.ManagedGameNotFound",
                           Path.GetFullPath(preferredGameRoot)));
        }

        var expectedBuildId = GetExpectedSteamBuildId(product);
        return candidates
            .OrderBy(candidate => candidate.BuildId.Equals(
                expectedBuildId,
                StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(candidate => candidate.GameRoot, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private SteamGameInstallation RequireInstallationAtRoot(
        ProductManifest product,
        string gameRoot) =>
        SelectInstallation(product, gameRoot);

    private static string GetSteamAppId(ProductManifest product) =>
        TryGetSteamAppId(product)
        ?? throw new ProductInstallationException(
            LocalizationService.Instance.Format("Install.MissingSteamAppId", product.Id));

    private static string? TryGetSteamAppId(ProductManifest product)
    {
        var appIds = product.Installation.Detection
            .Where(rule => rule is not null &&
                           string.Equals(
                               rule.Provider,
                               "steam",
                               StringComparison.OrdinalIgnoreCase))
            .Select(rule => rule.ProductId)
            .Where(value =>
                !string.IsNullOrWhiteSpace(value) &&
                value.All(char.IsAsciiDigit))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return appIds.Length == 1 ? appIds[0] : null;
    }

    private static string? GetExpectedSteamBuildId(ProductManifest product)
    {
        var gameVersion = product.Compatibility.GameVersion;
        return gameVersion is not null &&
               gameVersion.StartsWith(
                   ProductReleaseCatalog.SteamBuildPrefix,
                   StringComparison.Ordinal)
            ? gameVersion[ProductReleaseCatalog.SteamBuildPrefix.Length..]
            : null;
    }

    private static ProductGameInstallation ToProductInstallation(
        SteamGameInstallation installation) => new(
        installation.AppId,
        installation.BuildId,
        installation.TargetBuildId,
        installation.GameRoot,
        installation.AppManifestPath);

    private static bool PathsEqual(string left, string right) =>
        Path.GetFullPath(left).Equals(
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    private sealed record PersistedOutcome(
        bool IsReadable,
        InstalledProductState? State);
}
