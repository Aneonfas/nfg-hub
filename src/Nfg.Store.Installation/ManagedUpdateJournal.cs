using System.Text.Json;
using System.Text.Json.Serialization;
using Nfg.Store.Core;

namespace Nfg.Store.Installation;

internal sealed record ManagedUpdateJournal
{
    public required int SchemaVersion { get; init; }

    public required string OperationId { get; init; }

    /// <summary>
    /// Schema-v2 installation-slot identity. Schema-v1 journals omit this field.
    /// </summary>
    public string? InstallationKey { get; init; }

    /// <summary>
    /// Legacy schema-v1 top-level identity. Schema-v2 journals omit this field.
    /// </summary>
    public string? ProductId { get; init; }

    public required InstalledProductState? OldState { get; init; }

    public required InstalledProductState NewState { get; init; }

    public IReadOnlyList<string> PreservedDestinations { get; init; } = [];

    public bool CleanupPrepared { get; init; }

    /// <summary>
    /// Schema-v1 recovery has fully restored and verified the old installation. While this
    /// flag is durable, recovery may only remove its own rollback markers and the journal.
    /// </summary>
    public bool RollbackComplete { get; init; }

    [JsonIgnore]
    public string EffectiveInstallationKey => SchemaVersion switch
    {
        1 when ProductId is not null => ProductId,
        2 when InstallationKey is not null => InstallationKey,
        _ => throw new InstallationStateException(
            "Managed update journal does not declare a valid installation identity.")
    };
}

internal sealed class ManagedUpdateJournalStore
{
    private const int LegacySchemaVersion = 1;
    private const int CurrentSchemaVersion = 2;
    private const string JournalSuffix = ".update.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowDuplicateProperties = false,
        PropertyNameCaseInsensitive = false,
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
        if (journal.SchemaVersion != CurrentSchemaVersion ||
            journal.InstallationKey is null ||
            journal.CleanupPrepared ||
            journal.RollbackComplete)
        {
            throw new InstallationStateException(
                "Only schema-v2 managed update journals can be committed.");
        }

        ValidateCurrentJournal(journal, journal.InstallationKey);
        var document = new ManagedUpdateJournalV2
        {
            SchemaVersion = CurrentSchemaVersion,
            OperationId = journal.OperationId,
            InstallationKey = journal.InstallationKey,
            OldState = journal.OldState,
            NewState = journal.NewState,
            PreservedDestinations = journal.PreservedDestinations,
            CleanupPrepared = journal.CleanupPrepared
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, SerializerOptions);
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
                $"A managed update journal for '{journal.InstallationKey}' already exists.",
                exception);
        }
    }

    public async Task<ManagedUpdateJournal> MarkCleanupPreparedAsync(
        ManagedUpdateJournal journal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (journal.SchemaVersion != CurrentSchemaVersion ||
            journal.CleanupPrepared ||
            journal.OldState is null)
        {
            throw new InstallationStateException(
                "Only an unprepared schema-v2 replacement journal can enter cleanup phase.");
        }

        var current = await LoadAsync(journal.EffectiveInstallationKey, cancellationToken)
            ?? throw new InstallationStateException(
                $"Managed update journal for '{journal.EffectiveInstallationKey}' disappeared.");
        if (current.CleanupPrepared ||
            current.OldState is null ||
            !current.OperationId.Equals(journal.OperationId, StringComparison.Ordinal) ||
            !InstallationStateMigrator.StatesEqual(current.OldState, journal.OldState) ||
            !InstallationStateMigrator.StatesEqual(current.NewState, journal.NewState) ||
            !current.PreservedDestinations.SequenceEqual(
                journal.PreservedDestinations,
                StringComparer.Ordinal))
        {
            throw new InstallationStateException(
                $"Managed update journal for '{journal.EffectiveInstallationKey}' changed before cleanup.");
        }

        var prepared = journal with { CleanupPrepared = true };
        ValidateCurrentJournal(prepared, prepared.EffectiveInstallationKey);
        var document = new ManagedUpdateJournalV2
        {
            SchemaVersion = CurrentSchemaVersion,
            OperationId = prepared.OperationId,
            InstallationKey = prepared.InstallationKey!,
            OldState = prepared.OldState,
            NewState = prepared.NewState,
            PreservedDestinations = prepared.PreservedDestinations,
            CleanupPrepared = true
        };
        await InstallationStateStore.WriteBytesAtomicAsync(
            GetJournalPath(prepared.EffectiveInstallationKey),
            JsonSerializer.SerializeToUtf8Bytes(document, SerializerOptions),
            overwrite: true,
            cancellationToken);
        return prepared;
    }

    public async Task<ManagedUpdateJournal> MarkRollbackCompleteAsync(
        ManagedUpdateJournal journal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (journal.SchemaVersion != LegacySchemaVersion ||
            journal.RollbackComplete ||
            journal.CleanupPrepared ||
            journal.ProductId is null ||
            journal.OldState is null)
        {
            throw new InstallationStateException(
                "Only an incomplete schema-v1 journal can enter completed rollback cleanup.");
        }

        var current = await LoadAsync(journal.EffectiveInstallationKey, cancellationToken)
            ?? throw new InstallationStateException(
                $"Managed update journal for '{journal.EffectiveInstallationKey}' disappeared.");
        if (current.SchemaVersion != LegacySchemaVersion ||
            current.RollbackComplete ||
            current.CleanupPrepared ||
            !current.OperationId.Equals(journal.OperationId, StringComparison.Ordinal) ||
            !string.Equals(current.ProductId, journal.ProductId, StringComparison.Ordinal) ||
            !InstallationStateMigrator.StatesEqual(current.OldState!, journal.OldState) ||
            !InstallationStateMigrator.StatesEqual(current.NewState, journal.NewState))
        {
            throw new InstallationStateException(
                $"Managed update journal for '{journal.EffectiveInstallationKey}' changed before rollback cleanup.");
        }

        var completed = journal with { RollbackComplete = true };
        ValidateLegacyJournal(completed, completed.EffectiveInstallationKey);
        var document = new ManagedUpdateJournalV1
        {
            SchemaVersion = LegacySchemaVersion,
            OperationId = completed.OperationId,
            ProductId = completed.ProductId!,
            OldState = completed.OldState,
            NewState = completed.NewState,
            RollbackComplete = true
        };
        await InstallationStateStore.WriteBytesAtomicAsync(
            GetJournalPath(completed.EffectiveInstallationKey),
            JsonSerializer.SerializeToUtf8Bytes(document, SerializerOptions),
            overwrite: true,
            cancellationToken);
        return completed;
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

            var expectedInstallationKey = fileName[..^JournalSuffix.Length];
            InstallationStateStore.SanitizeInstallationKey(expectedInstallationKey);
            journals.Add(await LoadCoreAsync(path, expectedInstallationKey, cancellationToken));
        }

        return journals;
    }

    public async Task<ManagedUpdateJournal?> LoadAsync(
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

    private static async Task<ManagedUpdateJournal> LoadCoreAsync(
        string path,
        string expectedInstallationKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var journal = DeserializeJournal(bytes, expectedInstallationKey);
            ValidateJournal(journal, expectedInstallationKey);
            return journal;
        }
        catch (JsonException exception)
        {
            throw new InstallationStateException(
                $"Managed update journal for '{expectedInstallationKey}' contains invalid JSON.",
                exception);
        }
    }

    private static ManagedUpdateJournal DeserializeJournal(
        ReadOnlyMemory<byte> bytes,
        string expectedInstallationKey)
    {
        using var json = JsonDocument.Parse(bytes);
        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            !json.RootElement.TryGetProperty("schemaVersion", out var schemaVersionElement) ||
            !schemaVersionElement.TryGetInt32(out var schemaVersion))
        {
            throw new InstallationStateException(
                $"Managed update journal for '{expectedInstallationKey}' is invalid.");
        }

        return schemaVersion switch
        {
            LegacySchemaVersion => ConvertLegacyDocument(
                JsonSerializer.Deserialize<ManagedUpdateJournalV1>(bytes.Span, SerializerOptions)),
            CurrentSchemaVersion => ConvertCurrentDocument(
                JsonSerializer.Deserialize<ManagedUpdateJournalV2>(bytes.Span, SerializerOptions)),
            _ => throw new InstallationStateException(
                $"Managed update journal for '{expectedInstallationKey}' uses unsupported schema " +
                $"version {schemaVersion}.")
        };
    }

    private static ManagedUpdateJournal ConvertLegacyDocument(ManagedUpdateJournalV1? document)
    {
        if (document is null)
        {
            throw new InstallationStateException("Managed update journal is empty or invalid.");
        }

        return new ManagedUpdateJournal
        {
            SchemaVersion = document.SchemaVersion,
            OperationId = document.OperationId,
            ProductId = document.ProductId,
            OldState = document.OldState,
            NewState = document.NewState,
            PreservedDestinations = [],
            CleanupPrepared = false,
            RollbackComplete = document.RollbackComplete
        };
    }

    private static ManagedUpdateJournal ConvertCurrentDocument(ManagedUpdateJournalV2? document)
    {
        if (document is null)
        {
            throw new InstallationStateException("Managed update journal is empty or invalid.");
        }

        return new ManagedUpdateJournal
        {
            SchemaVersion = document.SchemaVersion,
            OperationId = document.OperationId,
            InstallationKey = document.InstallationKey,
            OldState = document.OldState,
            NewState = document.NewState,
            PreservedDestinations = document.PreservedDestinations,
            CleanupPrepared = document.CleanupPrepared,
            RollbackComplete = false
        };
    }

    private static void ValidateJournal(
        ManagedUpdateJournal? journal,
        string expectedInstallationKey)
    {
        InstallationStateStore.SanitizeInstallationKey(expectedInstallationKey);
        switch (journal?.SchemaVersion)
        {
            case LegacySchemaVersion:
                ValidateLegacyJournal(journal, expectedInstallationKey);
                return;
            case CurrentSchemaVersion:
                ValidateCurrentJournal(journal, expectedInstallationKey);
                return;
            default:
                throw new InstallationStateException(
                    $"Managed update journal for '{expectedInstallationKey}' is invalid.");
        }
    }

    private static void ValidateLegacyJournal(
        ManagedUpdateJournal journal,
        string expectedProductId)
    {
        if (journal.InstallationKey is not null ||
            !string.Equals(journal.ProductId, expectedProductId, StringComparison.Ordinal) ||
            !IsOperationId(journal.OperationId) ||
            journal.OldState is null ||
            journal.NewState is null ||
            journal.PreservedDestinations is not { Count: 0 } ||
            journal.CleanupPrepared ||
            journal.OldState.SchemaVersion != LegacySchemaVersion ||
            journal.NewState.SchemaVersion != LegacySchemaVersion)
        {
            throw new InstallationStateException(
                $"Managed update journal for '{expectedProductId}' is invalid.");
        }

        InstallationStateStore.ValidateState(journal.OldState, expectedProductId);
        InstallationStateStore.ValidateState(journal.NewState, expectedProductId);
        ValidateTransition(journal, expectedProductId, requireSameProductVersionChange: true);
    }

    private static void ValidateCurrentJournal(
        ManagedUpdateJournal journal,
        string expectedInstallationKey)
    {
        if (journal.ProductId is not null ||
            !string.Equals(
                journal.InstallationKey,
                expectedInstallationKey,
                StringComparison.Ordinal) ||
            !IsOperationId(journal.OperationId) ||
            journal.NewState is null ||
            journal.NewState.SchemaVersion != CurrentSchemaVersion ||
            !string.Equals(
                journal.NewState.InstallationKey,
                expectedInstallationKey,
                StringComparison.Ordinal) ||
            journal.OldState is { SchemaVersion: not CurrentSchemaVersion } ||
            journal.OldState is not null &&
            !string.Equals(
                journal.OldState.InstallationKey,
                expectedInstallationKey,
                StringComparison.Ordinal) ||
            journal.PreservedDestinations is null ||
            journal.CleanupPrepared && journal.OldState is null ||
            journal.RollbackComplete)
        {
            throw new InstallationStateException(
                $"Managed update journal for '{expectedInstallationKey}' is invalid.");
        }

        InstallationStateStore.ValidateState(journal.NewState, expectedInstallationKey);
        if (journal.OldState is not null)
        {
            InstallationStateStore.ValidateState(journal.OldState, expectedInstallationKey);
        }

        ValidatePreservedDestinations(journal, expectedInstallationKey);
        ValidateTransition(journal, expectedInstallationKey, requireSameProductVersionChange: true);
    }

    private static void ValidatePreservedDestinations(
        ManagedUpdateJournal journal,
        string expectedInstallationKey)
    {
        if (journal.OldState is not null && journal.PreservedDestinations.Count != 0)
        {
            throw new InstallationStateException(
                $"Managed update journal for '{expectedInstallationKey}' preserves fresh-install " +
                "destinations while replacing an existing installation.");
        }

        var newDestinations = journal.NewState.Files
            .Select(file => file.Destination)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var destination in journal.PreservedDestinations)
        {
            if (string.IsNullOrWhiteSpace(destination) ||
                !newDestinations.Contains(destination) ||
                !preserved.Add(destination))
            {
                throw new InstallationStateException(
                    $"Managed update journal for '{expectedInstallationKey}' contains an invalid " +
                    "preserved destination.");
            }
        }
    }

    private static void ValidateTransition(
        ManagedUpdateJournal journal,
        string expectedInstallationKey,
        bool requireSameProductVersionChange)
    {
        if (!IsSteamAppId(journal.NewState.SteamAppId))
        {
            throw new InstallationStateException(
                $"Managed update journal for '{expectedInstallationKey}' has an invalid Steam App ID.");
        }

        if (journal.OldState is null)
        {
            if (!journal.NewState.IsEnabled)
            {
                throw new InstallationStateException(
                    $"Fresh installation journal for '{expectedInstallationKey}' is disabled.");
            }

            return;
        }

        if (!IsSteamAppId(journal.OldState.SteamAppId) ||
            !journal.OldState.SteamAppId.Equals(
                journal.NewState.SteamAppId,
                StringComparison.Ordinal) ||
            journal.OldState.IsEnabled != journal.NewState.IsEnabled ||
            !PathsEqual(journal.OldState.GameRoot, journal.NewState.GameRoot))
        {
            throw new InstallationStateException(
                $"Managed update journal for '{expectedInstallationKey}' changes slot invariants.");
        }

        if (requireSameProductVersionChange &&
            journal.OldState.ProductId.Equals(
                journal.NewState.ProductId,
                StringComparison.Ordinal) &&
            (!SemanticVersionComparer.TryCompare(
                 journal.NewState.Version,
                 journal.OldState.Version,
                 out _) ||
             journal.NewState.Version.Equals(
                 journal.OldState.Version,
                 StringComparison.Ordinal)))
        {
            throw new InstallationStateException(
                $"Managed update journal for '{expectedInstallationKey}' does not switch semantic versions.");
        }
    }

    private static bool IsOperationId(string? operationId) =>
        operationId is { Length: 32 } && operationId.All(Uri.IsHexDigit);

    private static bool IsSteamAppId(string? steamAppId) =>
        !string.IsNullOrWhiteSpace(steamAppId) && steamAppId.All(char.IsAsciiDigit);

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

    private sealed record ManagedUpdateJournalV1
    {
        public required int SchemaVersion { get; init; }

        public required string OperationId { get; init; }

        public required string ProductId { get; init; }

        public required InstalledProductState OldState { get; init; }

        public required InstalledProductState NewState { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool RollbackComplete { get; init; }
    }

    private sealed record ManagedUpdateJournalV2
    {
        public required int SchemaVersion { get; init; }

        public required string OperationId { get; init; }

        public required string InstallationKey { get; init; }

        public required InstalledProductState? OldState { get; init; }

        public required InstalledProductState NewState { get; init; }

        public required IReadOnlyList<string> PreservedDestinations { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool CleanupPrepared { get; init; }
    }
}
