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

    public async Task<ManagedInstallResult> InstallAsync(
        ProductManifest product,
        IProgress<ProductInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        progress?.Report(new ProductInstallProgress(0, "Поиск игры и проверка версии…"));
        var installation = SelectInstallation(product);

        var downloadProgress = new Progress<PackageDownloadProgress>(value =>
            progress?.Report(new ProductInstallProgress(
                8 + value.Percentage * 0.66,
                $"Загрузка пакета: {value.Percentage:0}%")));
        var archivePath = await _downloader.DownloadAsync(
            product,
            _downloadRoot,
            downloadProgress,
            cancellationToken);

        progress?.Report(new ProductInstallProgress(76, "Проверка пакета и контрольных сумм…"));
        var package = await _archiveService.ValidateAsync(
            archivePath,
            product,
            cancellationToken);

        progress?.Report(new ProductInstallProgress(88, "Повторная проверка версии игры…"));
        installation = RequireInstallationAtRoot(product, installation.GameRoot);

        progress?.Report(new ProductInstallProgress(92, "Безопасная установка файлов…"));
        var result = await _installer.InstallAsync(
            package,
            product,
            installation.GameRoot,
            installation.BuildId,
            cancellationToken);

        progress?.Report(new ProductInstallProgress(100, "Установка завершена."));
        return result;
    }

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
        progress?.Report(new ProductInstallProgress(0, "Поиск игры и проверка версии…"));
        var installedState = await stateStore.LoadAsync(product.Id, cancellationToken)
            ?? throw new ProductInstallationException(
                $"Продукт '{product.Id}' не установлен через NFG Hub.");
        var installation = SelectInstallation(product, installedState.GameRoot);

        var downloadProgress = new Progress<PackageDownloadProgress>(value =>
            progress?.Report(new ProductInstallProgress(
                8 + value.Percentage * 0.66,
                $"Загрузка выбранной версии: {value.Percentage:0}%")));
        var archivePath = await _downloader.DownloadAsync(
            product,
            _downloadRoot,
            downloadProgress,
            cancellationToken);

        progress?.Report(new ProductInstallProgress(76, "Проверка пакета и контрольных сумм…"));
        var package = await _archiveService.ValidateAsync(
            archivePath,
            product,
            cancellationToken);

        progress?.Report(new ProductInstallProgress(88, "Повторная проверка версии игры…"));
        installation = RequireInstallationAtRoot(product, installation.GameRoot);

        progress?.Report(new ProductInstallProgress(92, "Безопасное переключение версии…"));
        var result = await _installer.SwitchVersionAsync(
            package,
            product,
            installation.GameRoot,
            installation.BuildId,
            cancellationToken);

        progress?.Report(new ProductInstallProgress(100, "Версия продукта переключена."));
        return result;
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

        SteamGameInstallation? selected = null;
        if (!string.IsNullOrWhiteSpace(preferredGameRoot))
        {
            selected = candidates.FirstOrDefault(candidate =>
                PathsEqual(candidate.GameRoot, preferredGameRoot));
            return selected is null
                ? null
                : ToProductInstallation(selected);
        }

        var expectedBuildId = GetExpectedSteamBuildId(product);
        selected = candidates
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

    public Task RemoveFromDeviceAsync(
        string productId,
        CancellationToken cancellationToken = default) =>
        _installer.UninstallAsync(productId, cancellationToken);

    public Task<InstalledProductState> SetEnabledAsync(
        string productId,
        bool isEnabled,
        CancellationToken cancellationToken = default) =>
        _installer.SetEnabledAsync(productId, isEnabled, cancellationToken);

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

    private static string GetSteamAppId(ProductManifest product)
    {
        return TryGetSteamAppId(product)
            ?? throw new ProductInstallationException(
                $"Продукт '{product.Id}' должен объявлять один Steam App ID.");
    }

    private static string? TryGetSteamAppId(ProductManifest product)
    {
        var appIds = product.Installation.Detection
            .Where(rule => rule is not null &&
                           string.Equals(
                               rule.Provider,
                               "steam",
                               StringComparison.OrdinalIgnoreCase))
            .Select(rule => rule.ProductId)
            .Where(value => !string.IsNullOrWhiteSpace(value) && value.All(char.IsAsciiDigit))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return appIds.Length == 1 ? appIds[0] : null;
    }

    private static string? GetExpectedSteamBuildId(ProductManifest product)
    {
        var gameVersion = product.Compatibility.GameVersion;
        return gameVersion is not null &&
               gameVersion.StartsWith(ProductReleaseCatalog.SteamBuildPrefix, StringComparison.Ordinal)
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
}
