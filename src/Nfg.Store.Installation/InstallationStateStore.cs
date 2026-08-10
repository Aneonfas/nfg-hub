using System.Text.Json;
using System.Text.Json.Serialization;
using Nfg.Store.Core;

namespace Nfg.Store.Installation;

public sealed class InstallationStateStore : IInstallationStateStore
{
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowDuplicateProperties = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private readonly string _stateRoot;

    public InstallationStateStore(string dataRoot)
    {
        DataRoot = Path.GetFullPath(dataRoot);
        _stateRoot = Path.Combine(DataRoot, "state", "installations");
    }

    public string DataRoot { get; }

    internal string StateRoot => _stateRoot;

    public async Task<InstalledProductState?> LoadAsync(
        string installationKey,
        CancellationToken cancellationToken = default)
    {
        var path = GetStatePath(installationKey);
        if (!File.Exists(path))
        {
            return null;
        }

        return (await ReadSnapshotAsync(path, installationKey, cancellationToken)).State;
    }

    public async Task<IReadOnlyList<InstalledProductState>> LoadAllAsync(
        CancellationToken cancellationToken = default) =>
        (await ReadSnapshotsAsync(cancellationToken))
        .Select(snapshot => snapshot.State)
        .ToArray();

    public async Task SaveAsync(
        InstalledProductState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SchemaVersion != 2 || state.InstallationKey is null)
        {
            throw new InstallationStateException(
                "Only schema-v2 installation state can be committed.");
        }

        ValidateState(state, state.InstallationKey);
        Directory.CreateDirectory(_stateRoot);
        await WriteAtomicAsync(GetStatePath(state.InstallationKey), state, cancellationToken);
    }

    internal async Task SaveNewAsync(
        InstalledProductState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SchemaVersion != 2 || state.InstallationKey is null)
        {
            throw new InstallationStateException(
                "Only schema-v2 installation state can be committed.");
        }

        ValidateState(state, state.InstallationKey);
        Directory.CreateDirectory(_stateRoot);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, SerializerOptions);
        await WriteBytesAtomicAsync(
            GetStatePath(state.InstallationKey),
            bytes,
            overwrite: false,
            cancellationToken);
    }

    public void Delete(string installationKey)
    {
        var path = GetStatePath(installationKey);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    internal async Task<IReadOnlyList<InstallationStateSnapshot>> ReadSnapshotsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_stateRoot))
        {
            return [];
        }

        var snapshots = new List<InstallationStateSnapshot>();
        foreach (var path in Directory
                     .EnumerateFiles(_stateRoot, "*.json", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileNameWithoutExtension(path);
            SanitizeInstallationKey(fileName);
            snapshots.Add(await ReadSnapshotAsync(path, fileName, cancellationToken));
        }

        return snapshots;
    }

    internal string GetStatePath(string installationKey) =>
        Path.Combine(_stateRoot, $"{SanitizeInstallationKey(installationKey)}.json");

    internal static void ValidateState(
        InstalledProductState? state,
        string expectedInstallationKey)
    {
        SanitizeInstallationKey(expectedInstallationKey);
        var hasValidIdentity = state?.SchemaVersion switch
        {
            1 => state.InstallationKey is null &&
                 string.Equals(
                     state.ProductId,
                     expectedInstallationKey,
                     StringComparison.Ordinal),
            2 => string.Equals(
                     state.InstallationKey,
                     expectedInstallationKey,
                     StringComparison.Ordinal) &&
                 IsSafeIdentifier(state.ProductId),
            _ => false
        };

        if (!hasValidIdentity ||
            state is null ||
            !SemanticVersionComparer.IsValid(state.Version) ||
            state.PackageSizeBytes <= 0 ||
            !IsSha256(state.PackageSha256) ||
            string.IsNullOrWhiteSpace(state.SteamAppId) ||
            string.IsNullOrWhiteSpace(state.SteamBuildId) ||
            state.DetectedSteamBuildId is not null &&
            (state.DetectedSteamBuildId.Length == 0 ||
             !state.DetectedSteamBuildId.All(char.IsAsciiDigit)) ||
            string.IsNullOrWhiteSpace(state.GameRoot) ||
            state.Files is not { Count: > 0 } ||
            state.Files.Any(file =>
                file is null ||
                string.IsNullOrWhiteSpace(file.Destination) ||
                file.SizeBytes < 0 ||
                !IsSha256(file.Sha256)))
        {
            throw new InstallationStateException(
                $"Installation state for '{expectedInstallationKey}' is invalid.");
        }

        if (state.Files
                .Select(file => file.Destination)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != state.Files.Count)
        {
            throw new InstallationStateException(
                $"Installation state for '{expectedInstallationKey}' contains duplicate destinations.");
        }
    }

    internal static string SanitizeInstallationKey(string installationKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationKey);
        if (!IsSafeIdentifier(installationKey))
        {
            throw new InstallationStateException(
                $"Installation key '{installationKey}' cannot be used for installation state.");
        }

        return installationKey;
    }

    internal static string SanitizeProductId(string productId) =>
        SanitizeInstallationKey(productId);

    internal static async Task WriteBytesAtomicAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(path)
            ?? throw new InstallationStateException("Installation state path has no parent directory.");
        Directory.CreateDirectory(parent);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16384,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool IsSafeIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static async Task WriteAtomicAsync(
        string path,
        InstalledProductState state,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, SerializerOptions);
        await WriteBytesAtomicAsync(path, bytes, overwrite: true, cancellationToken);
    }

    private static async Task<InstallationStateSnapshot> ReadSnapshotAsync(
        string path,
        string expectedInstallationKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var state = JsonSerializer.Deserialize<InstalledProductState>(
                bytes,
                SerializerOptions);
            ValidateState(state, expectedInstallationKey);
            return new InstallationStateSnapshot(
                Path.GetFullPath(path),
                expectedInstallationKey,
                bytes,
                state!);
        }
        catch (JsonException exception)
        {
            throw new InstallationStateException(
                $"Installation state for '{expectedInstallationKey}' contains invalid JSON.",
                exception);
        }
    }
}

internal sealed record InstallationStateSnapshot(
    string Path,
    string FileKey,
    byte[] Bytes,
    InstalledProductState State);
