namespace Nfg.Store.Installation;

public sealed class ManagedFilesInstallException : Exception
{
    public ManagedFilesInstallException(string message)
        : base(message)
    {
    }

    public ManagedFilesInstallException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
