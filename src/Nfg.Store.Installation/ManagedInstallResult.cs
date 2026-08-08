namespace Nfg.Store.Installation;

public enum ManagedInstallOutcome
{
    Installed,
    Adopted,
    AlreadyInstalled,
    Updated
}

public sealed record ManagedInstallResult(
    ManagedInstallOutcome Outcome,
    InstalledProductState State);
