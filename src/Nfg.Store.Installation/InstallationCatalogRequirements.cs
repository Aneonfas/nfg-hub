namespace Nfg.Store.Installation;

public static class InstallationCatalogRequirements
{
    public static IReadOnlySet<string> UnionProductIds(
        IEnumerable<string> libraryProductIds,
        IEnumerable<InstalledProductState> installedStates)
    {
        ArgumentNullException.ThrowIfNull(libraryProductIds);
        ArgumentNullException.ThrowIfNull(installedStates);

        var requiredProductIds = new HashSet<string>(
            libraryProductIds,
            StringComparer.Ordinal);
        foreach (var state in installedStates)
        {
            ArgumentNullException.ThrowIfNull(state);
            requiredProductIds.Add(state.ProductId);
        }

        return requiredProductIds;
    }
}
