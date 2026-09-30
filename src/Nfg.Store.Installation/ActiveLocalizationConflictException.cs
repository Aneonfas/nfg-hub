namespace Nfg.Store.Installation;

public sealed class ActiveLocalizationConflictException : ManagedFilesInstallException
{
    public ActiveLocalizationConflictException(IReadOnlyList<string> paths)
        : base(
            "Another active Anvil localization PAK was found: " +
            string.Join(", ", paths) + ". " +
            "Close the game, move the listed file out of the Paks folder or append .disabled " +
            "to its full filename, then retry. NFG Hub has preserved these files.")
    {
        Paths = paths.ToArray();
    }

    public IReadOnlyList<string> Paths { get; }
}
