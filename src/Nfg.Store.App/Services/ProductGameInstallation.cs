namespace Nfg.Store.App.Services;

public sealed record ProductGameInstallation(
    string AppId,
    string BuildId,
    string? TargetBuildId,
    string GameRoot,
    string AppManifestPath)
{
    public bool HasPendingBuildChange =>
        !string.IsNullOrWhiteSpace(TargetBuildId) &&
        !TargetBuildId.Equals("0", StringComparison.Ordinal) &&
        !TargetBuildId.Equals(BuildId, StringComparison.Ordinal);
}
