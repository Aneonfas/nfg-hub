using System.Windows;
using System.Windows.Input;

namespace Nfg.Store.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Activated += MainWindow_OnActivated;
    }

    private void MainWindow_OnActivated(object? sender, EventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel viewModel)
        {
            viewModel.RefreshGameCompatibility();
        }
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximized();
            return;
        }

        DragMove();
    }

    private void Minimize_OnClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void Maximize_OnClick(object sender, RoutedEventArgs e) => ToggleMaximized();

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximized() =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
}
