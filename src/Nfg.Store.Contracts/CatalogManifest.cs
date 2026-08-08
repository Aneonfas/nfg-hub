namespace Nfg.Store.Contracts;

public sealed record CatalogManifest
{
    public required int SchemaVersion { get; init; }

    public required string CatalogId { get; init; }

    public required string DisplayName { get; init; }

    public required IReadOnlyList<string> Products { get; init; }
}
