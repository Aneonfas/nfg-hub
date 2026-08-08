using System.Text.Json;
using System.Text.Json.Serialization;
using Nfg.Store.Core;

namespace Nfg.Store.Installation;

internal sealed record ManagedUpdateJournal
{
    public required int SchemaVersion { get; init; }

    public required string OperationId { get; init; }

    public required string ProductId { get; init; }

    public required InstalledProductState OldState { get; init; }

    public required InstalledProductState NewState { get; init; }
}

internal sealed class ManagedUpdateJournalStore
{
    private const int CurrentSchemaVersion = 1;
    private const string JournalSuffix = ".update.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowDuplicateProperties = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private readonly string _transactionsRoot;

    public ManagedUpdateJournalStore(IInstallationStateStore stateStore)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        _transactionsRoot = Path.GetFullPath(
            Path.Combine(stateStore.DataRoot, "state", "transactions"));
    }

    public async Task SaveAsync(
        ManagedUpdateJournal journal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ValidateJournal(journal, journal.ProductId);

        Directory.CreateDirectory(_transactionsRoot);
        var path = GetJournalPath(journal.ProductId);
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
                    journal,
                    SerializerOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            try
            {
                File.Move(temporaryPath, path, overwrite: false);
            }
            catch (IOException exception) when (File.Exists(path))
            {
                throw new InstallationStateException(
                    $"A managed update journal for '{journal.ProductId}' already exists.",
                    exception);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<IReadOnlyList<ManagedUpdateJournal>> LoadAllAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_transactionsRoot))
        {
            return [];
        }

        var journals = new List<ManagedUpdateJournal>();
        foreach (var path in Directory
                     .EnumerateFiles(_transactionsRoot, $"*{JournalSuffix}", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(path);
            if (!fileName.EndsWith(JournalSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var expectedProductId = fileName[..^JournalSuffix.Length];
            InstallationStateStore.SanitizeProductId(expectedProductId);

            journals.Add(await LoadCoreAsync(path, expectedProductId, cancellationToken));
        }

        return journals;
    }

    public async Task<ManagedUpdateJournal?> LoadAsync(
        string productId,
        CancellationToken cancellationToken = default)
    {
        var path = GetJournalPath(productId);
        if (!File.Exists(path))
        {
            return null;
        }

        return await LoadCoreAsync(path, productId, cancellationToken);
    }

    public void Delete(string productId)
    {
        var path = GetJournalPath(productId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string GetJournalPath(string productId) =>
        Path.Combine(
            _transactionsRoot,
            $"{InstallationStateStore.SanitizeProductId(productId)}{JournalSuffix}");

    private static async Task<ManagedUpdateJournal> LoadCoreAsync(
        string path,
        string expectedProductId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16384,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var journal = await JsonSerializer.DeserializeAsync<ManagedUpdateJournal>(
                stream,
                SerializerOptions,
                cancellationToken);
            ValidateJournal(journal, expectedProductId);
            return journal!;
        }
        catch (JsonException exception)
        {
            throw new InstallationStateException(
                $"Managed update journal for '{expectedProductId}' contains invalid JSON.",
                exception);
        }
    }

    private static void ValidateJournal(
        ManagedUpdateJournal? journal,
        string expectedProductId)
    {
        InstallationStateStore.SanitizeProductId(expectedProductId);
        if (journal is null ||
            journal.SchemaVersion != CurrentSchemaVersion ||
            !string.Equals(journal.ProductId, expectedProductId, StringComparison.Ordinal) ||
            !IsOperationId(journal.OperationId) ||
            journal.OldState is null ||
            journal.NewState is null)
        {
            throw new InstallationStateException(
                $"Managed update journal for '{expectedProductId}' is invalid.");
        }

        InstallationStateStore.ValidateState(journal.OldState, expectedProductId);
        InstallationStateStore.ValidateState(journal.NewState, expectedProductId);

        if (!PathsEqual(journal.OldState.GameRoot, journal.NewState.GameRoot))
        {
            throw new InstallationStateException(
                $"Managed update journal for '{expectedProductId}' changes the game root.");
        }

        if (!SemanticVersionComparer.TryCompare(
                journal.NewState.Version,
                journal.OldState.Version,
                out _) ||
            journal.NewState.Version.Equals(
                journal.OldState.Version,
                StringComparison.Ordinal))
        {
            throw new InstallationStateException(
                $"Managed update journal for '{expectedProductId}' does not switch semantic versions.");
        }
    }

    private static bool IsOperationId(string? operationId) =>
        operationId is { Length: 32 } && operationId.All(Uri.IsHexDigit);

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return Path.GetFullPath(left).Equals(
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InstallationStateException(
                "Managed update journal contains an invalid game root.",
                exception);
        }
    }

}
