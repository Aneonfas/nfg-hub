using System.Text.Json;
using System.Text.Json.Serialization;
using Nfg.Store.Installation;

internal static class ManagedUpdateJournalV2Smoke
{
    private const string InstallationKey = "nfg.anvil-empires.ru";
    private const string RuProductId = "nfg.anvil-empires.ru";
    private const string EsProductId = "nfg.anvil-empires.es";
    private const string Destination = "Anvil/Content/Paks/Test-Language.pak";

    private static readonly JsonSerializerOptions LegacySerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task RunAsync(string testRoot)
    {
        await CheckSchemaV2RoundTripAsync(Path.Combine(testRoot, "journal-v2-roundtrip"));
        await CheckSchemaV2VariantTransitionAsync(Path.Combine(testRoot, "journal-v2-variant"));
        await CheckLegacySchemaV1LoadAsync(Path.Combine(testRoot, "journal-v1-load"));
    }

    private static async Task CheckSchemaV2RoundTripAsync(string dataRoot)
    {
        var stateStore = new InstallationStateStore(dataRoot);
        var journalStore = new ManagedUpdateJournalStore(stateStore);
        var newState = CreateState(
            schemaVersion: 2,
            installationKey: InstallationKey,
            productId: RuProductId,
            version: "1.0.0",
            gameRoot: Path.Combine(dataRoot, "game"));
        var journal = new ManagedUpdateJournal
        {
            SchemaVersion = 2,
            OperationId = new string('a', 32),
            InstallationKey = InstallationKey,
            OldState = null,
            NewState = newState,
            PreservedDestinations = [Destination]
        };

        await journalStore.SaveAsync(journal);
        var path = GetJournalPath(dataRoot, InstallationKey);
        using (var json = JsonDocument.Parse(await File.ReadAllBytesAsync(path)))
        {
            var properties = json.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal);
            Assert(properties.SetEquals(
                [
                    "schemaVersion",
                    "operationId",
                    "installationKey",
                    "oldState",
                    "newState",
                    "preservedDestinations"
                ]), "Schema-v2 journal persisted a mixed or incomplete top-level shape.");
            Assert(
                json.RootElement.GetProperty("oldState").ValueKind == JsonValueKind.Null,
                "Fresh-install journal did not persist an explicit null oldState.");
        }

        var loaded = await journalStore.LoadAsync(InstallationKey)
            ?? throw new InvalidOperationException("Schema-v2 journal was not reloaded.");
        Assert(loaded.SchemaVersion == 2, "Schema-v2 journal changed schema during reload.");
        Assert(
            loaded.EffectiveInstallationKey == InstallationKey,
            "Schema-v2 journal exposed the wrong effective installation key.");
        Assert(loaded.ProductId is null, "Schema-v2 journal retained a legacy top-level ProductId.");
        Assert(loaded.OldState is null, "Fresh-install journal unexpectedly gained an old state.");
        Assert(
            loaded.PreservedDestinations.SequenceEqual([Destination], StringComparer.Ordinal),
            "Schema-v2 preserved destinations did not round-trip.");

        await AssertThrowsAsync<InstallationStateException>(
            () => journalStore.SaveAsync(journal),
            "A pending schema-v2 journal was overwritten.");
    }

    private static async Task CheckSchemaV2VariantTransitionAsync(string dataRoot)
    {
        var journalStore = new ManagedUpdateJournalStore(new InstallationStateStore(dataRoot));
        var gameRoot = Path.Combine(dataRoot, "game");
        var oldRu = CreateState(2, InstallationKey, RuProductId, "1.0.0", gameRoot);
        var newEs = CreateState(2, InstallationKey, EsProductId, "1.0.0", gameRoot);
        var variantSwitch = new ManagedUpdateJournal
        {
            SchemaVersion = 2,
            OperationId = new string('b', 32),
            InstallationKey = InstallationKey,
            OldState = oldRu,
            NewState = newEs,
            PreservedDestinations = []
        };

        await journalStore.SaveAsync(variantSwitch);
        var loaded = await journalStore.LoadAsync(InstallationKey)
            ?? throw new InvalidOperationException("Variant-switch journal was not reloaded.");
        Assert(
            loaded.OldState?.ProductId == RuProductId && loaded.NewState.ProductId == EsProductId,
            "Variant-switch product identities did not round-trip.");
        Assert(
            loaded.OldState!.Version == loaded.NewState.Version,
            "The same SemVer variant transition was unexpectedly changed.");

        journalStore.Delete(InstallationKey);
        var sameProductSameVersion = variantSwitch with
        {
            OperationId = new string('c', 32),
            NewState = oldRu with { InstalledAt = oldRu.InstalledAt.AddSeconds(1) }
        };
        await AssertThrowsAsync<InstallationStateException>(
            () => journalStore.SaveAsync(sameProductSameVersion),
            "A same-product journal without a version transition was accepted.");
        Assert(
            !File.Exists(GetJournalPath(dataRoot, InstallationKey)),
            "Rejected same-product journal left a persisted transaction.");
    }

    private static async Task CheckLegacySchemaV1LoadAsync(string dataRoot)
    {
        var gameRoot = Path.Combine(dataRoot, "game");
        var oldState = CreateState(1, installationKey: null, RuProductId, "1.0.0", gameRoot);
        var newState = CreateState(1, installationKey: null, RuProductId, "1.1.0", gameRoot);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                schemaVersion = 1,
                operationId = new string('d', 32),
                productId = RuProductId,
                oldState,
                newState
            },
            LegacySerializerOptions);
        var path = GetJournalPath(dataRoot, RuProductId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);

        var journalStore = new ManagedUpdateJournalStore(new InstallationStateStore(dataRoot));
        var loaded = await journalStore.LoadAsync(RuProductId)
            ?? throw new InvalidOperationException("Legacy schema-v1 journal was not loaded.");
        Assert(loaded.SchemaVersion == 1, "Legacy journal did not retain schema version 1.");
        Assert(loaded.ProductId == RuProductId, "Legacy top-level ProductId was not retained.");
        Assert(loaded.InstallationKey is null, "Legacy journal unexpectedly gained a v2 field.");
        Assert(
            loaded.EffectiveInstallationKey == RuProductId,
            "Legacy journal exposed the wrong effective installation key.");
        Assert(
            loaded.OldState?.SchemaVersion == 1 && loaded.NewState.SchemaVersion == 1,
            "Legacy journal states were not retained as schema v1.");
        Assert(
            loaded.PreservedDestinations.Count == 0,
            "Legacy journal unexpectedly gained preserved destinations.");

        journalStore.Delete(RuProductId);
        await AssertThrowsAsync<InstallationStateException>(
            () => journalStore.SaveAsync(loaded),
            "ManagedUpdateJournalStore.SaveAsync accepted a legacy journal.");
    }

    private static InstalledProductState CreateState(
        int schemaVersion,
        string? installationKey,
        string productId,
        string version,
        string gameRoot) => new()
        {
            SchemaVersion = schemaVersion,
            InstallationKey = installationKey,
            ProductId = productId,
            Version = version,
            PackageSizeBytes = 128,
            PackageSha256 = new string('a', 64),
            SteamAppId = "1373910",
            SteamBuildId = "24370001",
            DetectedSteamBuildId = "24378492",
            GameRoot = gameRoot,
            InstalledAt = DateTimeOffset.Parse("2026-08-10T12:00:00+03:00"),
            IsEnabled = true,
            Files =
            [
                new InstalledFileState
                {
                    Destination = Destination,
                    SizeBytes = 64,
                    Sha256 = new string('b', 64)
                }
            ]
        };

    private static string GetJournalPath(string dataRoot, string installationKey) =>
        Path.Combine(
            dataRoot,
            "state",
            "transactions",
            $"{installationKey}.update.json");

    private static async Task AssertThrowsAsync<TException>(
        Func<Task> action,
        string message)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
