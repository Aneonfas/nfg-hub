using Nfg.Store.Contracts;
using Nfg.Store.Core;

namespace Nfg.Store.App.ViewModels;

public sealed class ProductReleaseOptionViewModel
{
    public ProductReleaseOptionViewModel(
        ProductManifest product,
        ProductRelease release,
        string? detectedGameBuildId,
        bool isRecommended)
    {
        Release = release;
        Version = release.Version;
        ChannelLabel = release.Channel.ToUpperInvariant();
        IsPublished = ProductReleaseCatalog.IsPublished(release);
        IsExactlyCompatible = ProductReleaseCatalog.IsExactlyCompatible(
            product,
            release,
            detectedGameBuildId);
        IsAutomaticChoice = isRecommended;
        IsRecommended = isRecommended && IsExactlyCompatible;

        var testedGameVersion = ProductReleaseCatalog.GetEffectiveGameVersion(product, release);
        TestedGameBuildLabel = FormatGameBuild(testedGameVersion);
        DetectedGameBuildLabel = string.IsNullOrWhiteSpace(detectedGameBuildId)
            ? "не определена"
            : detectedGameBuildId;
        CompatibilityLabel = CreateCompatibilityLabel(
            IsExactlyCompatible,
            TestedGameBuildLabel,
            detectedGameBuildId);
        DisplayLabel = IsRecommended
            ? $"{Version} — рекомендуется"
            : IsAutomaticChoice
                ? $"{Version} — последняя доступная"
                : Version;
    }

    public ProductRelease Release { get; }

    public string Version { get; }

    public string DisplayLabel { get; }

    public string ChannelLabel { get; }

    public bool IsPublished { get; }

    public bool IsRecommended { get; }

    public bool IsAutomaticChoice { get; }

    public bool IsExactlyCompatible { get; }

    public string? TestedGameBuildLabel { get; }

    public string DetectedGameBuildLabel { get; }

    public string CompatibilityLabel { get; }

    public string ReleaseChangesTitle => $"Что нового в {Version}";

    public IReadOnlyList<string> ReleaseHighlights => Release.Highlights;

    public IReadOnlyList<string> KnownIssues => Release.KnownIssues;

    public string? NotesUrl => Release.NotesUrl?.AbsoluteUri;

    public bool HasReleaseHighlights => ReleaseHighlights.Count > 0;

    public bool HasKnownIssues => KnownIssues.Count > 0;

    public bool HasNotesUrl => NotesUrl is not null;

    private static string? FormatGameBuild(string? gameVersion)
    {
        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            return null;
        }

        return gameVersion.StartsWith(
            ProductReleaseCatalog.SteamBuildPrefix,
            StringComparison.Ordinal)
            ? gameVersion[ProductReleaseCatalog.SteamBuildPrefix.Length..]
            : gameVersion;
    }

    private static string CreateCompatibilityLabel(
        bool isExactlyCompatible,
        string? testedGameBuild,
        string? detectedGameBuild)
    {
        if (isExactlyCompatible)
        {
            return $"Проверено для вашей сборки ({detectedGameBuild})";
        }

        if (string.IsNullOrWhiteSpace(detectedGameBuild))
        {
            return testedGameBuild is null
                ? "Версия игры не определена"
                : $"Проверено на сборке {testedGameBuild}";
        }

        return testedGameBuild is null
            ? $"Не проверено на вашей сборке ({detectedGameBuild})"
            : $"Проверено на {testedGameBuild}; у вас {detectedGameBuild}";
    }
}
