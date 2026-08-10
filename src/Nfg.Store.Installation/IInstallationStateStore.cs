namespace Nfg.Store.Installation;

public interface IInstallationStateStore
{
    string DataRoot { get; }

    Task<InstalledProductState?> LoadAsync(
        string installationKey,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InstalledProductState>> LoadAllAsync(
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        InstalledProductState state,
        CancellationToken cancellationToken = default);

    void Delete(string installationKey);
}
