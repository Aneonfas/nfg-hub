namespace Nfg.Store.Installation;

public sealed class InstallationStateException : Exception
{
    public InstallationStateException(string message)
        : base(message)
    {
    }

    public InstallationStateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
