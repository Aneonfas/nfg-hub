using Nfg.Store.Contracts;

namespace Nfg.Store.App.ViewModels;

public sealed class ProductVariantOptionViewModel
{
    internal ProductVariantOptionViewModel(
        ProductManifest manifest,
        ProductViewModel? product = null)
    {
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        Product = product ?? new ProductViewModel(manifest);
        ProductId = manifest.Id;
        Locale = manifest.Locale;
        LocaleLabel = string.IsNullOrWhiteSpace(Locale)
            ? Product.Title
            : Locale.ToUpperInvariant();
        DisplayLabel = LocaleLabel;
        Subtitle = Product.Subtitle;
    }

    internal ProductManifest Manifest { get; }

    public ProductViewModel Product { get; }

    public string ProductId { get; }

    public string? Locale { get; }

    public string LocaleLabel { get; }

    public string DisplayLabel { get; }

    public string Subtitle { get; }
}
