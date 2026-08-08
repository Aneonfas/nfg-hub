using System.Text.Json;
using Nfg.Store.Core;

namespace Nfg.Store.Installation;

public sealed class InstallationStateStore : IInstallationStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _stateRoot;

    public InstallationStateStore(string dataRoot)
    {
        DataRoot = Path.GetFullPath(dataRoot);
        _stateRoot = Path.Combine(DataRoot, "state", "installations");
    }

    public string DataRoot { get; }

    public async Task<InstalledProductState?> LoadAsync(
        string productId,
        CancellationToken cancellationToken = default)
    {
        var path = GetStatePath(productId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var state = await JsonSerializer.DeserializeAsync<InstalledProductState>(
                stream,
                SerializerOptions,
                cancellationToken);
            ValidateState(state, productId);
            return state;
        }
        catch (JsonException exception)
        {
            throw new InstallationStateException(
                $"Installation state for '{productId}' contains invalid JSON.",
                exception);
        }
    }

    public async Task SaveAsync(
        InstalledProductState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateState(state, state.ProductId);

        var path = GetStatePath(state.ProductId);
        Directory.CreateDirectory(_stateRoot);
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
                await JsonSerializer.SerializeAsync(
                    stream,
                    state,
                    SerializerOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public void Delete(string productId)
    {
        var path = GetStatePath(productId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string GetStatePath(string productId) =>
        Path.Combine(_stateRoot, $"{SanitizeProductId(productId)}.json");

    internal static void ValidateState(InstalledProductState? state, string expectedProductId)
    {
        if (state is null ||
            state.SchemaVersion != 1 ||
            !string.Equals(state.ProductId, expectedProductId, StringComparison.Ordinal) ||
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
                $"Installation state for '{expectedProductId}' is invalid.");
        }

        if (state.Files
                .Select(file => file.Destination)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != state.Files.Count)
        {
            throw new InstallationStateException(
                $"Installation state for '{expectedProductId}' contains duplicate destinations.");
        }
    }

    internal static string SanitizeProductId(string productId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productId);
        if (!productId.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'))
        {
            throw new InstallationStateException(
                $"Product id '{productId}' cannot be used for installation state.");
        }

        return productId;
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
