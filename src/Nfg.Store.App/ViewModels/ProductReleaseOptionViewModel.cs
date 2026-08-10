using Nfg.Store.Contracts;
using Nfg.Store.Core;
using Nfg.Store.App.Infrastructure;

namespace Nfg.Store.App.ViewModels;

public sealed class ProductReleaseOptionViewModel : LocalizedObservableObject
{
    private readonly string? _detectedGameBuildId;
    private readonly string? _testedGameBuildLabel;
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
        _testedGameBuildLabel = FormatGameBuild(testedGameVersion);
        _detectedGameBuildId = detectedGameBuildId;
    }

    public ProductRelease Release { get; }

    public string Version { get; }

    public string DisplayLabel => IsRecommended
        ? Text.Format("Release.Recommended", Version)
        : IsAutomaticChoice
            ? Text.Format("Release.Latest", Version)
            : Version;

    public string ChannelLabel { get; }

    public bool IsPublished { get; }

    public bool IsRecommended { get; }

    public bool IsAutomaticChoice { get; }

    public bool IsExactlyCompatible { get; }

    public string? TestedGameBuildLabel => _testedGameBuildLabel;

    public string DetectedGameBuildLabel => string.IsNullOrWhiteSpace(_detectedGameBuildId)
        ? Text.Get("Release.GameBuildUnknown")
        : _detectedGameBuildId;

    public string CompatibilityLabel => CreateCompatibilityLabel(
        IsExactlyCompatible,
        TestedGameBuildLabel,
        _detectedGameBuildId);

    public string ReleaseChangesTitle => Text.Format("Product.WhatsNew", Version);

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
            return Text.Format("Release.Exact", detectedGameBuild);
        }

        if (string.IsNullOrWhiteSpace(detectedGameBuild))
        {
            return testedGameBuild is null
                ? Text.Get("Release.GameUnknown")
                : Text.Format("Release.Tested", testedGameBuild);
        }

        return testedGameBuild is null
            ? Text.Format("Release.Unverified", detectedGameBuild)
            : Text.Format("Release.Mismatch", testedGameBuild, detectedGameBuild);
    }
}
