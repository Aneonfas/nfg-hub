using System.Text.Json.Serialization;

namespace Nfg.Store.Contracts;

public sealed record ProductManifest
{
    public required int SchemaVersion { get; init; }

    public required string Id { get; init; }

    public required string Type { get; init; }

    /// <summary>
    /// Stable product family used to group localized variants in the Hub.
    /// Required for schema-v2 localization products.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FamilyId { get; init; }

    /// <summary>
    /// BCP-47 language tag of this product variant.
    /// Required for schema-v2 localization products.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Locale { get; init; }

    /// <summary>
    /// Stable mutually-exclusive installation slot shared by product variants.
    /// Required for schema-v2 localization products.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExclusiveGroup { get; init; }

    public required ProductDisplay Display { get; init; }

    public required ProductRelease Release { get; init; }

    /// <summary>
    /// Additional published and historical releases available for explicit selection.
    /// The legacy <see cref="Release"/> property remains the current/default release.
    /// </summary>
    public IReadOnlyList<ProductRelease> Releases { get; init; } = [];

    public required ProductCompatibility Compatibility { get; init; }

    public required ProductInstallation Installation { get; init; }

    public required IReadOnlyList<ProductDependency> Dependencies { get; init; }

    public required ProductProgress Progress { get; init; }
}

public sealed record ProductDisplay
{
    public required string Title { get; init; }

    public required string Subtitle { get; init; }

    public required string Summary { get; init; }

    public required string Description { get; init; }

    public IReadOnlyList<string> Features { get; init; } = [];
}

public sealed record ProductRelease
{
    public required string Version { get; init; }

    public required string Channel { get; init; }

    /// <summary>
    /// Game version this release was verified against, for example
    /// <c>steam-build-24378492</c>. When omitted, the product-level compatibility
    /// value is used for backward compatibility with schema v1 catalogs.
    /// </summary>
    public string? GameVersion { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    public IReadOnlyList<string> Highlights { get; init; } = [];

    public IReadOnlyList<string> KnownIssues { get; init; } = [];

    public Uri? NotesUrl { get; init; }

    public ProductPayload? Payload { get; init; }
}

public sealed record ProductPayload
{
    public required Uri Url { get; init; }

    public required long SizeBytes { get; init; }

    public required string Sha256 { get; init; }
}

public sealed record ProductCompatibility
{
    public required IReadOnlyList<string> Platforms { get; init; }

    public string? GameVersion { get; init; }

    public required string Status { get; init; }
}

public sealed record ProductInstallation
{
    public required string Strategy { get; init; }

    public required bool SupportsRollback { get; init; }

    public required string RequiresElevation { get; init; }

    public required IReadOnlyList<ProductDetectionRule> Detection { get; init; }
}

public sealed record ProductDetectionRule
{
    public required string Provider { get; init; }

    public string? ProductId { get; init; }
}

public sealed record ProductDependency
{
    public required string ProductId { get; init; }

    public required string VersionRange { get; init; }
}

public sealed record ProductProgress
{
    public int? TranslationPercent { get; init; }

    public required string Label { get; init; }
}
