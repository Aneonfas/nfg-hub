using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Nfg.Store.App.Infrastructure;

namespace Nfg.Store.App.ViewModels;

public sealed class LibraryViewModel : PageViewModel
{
    private readonly IReadOnlyList<ProductStateViewModel> _productStates;
    private readonly Action<ProductStateViewModel> _openProduct;

    public LibraryViewModel(
        IReadOnlyList<ProductStateViewModel> productStates,
        Action<ProductStateViewModel> openProduct)
        : base("Nav.Library", localizeTitle: true)
    {
        ArgumentNullException.ThrowIfNull(productStates);
        ArgumentNullException.ThrowIfNull(openProduct);

        _productStates = productStates;
        _openProduct = openProduct;
        OpenProductCommand = new RelayCommand<ProductStateViewModel>(_openProduct);

        foreach (var productState in _productStates)
        {
            productState.PropertyChanged += ProductStateOnPropertyChanged;
            if (productState.IsInLibrary)
            {
                Products.Add(productState);
            }
        }
    }

    public ObservableCollection<ProductStateViewModel> Products { get; } = [];

    public bool IsEmpty => Products.Count == 0;

    public bool HasProducts => !IsEmpty;

    public ICommand OpenProductCommand { get; }

    private void ProductStateOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ProductStateViewModel productState ||
            (!string.IsNullOrEmpty(e.PropertyName) &&
             e.PropertyName != nameof(ProductStateViewModel.IsInLibrary)))
        {
            return;
        }

        SynchronizeMembership(productState);
    }

    private void SynchronizeMembership(ProductStateViewModel productState)
    {
        var currentIndex = IndexOfReference(Products, productState);
        if (!productState.IsInLibrary)
        {
            if (currentIndex >= 0)
            {
                Products.RemoveAt(currentIndex);
                NotifyCollectionStateChanged();
            }

            return;
        }

        if (currentIndex >= 0)
        {
            return;
        }

        var sourceIndex = IndexOfReference(_productStates, productState);
        var insertionIndex = 0;
        for (var index = 0; index < sourceIndex; index++)
        {
            if (_productStates[index].IsInLibrary)
            {
                insertionIndex++;
            }
        }

        Products.Insert(insertionIndex, productState);
        NotifyCollectionStateChanged();
    }

    private void NotifyCollectionStateChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasProducts));
    }

    private static int IndexOfReference(
        IReadOnlyList<ProductStateViewModel> products,
        ProductStateViewModel productState)
    {
        for (var index = 0; index < products.Count; index++)
        {
            if (ReferenceEquals(products[index], productState))
            {
                return index;
            }
        }

        return -1;
    }
}
