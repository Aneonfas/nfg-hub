using System.Text.Json.Serialization;

namespace Nfg.Store.Contracts;

public sealed record NfgPackageManifest
{
    [JsonPropertyName("schema")]
    public required string Schema { get; init; }

    [JsonPropertyName("productId")]
    public required string ProductId { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("strategy")]
    public required string Strategy { get; init; }

    [JsonPropertyName("steam")]
    public required NfgPackageSteam Steam { get; init; }

    [JsonPropertyName("files")]
    public required IReadOnlyList<NfgPackageFile> Files { get; init; }
}

public sealed record NfgPackageSteam
{
    [JsonPropertyName("appId")]
    public required string AppId { get; init; }

    [JsonPropertyName("buildId")]
    public required string BuildId { get; init; }
}

public sealed record NfgPackageFile
{
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("destination")]
    public required string Destination { get; init; }

    [JsonPropertyName("sizeBytes")]
    public required long SizeBytes { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }
}
