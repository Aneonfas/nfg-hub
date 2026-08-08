namespace Nfg.Store.Platform.Windows;

public sealed record SteamGameInstallation(
    string AppId,
    string BuildId,
    string? TargetBuildId,
    string? Language,
    string InstallDirectoryName,
    string GameRoot,
    string AppManifestPath);
