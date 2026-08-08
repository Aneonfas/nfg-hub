namespace Nfg.Store.Installation;

public sealed class ProductLibraryException : Exception
{
    public ProductLibraryException(string message)
        : base(message)
    {
    }

    public ProductLibraryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
