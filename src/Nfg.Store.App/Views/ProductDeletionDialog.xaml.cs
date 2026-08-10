using System.Windows;
using Nfg.Store.App.Localization;
using Nfg.Store.App.Services;

namespace Nfg.Store.App.Views;

public partial class ProductDeletionDialog : Window
{
    public ProductDeletionDialog(
        string selectedVariantLabel,
        bool hasOtherInstalledVariants,
        bool isProductFamily)
    {
        InitializeComponent();
        var text = LocalizationService.Instance;
        Title = text.Get("Remove.Title");
        TitleText.Text = Title;
        CancelButton.Content = text.Get("Common.Cancel");
        DeleteAllButton.Visibility = hasOtherInstalledVariants
            ? Visibility.Visible
            : Visibility.Collapsed;
        DeleteAllButton.Content = text.Get("Remove.AllLanguages");
        DeleteSelectedButton.Content = hasOtherInstalledVariants && isProductFamily
            ? text.Format("Remove.SelectedLanguage", selectedVariantLabel)
            : text.Get("Library.Remove");
        MessageText.Text = hasOtherInstalledVariants && isProductFamily
            ? text.Format("Remove.MultiplePrompt", selectedVariantLabel)
            : text.Get("Remove.SinglePrompt");
    }

    internal ProductDeletionChoice Choice { get; private set; } =
        ProductDeletionChoice.Cancel;

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        Choice = ProductDeletionChoice.Cancel;
        DialogResult = false;
    }

    private void DeleteAllButton_OnClick(object sender, RoutedEventArgs e)
    {
        Choice = ProductDeletionChoice.AllVariants;
        DialogResult = true;
    }

    private void DeleteSelectedButton_OnClick(object sender, RoutedEventArgs e)
    {
        Choice = ProductDeletionChoice.SelectedVariant;
        DialogResult = true;
    }
}
