namespace Nfg.Store.Core;

public enum CatalogSourceKind
{
    Remote,
    Cache,
    Bundled
}

public sealed record CatalogLoadResult(
    StoreCatalog Catalog,
    CatalogSourceKind Source,
    Uri RemoteUri,
    string CachePath);
