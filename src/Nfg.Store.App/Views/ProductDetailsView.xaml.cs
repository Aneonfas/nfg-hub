using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace Nfg.Store.App.Views;

public partial class ProductDetailsView : UserControl
{
    public ProductDetailsView()
    {
        InitializeComponent();
    }

    private void ReleaseNotesLink_OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        _ = Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
        {
            UseShellExecute = true
        });
        e.Handled = true;
    }
}
