namespace Nfg.Store.Installation;

public sealed record InstalledProductState
{
    public required int SchemaVersion { get; init; }

    public required string ProductId { get; init; }

    public required string Version { get; init; }

    public required long PackageSizeBytes { get; init; }

    public required string PackageSha256 { get; init; }

    public required string SteamAppId { get; init; }

    public required string SteamBuildId { get; init; }

    public string? DetectedSteamBuildId { get; init; }

    public required string GameRoot { get; init; }

    public required DateTimeOffset InstalledAt { get; init; }

    public bool IsEnabled { get; init; } = true;

    public required IReadOnlyList<InstalledFileState> Files { get; init; }
}

public sealed record InstalledFileState
{
    public required string Destination { get; init; }

    public required long SizeBytes { get; init; }

    public required string Sha256 { get; init; }
}
