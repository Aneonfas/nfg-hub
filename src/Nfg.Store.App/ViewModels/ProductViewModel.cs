using Nfg.Store.Contracts;
using Nfg.Store.App.Infrastructure;

namespace Nfg.Store.App.ViewModels;

public sealed class ProductViewModel : LocalizedObservableObject
{
    private readonly ProductManifest _manifest;

    public ProductViewModel(ProductManifest manifest)
    {
        _manifest = manifest;
    }

    public string Id => _manifest.Id;

    public string Title => _manifest.Display.Title;

    public string Subtitle => _manifest.Display.Subtitle;

    public string TypeLabel => _manifest.Type switch
    {
        "localization" => Text.Get("Product.Type.Localization"),
        "mod" => Text.Get("Product.Type.Mod"),
        "tool" => Text.Get("Product.Type.Tool"),
        _ => Text.Get("Product.Type.Product")
    };

    public string Summary => _manifest.Display.Summary;

    public string Description => _manifest.Display.Description;

    public IReadOnlyList<string> Features => _manifest.Display.Features;

    public string VersionLabel => _manifest.Release.Version;

    public string ReleaseChangesTitle => Text.Format("Product.WhatsNew", VersionLabel);

    public IReadOnlyList<string> ReleaseHighlights => _manifest.Release.Highlights;

    public IReadOnlyList<string> KnownIssues => _manifest.Release.KnownIssues;

    public string? NotesUrl => _manifest.Release.NotesUrl?.AbsoluteUri;

    public bool HasFeatures => Features.Count > 0;

    public bool HasReleaseHighlights => ReleaseHighlights.Count > 0;

    public bool HasKnownIssues => KnownIssues.Count > 0;

    public bool HasNotesUrl => NotesUrl is not null;

    public string ChannelLabel => _manifest.Release.Channel.ToUpperInvariant();

    public string Monogram
    {
        get
        {
            var initials = Title
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Take(2)
                .Select(part => char.ToUpperInvariant(part[0]));
            var value = string.Concat(initials);
            return value.Length == 0 ? "NFG" : value;
        }
    }

    public string CompatibilityLabel => _manifest.Compatibility.Status switch
    {
        "compatible" => Text.Get("Product.Compatible"),
        "incompatible" => Text.Get("Product.Incompatible"),
        _ => Text.Get("Product.NeedsReview")
    };

    public string ProgressLabel => _manifest.Progress.TranslationPercent is { } percent
        ? $"{percent}%"
        : _manifest.Progress.Label;

    public double ProgressValue => _manifest.Progress.TranslationPercent ?? 0;

    public bool HasMeasuredProgress => _manifest.Progress.TranslationPercent.HasValue;

    public bool SupportsRollback => _manifest.Installation.SupportsRollback;

    public bool HasPublishedRelease =>
        _manifest.Release.Payload is not null &&
        !_manifest.Release.Channel.Equals("development", StringComparison.OrdinalIgnoreCase) &&
        _manifest.Compatibility.Status.Equals("compatible", StringComparison.OrdinalIgnoreCase);

    public string RollbackLabel => SupportsRollback
        ? Text.Get("Product.SafeRemoval")
        : Text.Get("Product.NoRollback");
}
