using Nfg.Store.Contracts;
using Nfg.Store.Installation;

namespace Nfg.Store.App.Services;

internal interface IProductInstallationOperations
{
    ProductGameInstallation? DetectGameInstallation(
        ProductManifest product,
        string? preferredGameRoot = null);

    Task<ManagedInstallResult> ApplyVariantAsync(
        ProductManifest product,
        string? expectedProductId,
        IProgress<ProductInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<InstalledProductState?> LoadInstalledStateAsync(
        string installationKey,
        CancellationToken cancellationToken = default);

    Task<ManagedFamilyInventory> ReconcileFamilyAsync(
        IReadOnlyList<ProductManifest> products,
        CancellationToken cancellationToken = default);

    Task<ManagedFamilyInventory> RemoveFamilyVariantAsync(
        IReadOnlyList<ProductManifest> products,
        string selectedProductId,
        ManagedVariantRemovalScope scope,
        CancellationToken cancellationToken = default);

    Task<InstalledProductState> SetEnabledAsync(
        string installationKey,
        string expectedProductId,
        bool isEnabled,
        CancellationToken cancellationToken = default);

    Task RemoveFromDeviceAsync(
        string installationKey,
        string expectedProductId,
        CancellationToken cancellationToken = default);
}

internal sealed class ProductInstallationOperations(
    ProductInstallationCoordinator coordinator) : IProductInstallationOperations
{
    public ProductGameInstallation? DetectGameInstallation(
        ProductManifest product,
        string? preferredGameRoot = null) =>
        coordinator.DetectGameInstallation(product, preferredGameRoot);

    public Task<ManagedInstallResult> ApplyVariantAsync(
        ProductManifest product,
        string? expectedProductId,
        IProgress<ProductInstallProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        coordinator.ApplyVariantAsync(
            product,
            expectedProductId,
            progress,
            cancellationToken);

    public Task<InstalledProductState?> LoadInstalledStateAsync(
        string installationKey,
        CancellationToken cancellationToken = default) =>
        coordinator.LoadInstalledStateAsync(installationKey, cancellationToken);

    public Task<ManagedFamilyInventory> ReconcileFamilyAsync(
        IReadOnlyList<ProductManifest> products,
        CancellationToken cancellationToken = default) =>
        coordinator.ReconcileFamilyAsync(products, cancellationToken);

    public Task<ManagedFamilyInventory> RemoveFamilyVariantAsync(
        IReadOnlyList<ProductManifest> products,
        string selectedProductId,
        ManagedVariantRemovalScope scope,
        CancellationToken cancellationToken = default) =>
        coordinator.RemoveFamilyVariantAsync(
            products,
            selectedProductId,
            scope,
            cancellationToken);

    public Task<InstalledProductState> SetEnabledAsync(
        string installationKey,
        string expectedProductId,
        bool isEnabled,
        CancellationToken cancellationToken = default) =>
        coordinator.SetEnabledAsync(
            installationKey,
            expectedProductId,
            isEnabled,
            cancellationToken);

    public Task RemoveFromDeviceAsync(
        string installationKey,
        string expectedProductId,
        CancellationToken cancellationToken = default) =>
        coordinator.RemoveFromDeviceAsync(
            installationKey,
            expectedProductId,
            cancellationToken);
}
