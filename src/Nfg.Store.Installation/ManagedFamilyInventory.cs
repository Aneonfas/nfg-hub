using Nfg.Store.Contracts;
using Nfg.Store.Core;

namespace Nfg.Store.Installation;

public enum ManagedVariantRemovalScope
{
    SelectedVariant,
    AllVariants
}

public sealed record ManagedVariantPackage(
    ProductManifest Product,
    ValidatedPackage Package);

public sealed record ManagedVariantPresence(
    string ProductId,
    string Version,
    bool IsInstalled,
    bool IsEnabled,
    bool HasRelatedArtifacts);

public sealed record ManagedFamilyInventory(
    string InstallationKey,
    InstalledProductState? State,
    IReadOnlyList<ManagedVariantPresence> Variants,
    bool HasConflict = false,
    IReadOnlyList<string>? ConflictPaths = null)
{
    public bool HasAnyInstalledVariants => Variants.Any(variant => variant.IsInstalled);

    public bool HasVariant(string productId) =>
        Variants.Any(variant =>
            variant.ProductId.Equals(productId, StringComparison.Ordinal) &&
            (variant.IsInstalled || variant.HasRelatedArtifacts));

    public bool HasOtherInstalledVariant(string productId) =>
        Variants.Any(variant =>
            !variant.ProductId.Equals(productId, StringComparison.Ordinal) &&
            variant.IsInstalled);

    public static ManagedFamilyInventory FromState(
        string installationKey,
        InstalledProductState? state) => new(
        installationKey,
        state,
        state is null
            ? []
            :
            [
                new ManagedVariantPresence(
                    state.ProductId,
                    state.Version,
                    IsInstalled: true,
                    state.IsEnabled,
                    HasRelatedArtifacts: true)
            ]);
}
