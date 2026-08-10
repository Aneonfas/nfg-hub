using System.Collections.ObjectModel;
using System.Windows.Input;
using Nfg.Store.App.Infrastructure;
using Nfg.Store.App.Services;
using Nfg.Store.Contracts;
using Nfg.Store.Core;
using Nfg.Store.Installation;

namespace Nfg.Store.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly RelayCommand _backFromProductCommand;
    private readonly NavigationItemViewModel _catalogNavigation;
    private readonly NavigationItemViewModel _libraryNavigation;
    private readonly IReadOnlyList<ProductStateViewModel> _productStates;
    private NavigationItemViewModel? _detailReturnNavigation;
    private PageViewModel _currentPage;

    public MainWindowViewModel(
        CatalogLoadResult catalogResult,
        string dataPath,
        ProductInstallationCoordinator installationCoordinator,
        ProductLibraryStore libraryStore,
        AppSettings settings,
        AppSettingsStore settingsStore,
        IReadOnlyDictionary<string, InstalledProductState?> installedStates,
        IReadOnlySet<string> libraryProductIds)
    {
        var catalog = catalogResult.Catalog;
        CatalogName = catalog.DisplayName;

        var productGroups = GroupProducts(catalog.Products);
        var productStates = new List<ProductStateViewModel>(productGroups.Count);
        foreach (var products in productGroups)
        {
            var installationKey = ProductInstallationKey.FromManifest(products[0]);
            installedStates.TryGetValue(installationKey, out var installedState);
            var detectionProduct = products.FirstOrDefault(product =>
                    string.Equals(
                        product.Id,
                        installedState?.ProductId,
                        StringComparison.Ordinal))
                ?? products[0];
            var gameInstallation = installationCoordinator.DetectGameInstallation(
                detectionProduct,
                installedState?.GameRoot);
            productStates.Add(new ProductStateViewModel(
                products,
                installationCoordinator,
                libraryStore,
                installedState,
                products.Any(product => libraryProductIds.Contains(product.Id)),
                gameInstallation?.BuildId));
        }
        _productStates = productStates;

        var catalogPage = new CatalogViewModel(productStates, OpenProductFromCatalog);
        var libraryPage = new LibraryViewModel(productStates, OpenProductFromLibrary);
        var settingsPage = new SettingsViewModel(
            catalogResult,
            dataPath,
            settings,
            settingsStore);

        _catalogNavigation = new NavigationItemViewModel("Nav.Catalog", "▦", catalogPage);
        _libraryNavigation = new NavigationItemViewModel("Nav.Library", "▤", libraryPage);

        Navigation =
        [
            _catalogNavigation,
            _libraryNavigation,
            new NavigationItemViewModel("Nav.Settings", "⚙", settingsPage)
        ];

        _currentPage = catalogPage;
        _catalogNavigation.IsSelected = true;
        NavigateCommand = new RelayCommand<NavigationItemViewModel>(Navigate);
        _backFromProductCommand = new RelayCommand(
            BackFromProduct,
            () => _detailReturnNavigation is not null);
        BackFromProductCommand = _backFromProductCommand;
    }

    public string CatalogName { get; }

    public ObservableCollection<NavigationItemViewModel> Navigation { get; }

    public ICommand NavigateCommand { get; }

    public ICommand BackFromProductCommand { get; }

    public PageViewModel CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public void RefreshGameCompatibility()
    {
        foreach (var product in _productStates)
        {
            product.RefreshGameCompatibility();
        }
    }

    private void Navigate(NavigationItemViewModel destination)
    {
        RefreshGameCompatibility();
        _detailReturnNavigation = null;
        _backFromProductCommand.NotifyCanExecuteChanged();
        SelectNavigation(destination);
        CurrentPage = destination.Page;
    }

    private void OpenProductFromCatalog(ProductStateViewModel product) =>
        OpenProduct(product, _catalogNavigation);

    private void OpenProductFromLibrary(ProductStateViewModel product) =>
        OpenProduct(product, _libraryNavigation);

    private void OpenProduct(
        ProductStateViewModel product,
        NavigationItemViewModel returnNavigation)
    {
        product.RefreshGameCompatibility();
        _detailReturnNavigation = returnNavigation;
        _backFromProductCommand.NotifyCanExecuteChanged();
        SelectNavigation(returnNavigation);
        CurrentPage = product;
    }

    private void BackFromProduct()
    {
        var destination = _detailReturnNavigation;
        if (destination is not null)
        {
            Navigate(destination);
        }
    }

    private void SelectNavigation(NavigationItemViewModel destination)
    {
        foreach (var item in Navigation)
        {
            item.IsSelected = ReferenceEquals(item, destination);
        }
    }

    private static IReadOnlyList<IReadOnlyList<ProductManifest>> GroupProducts(
        IReadOnlyList<ProductManifest> products)
    {
        var groups = new List<List<ProductManifest>>(products.Count);
        var familyIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var product in products)
        {
            if (product.FamilyId is not { } familyId)
            {
                groups.Add([product]);
                continue;
            }

            if (!familyIndexes.TryGetValue(familyId, out var groupIndex))
            {
                groupIndex = groups.Count;
                familyIndexes.Add(familyId, groupIndex);
                groups.Add([]);
            }

            groups[groupIndex].Add(product);
        }

        return groups
            .Select(group => (IReadOnlyList<ProductManifest>)group.ToArray())
            .ToArray();
    }
}
