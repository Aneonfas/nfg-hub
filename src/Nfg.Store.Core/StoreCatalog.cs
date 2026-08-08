using Nfg.Store.Contracts;

namespace Nfg.Store.Core;

public sealed record StoreCatalog(
    string Id,
    string DisplayName,
    IReadOnlyList<ProductManifest> Products);
