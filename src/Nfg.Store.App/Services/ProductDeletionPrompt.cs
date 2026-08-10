using Nfg.Store.App.Views;

namespace Nfg.Store.App.Services;

internal enum ProductDeletionChoice
{
    Cancel,
    SelectedVariant,
    AllVariants
}

internal interface IProductDeletionPrompt
{
    ProductDeletionChoice Confirm(
        string selectedVariantLabel,
        bool hasOtherInstalledVariants,
        bool isProductFamily);
}

internal sealed class ProductDeletionPrompt : IProductDeletionPrompt
{
    public ProductDeletionChoice Confirm(
        string selectedVariantLabel,
        bool hasOtherInstalledVariants,
        bool isProductFamily)
    {
        var dialog = new ProductDeletionDialog(
            selectedVariantLabel,
            hasOtherInstalledVariants,
            isProductFamily);
        if (System.Windows.Application.Current?.MainWindow is { } owner)
        {
            dialog.Owner = owner;
        }

        _ = dialog.ShowDialog();
        return dialog.Choice;
    }
}
