namespace Nfg.Store.Installation;

public interface IInstallationStateStore
{
    string DataRoot { get; }

    Task<InstalledProductState?> LoadAsync(
        string productId,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        InstalledProductState state,
        CancellationToken cancellationToken = default);

    void Delete(string productId);
}
