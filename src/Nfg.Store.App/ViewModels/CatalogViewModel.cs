using System.Windows.Input;
using Nfg.Store.App.Infrastructure;

namespace Nfg.Store.App.ViewModels;

public sealed class CatalogViewModel : PageViewModel
{
    private double _verticalOffset;

    public CatalogViewModel(
        IReadOnlyList<ProductStateViewModel> products,
        Action<ProductStateViewModel> openProduct)
        : base("Каталог")
    {
        ArgumentNullException.ThrowIfNull(products);
        ArgumentNullException.ThrowIfNull(openProduct);

        Products = products;
        OpenProductCommand = new RelayCommand<ProductStateViewModel>(openProduct);
    }

    public IReadOnlyList<ProductStateViewModel> Products { get; }

    public bool HasProducts => Products.Count > 0;

    public bool IsEmpty => !HasProducts;

    public ICommand OpenProductCommand { get; }

    public double VerticalOffset
    {
        get => _verticalOffset;
        set
        {
            var normalizedValue = double.IsFinite(value)
                ? Math.Max(0, value)
                : 0;
            SetProperty(ref _verticalOffset, normalizedValue);
        }
    }
}
