using Nfg.Store.Contracts;

namespace Nfg.Store.Core;

/// <summary>
/// Derives the stable state, journal, and operation-lock identity for a product.
/// </summary>
public static class ProductInstallationKey
{
    public static string FromManifest(ProductManifest product)
    {
        ArgumentNullException.ThrowIfNull(product);

        var installationKey = product.ExclusiveGroup ?? product.Id;
        if (string.IsNullOrWhiteSpace(installationKey))
        {
            throw new ArgumentException(
                "The product must declare a product id or exclusive installation group.",
                nameof(product));
        }

        return installationKey;
    }
}
