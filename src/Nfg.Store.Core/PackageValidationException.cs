namespace Nfg.Store.Core;

public sealed class PackageValidationException : Exception
{
    public PackageValidationException(string message)
        : base(message)
    {
    }

    public PackageValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
