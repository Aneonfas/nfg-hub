using Nfg.Store.Installation;

namespace Nfg.Store.App.Services;

public sealed class ProductInstallationException : Exception
{
    public ProductInstallationException(string message)
        : base(message)
    {
    }

    public ProductInstallationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ProductInstallationException(
        string message,
        Exception innerException,
        InstalledProductState? actualState,
        bool actualStateUnreadable)
        : base(message, innerException)
    {
        ActualState = actualState;
        ActualStateUnreadable = actualStateUnreadable;
    }

    public InstalledProductState? ActualState { get; }

    public bool ActualStateUnreadable { get; }
}
