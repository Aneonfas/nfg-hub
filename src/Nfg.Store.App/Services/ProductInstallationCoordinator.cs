using System.IO;
using System.Net.Http;
using Nfg.Store.Contracts;
using Nfg.Store.Core;
using Nfg.Store.Installation;
using Nfg.Store.Platform.Windows;

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
                $"Installation slot '{installationKey}' is not installed through NFG Hub.");
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
                    "Поиск игры и проверка версии…"));
                var installation = SelectInstallation(
                    product,
                    currentState?.GameRoot);

                var downloadProgress = new Progress<PackageDownloadProgress>(value =>
                    progress?.Report(new ProductInstallProgress(
                        8 + value.Percentage * 0.66,
                        $"Загрузка выбранного варианта: {value.Percentage:0}%")));
                var archivePath = await _downloader.DownloadAsync(
                    product,
                    _downloadRoot,
                    downloadProgress,
                    cancellationToken);

                progress?.Report(new ProductInstallProgress(
                    76,
                    "Проверка пакета и контрольных сумм…"));
                var package = await _archiveService.ValidateAsync(
                    archivePath,
                    product,
                    cancellationToken);

                progress?.Report(new ProductInstallProgress(
                    88,
                    "Повторная проверка версии игры…"));
                installation = RequireInstallationAtRoot(
                    product,
                    installation.GameRoot);

                progress?.Report(new ProductInstallProgress(
                    92,
                    "Безопасное применение выбранного варианта…"));
                var result = await _installer.ApplyVariantAsync(
                    package,
                    product,
                    installation.GameRoot,
                    expectedProductId,
                    installation.BuildId,
                    cancellationToken);

                progress?.Report(new ProductInstallProgress(
                    100,
                    "Выбранный вариант применён."));
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
                ? "Фактическое состояние не удалось безопасно прочитать; persisted evidence сохранён."
                : outcome.State is null
                    ? "Фактическое состояние: слот не установлен."
                    : $"Фактическое состояние: {outcome.State.ProductId} " +
                      $"{outcome.State.Version}, " +
                      (outcome.State.IsEnabled ? "включён." : "выключен.");
            throw new ProductInstallationException(
                $"{exception.Message} {actualOutcome}",
                exception,
                outcome.State,
                actualStateUnreadable: !outcome.IsReadable);
        }
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
                $"Игра Steam App ID {appId} не найдена.");
        }

        if (!string.IsNullOrWhiteSpace(preferredGameRoot))
        {
            return candidates.FirstOrDefault(candidate =>
                       PathsEqual(candidate.GameRoot, preferredGameRoot))
                   ?? throw new ProductInstallationException(
                       $"Управляемая установка игры '{Path.GetFullPath(preferredGameRoot)}' не найдена в Steam.");
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
            $"Продукт '{product.Id}' должен объявлять один Steam App ID.");

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
