using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nfg.Store.Installation;

internal sealed record ManagedStateMutationJournal
{
    public const int CurrentSchemaVersion = 1;
    public const string SetEnabledKind = "set-enabled";
    public const string UninstallKind = "uninstall";

    public required int SchemaVersion { get; init; }

    public required string OperationId { get; init; }

    public required string InstallationKey { get; init; }

    public required string Kind { get; init; }

    public required InstalledProductState OldState { get; init; }

    public required InstalledProductState? NewState { get; init; }
}

internal sealed class ManagedStateMutationJournalStore
{
    private const string JournalSuffix = ".mutation.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowDuplicateProperties = false,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private readonly string _transactionsRoot;

    public ManagedStateMutationJournalStore(IInstallationStateStore stateStore)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        _transactionsRoot = Path.GetFullPath(
            Path.Combine(stateStore.DataRoot, "state", "transactions"));
    }

    public async Task SaveAsync(
        ManagedStateMutationJournal journal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ValidateJournal(journal, journal.InstallationKey);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, SerializerOptions);
        var path = GetJournalPath(journal.InstallationKey);

        try
        {
            await InstallationStateStore.WriteBytesAtomicAsync(
                path,
                bytes,
                overwrite: false,
                cancellationToken);
        }
        catch (IOException exception) when (File.Exists(path))
        {
            throw new InstallationStateException(
                $"A managed state mutation journal for '{journal.InstallationKey}' already exists.",
                exception);
        }
    }

    public async Task<IReadOnlyList<ManagedStateMutationJournal>> LoadAllAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_transactionsRoot))
        {
            return [];
        }

        var journals = new List<ManagedStateMutationJournal>();
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

            var expectedInstallationKey = fileName[..^JournalSuffix.Length];
            InstallationStateStore.SanitizeInstallationKey(expectedInstallationKey);
            journals.Add(await LoadCoreAsync(path, expectedInstallationKey, cancellationToken));
        }

        return journals;
    }

    public async Task<ManagedStateMutationJournal?> LoadAsync(
        string installationKey,
        CancellationToken cancellationToken = default)
    {
        var path = GetJournalPath(installationKey);
        if (!File.Exists(path))
        {
            return null;
        }

        return await LoadCoreAsync(path, installationKey, cancellationToken);
    }

    public void Delete(string installationKey)
    {
        var path = GetJournalPath(installationKey);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string GetJournalPath(string installationKey) =>
        Path.Combine(
            _transactionsRoot,
            $"{InstallationStateStore.SanitizeInstallationKey(installationKey)}{JournalSuffix}");

    private static async Task<ManagedStateMutationJournal> LoadCoreAsync(
        string path,
        string expectedInstallationKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var journal = JsonSerializer.Deserialize<ManagedStateMutationJournal>(
                bytes,
                SerializerOptions);
            ValidateJournal(journal, expectedInstallationKey);
            return journal!;
        }
        catch (JsonException exception)
        {
            throw new InstallationStateException(
                $"Managed state mutation journal for '{expectedInstallationKey}' contains invalid JSON.",
                exception);
        }
    }

    private static void ValidateJournal(
        ManagedStateMutationJournal? journal,
        string expectedInstallationKey)
    {
        InstallationStateStore.SanitizeInstallationKey(expectedInstallationKey);
        if (journal is null ||
            journal.SchemaVersion != ManagedStateMutationJournal.CurrentSchemaVersion ||
            !string.Equals(
                journal.InstallationKey,
                expectedInstallationKey,
                StringComparison.Ordinal) ||
            !IsOperationId(journal.OperationId) ||
            journal.OldState is null ||
            journal.OldState.SchemaVersion != 2 ||
            !IsSteamAppId(journal.OldState.SteamAppId) ||
            !string.Equals(
                journal.OldState.InstallationKey,
                expectedInstallationKey,
                StringComparison.Ordinal))
        {
            throw InvalidJournal(expectedInstallationKey);
        }

        InstallationStateStore.ValidateState(journal.OldState, expectedInstallationKey);
        switch (journal.Kind)
        {
            case ManagedStateMutationJournal.SetEnabledKind:
                ValidateSetEnabledTransition(journal, expectedInstallationKey);
                break;
            case ManagedStateMutationJournal.UninstallKind:
                if (journal.NewState is not null)
                {
                    throw InvalidJournal(expectedInstallationKey);
                }

                break;
            default:
                throw InvalidJournal(expectedInstallationKey);
        }
    }

    private static void ValidateSetEnabledTransition(
        ManagedStateMutationJournal journal,
        string expectedInstallationKey)
    {
        var newState = journal.NewState;
        if (newState is null ||
            newState.SchemaVersion != 2 ||
            !string.Equals(
                newState.InstallationKey,
                expectedInstallationKey,
                StringComparison.Ordinal))
        {
            throw InvalidJournal(expectedInstallationKey);
        }

        InstallationStateStore.ValidateState(newState, expectedInstallationKey);
        if (journal.OldState.IsEnabled == newState.IsEnabled ||
            !journal.OldState.SteamAppId.Equals(newState.SteamAppId, StringComparison.Ordinal) ||
            !InstallationStateMigrator.StatesEqual(
                journal.OldState with { IsEnabled = newState.IsEnabled },
                newState))
        {
            throw InvalidJournal(expectedInstallationKey);
        }
    }

    private static InstallationStateException InvalidJournal(string installationKey) =>
        new($"Managed state mutation journal for '{installationKey}' is invalid.");

    private static bool IsOperationId(string? operationId) =>
        operationId is { Length: 32 } && operationId.All(Uri.IsHexDigit);

    private static bool IsSteamAppId(string? steamAppId) =>
        !string.IsNullOrWhiteSpace(steamAppId) && steamAppId.All(char.IsAsciiDigit);
}
