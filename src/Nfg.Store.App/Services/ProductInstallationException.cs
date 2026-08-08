namespace Nfg.Store.App.Services;

public sealed class ProductInstallationException : Exception
{
    public ProductInstallationException(string message)
        : base(message)
    {
    }
}
