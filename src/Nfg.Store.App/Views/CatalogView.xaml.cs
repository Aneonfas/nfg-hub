using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Nfg.Store.App.ViewModels;

namespace Nfg.Store.App.Views;

public partial class CatalogView : UserControl
{
    private bool _canPersistVerticalOffset;
    private int _restoreGeneration;

    public CatalogView()
    {
        InitializeComponent();
    }

    private void CatalogScrollViewer_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer ||
            DataContext is not CatalogViewModel viewModel)
        {
            return;
        }

        _canPersistVerticalOffset = false;
        var restoreGeneration = ++_restoreGeneration;
        var verticalOffset = viewModel.VerticalOffset;

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                if (restoreGeneration != _restoreGeneration ||
                    !scrollViewer.IsLoaded ||
                    !ReferenceEquals(DataContext, viewModel))
                {
                    return;
                }

                scrollViewer.ScrollToVerticalOffset(verticalOffset);
                _canPersistVerticalOffset = true;
                viewModel.VerticalOffset = scrollViewer.VerticalOffset;
            }));
    }

    private void CatalogScrollViewer_OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_canPersistVerticalOffset &&
            sender is ScrollViewer scrollViewer &&
            DataContext is CatalogViewModel viewModel)
        {
            viewModel.VerticalOffset = scrollViewer.VerticalOffset;
        }

        _canPersistVerticalOffset = false;
        _restoreGeneration++;
    }

    private void CatalogScrollViewer_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_canPersistVerticalOffset ||
            sender is not ScrollViewer scrollViewer ||
            DataContext is not CatalogViewModel viewModel)
        {
            return;
        }

        viewModel.VerticalOffset = scrollViewer.VerticalOffset;
    }
}
