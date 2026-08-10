using System.Security.Cryptography;
using System.Text;
using Nfg.Store.Contracts;
using Nfg.Store.Core;
using Nfg.Store.Installation;

internal static class InstallationStateV2Smoke
{
    private const string RuProductId = "nfg.anvil-empires.ru";
    private const string EsProductId = "nfg.anvil-empires.es";
    private const string ForgeProductId = "nfg.anvil-empires.forge-helper";
    private const string AnvilFamilyId = "nfg.anvil-empires.localization";
    private const string AnvilInstallationKey = RuProductId;
    private const string PakDestination =
        "Anvil/Content/Paks/AnvilEmpires-WindowsNoEditor_Russian.pak";

    public static async Task RunAsync(string testRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(testRoot);

        CheckInstallationKeyDerivation();
        await CheckLiteralRuAndForgeMigrationAsync(
            Path.Combine(testRoot, "installation-state-v2-literal"));
        await CheckInterruptedMigrationCasesAsync(
            Path.Combine(testRoot, "installation-state-v2-interrupted"));
        await CheckFailClosedMigrationCasesAsync(
            Path.Combine(testRoot, "installation-state-v2-fail-closed"));
    }

    private static void CheckInstallationKeyDerivation()
    {
        var legacyRu = CreateProduct(RuProductId, schemaVersion: 1);
        var groupedRu = CreateVariantProduct(RuProductId, "ru");
        var groupedEs = CreateVariantProduct(EsProductId, "es");
        var forge = CreateProduct(ForgeProductId, schemaVersion: 2);

        Assert(
            ProductInstallationKey.FromManifest(legacyRu) == RuProductId,
            "Schema-v1 product did not derive installationKey=productId.");
        Assert(
            ProductInstallationKey.FromManifest(groupedRu) == AnvilInstallationKey,
            "Grouped RU product did not derive installationKey=exclusiveGroup.");
        Assert(
            ProductInstallationKey.FromManifest(groupedEs) == AnvilInstallationKey,
            "Grouped ES product did not share the RU installation key.");
        Assert(
            ProductInstallationKey.FromManifest(forge) == ForgeProductId,
            "Ungrouped Forge product did not retain an independent installation key.");
    }

    private static async Task CheckLiteralRuAndForgeMigrationAsync(string dataRoot)
    {
        var gameRoot = Path.Combine(dataRoot, "Anvil Empires");
        var pakPath = ResolveDestination(gameRoot, PakDestination);
        var forgeDestination = "Anvil/Binaries/Win64/NfgForgeHelper.dll";
        var forgePath = ResolveDestination(gameRoot, forgeDestination);
        var pakBytes = Encoding.UTF8.GetBytes("realistic RU PAK fixture - do not mutate");
        var forgeBytes = Encoding.UTF8.GetBytes("independent Forge fixture - do not mutate");
        await WriteFixtureFileAsync(pakPath, pakBytes);
        await WriteFixtureFileAsync(forgePath, forgeBytes);

        var ruBytes = CreateLiteralLegacyState(
            RuProductId,
            gameRoot,
            PakDestination,
            pakBytes,
            version: "1.4.2",
            detectedSteamBuildId: "24378492",
            includeIsEnabled: false,
            isEnabled: true);
        var forgeStateBytes = CreateLiteralLegacyState(
            ForgeProductId,
            gameRoot,
            forgeDestination,
            forgeBytes,
            version: "2.1.0",
            detectedSteamBuildId: "24378492",
            includeIsEnabled: true,
            isEnabled: false);
        var ruStatePath = GetStatePath(dataRoot, RuProductId);
        var forgeStatePath = GetStatePath(dataRoot, ForgeProductId);
        await WriteFixtureFileAsync(ruStatePath, ruBytes);
        await WriteFixtureFileAsync(forgeStatePath, forgeStateBytes);

        var catalog = CreateCatalog(
            CreateVariantProduct(RuProductId, "ru"),
            CreateVariantProduct(EsProductId, "es"),
            CreateProduct(ForgeProductId, schemaVersion: 2));
        var store = new InstallationStateStore(dataRoot);
        var migrated = await new InstallationStateMigrator(store).MigrateAsync(catalog);

        Assert(migrated.Count == 2, "RU and Forge migration returned an unexpected state count.");
        var ru = await store.LoadAsync(AnvilInstallationKey)
            ?? throw new InvalidOperationException("Migrated RU state was not found.");
        Assert(ru.SchemaVersion == 2, "RU state did not migrate to schema v2.");
        Assert(ru.InstallationKey == AnvilInstallationKey, "RU installation key changed unexpectedly.");
        Assert(ru.ProductId == RuProductId, "RU active product id changed during migration.");
        Assert(ru.Version == "1.4.2", "RU version was not preserved.");
        Assert(ru.PackageSizeBytes == 987654321, "RU package size was not preserved.");
        Assert(ru.PackageSha256 == new string('a', 64), "RU package hash was not preserved.");
        Assert(ru.SteamAppId == "1373910", "RU Steam AppID was not preserved.");
        Assert(ru.SteamBuildId == "24370001", "RU tested Steam build was not preserved.");
        Assert(ru.DetectedSteamBuildId == "24378492", "RU detected Steam build was not preserved.");
        Assert(
            Path.GetFullPath(ru.GameRoot) == Path.GetFullPath(gameRoot),
            "RU game root was not preserved.");
        Assert(
            ru.InstalledAt == DateTimeOffset.Parse("2026-07-18T19:20:21+03:00"),
            "RU installation timestamp was not preserved.");
        Assert(ru.IsEnabled, "Missing schema-v1 isEnabled did not default to true.");
        Assert(ru.Files.Count == 1, "RU managed file list changed during migration.");
        Assert(ru.Files[0].Destination == PakDestination, "RU destination was not preserved.");
        Assert(ru.Files[0].SizeBytes == pakBytes.LongLength, "RU file size was not preserved.");
        Assert(
            ru.Files[0].Sha256 == Sha256(pakBytes),
            "RU managed-file hash was not preserved.");

        var forge = await store.LoadAsync(ForgeProductId)
            ?? throw new InvalidOperationException("Migrated Forge state was not found.");
        Assert(forge.SchemaVersion == 2, "Forge state did not migrate to schema v2.");
        Assert(forge.InstallationKey == ForgeProductId, "Forge installation key changed.");
        Assert(forge.ProductId == ForgeProductId, "Forge active product id changed.");
        Assert(!forge.IsEnabled, "Forge disabled state was not preserved.");
        Assert(File.Exists(forgeStatePath), "Forge state was not migrated in place.");

        AssertBytesEqual(
            ruBytes,
            await File.ReadAllBytesAsync(GetBackupPath(dataRoot, RuProductId)),
            "RU schema-v1 backup was not an exact byte copy.");
        AssertBytesEqual(
            forgeStateBytes,
            await File.ReadAllBytesAsync(GetBackupPath(dataRoot, ForgeProductId)),
            "Forge schema-v1 backup was not an exact byte copy.");
        AssertBytesEqual(pakBytes, await File.ReadAllBytesAsync(pakPath), "RU PAK changed during migration.");
        AssertBytesEqual(
            forgeBytes,
            await File.ReadAllBytesAsync(forgePath),
            "Forge payload changed during migration.");

        var ruCurrentBytes = await File.ReadAllBytesAsync(ruStatePath);
        var forgeCurrentBytes = await File.ReadAllBytesAsync(forgeStatePath);
        var repeated = await new InstallationStateMigrator(store).MigrateAsync(catalog);
        Assert(repeated.Count == 2, "Repeated migration returned an unexpected state count.");
        AssertBytesEqual(
            ruCurrentBytes,
            await File.ReadAllBytesAsync(ruStatePath),
            "Repeated migration rewrote the RU schema-v2 state.");
        AssertBytesEqual(
            forgeCurrentBytes,
            await File.ReadAllBytesAsync(forgeStatePath),
            "Repeated migration rewrote the Forge schema-v2 state.");
        AssertBytesEqual(
            ruBytes,
            await File.ReadAllBytesAsync(GetBackupPath(dataRoot, RuProductId)),
            "Repeated migration changed the RU evidence backup.");
        AssertBytesEqual(pakBytes, await File.ReadAllBytesAsync(pakPath), "Repeated migration changed the PAK.");
    }

    private static async Task CheckInterruptedMigrationCasesAsync(string root)
    {
        var catalog = CreateCatalog(
            CreateVariantProduct(RuProductId, "ru"),
            CreateVariantProduct(EsProductId, "es"));

        await CheckBackupAndLegacySourceResumeAsync(Path.Combine(root, "backup-source"), catalog);
        await CheckBackupLegacyAndCurrentResumeAsync(Path.Combine(root, "backup-source-current"), catalog);
        await CheckBackupAndCurrentIdempotenceAsync(Path.Combine(root, "backup-current"), catalog);
    }

    private static async Task CheckBackupAndLegacySourceResumeAsync(
        string dataRoot,
        StoreCatalog catalog)
    {
        var fixture = await CreateSpanishLegacyFixtureAsync(dataRoot);
        await WriteFixtureFileAsync(GetBackupPath(dataRoot, EsProductId), fixture.StateBytes);

        var store = new InstallationStateStore(dataRoot);
        await new InstallationStateMigrator(store).MigrateAsync(catalog);

        Assert(!File.Exists(fixture.SourcePath), "Resumed migration retained the legacy ES source.");
        var current = await store.LoadAsync(AnvilInstallationKey);
        Assert(current?.SchemaVersion == 2, "Resumed migration did not commit schema v2.");
        Assert(current?.ProductId == EsProductId, "Resumed migration changed the active ES variant.");
        AssertBytesEqual(
            fixture.StateBytes,
            await File.ReadAllBytesAsync(GetBackupPath(dataRoot, EsProductId)),
            "Resumed migration changed its existing exact backup.");
        AssertBytesEqual(
            fixture.PakBytes,
            await File.ReadAllBytesAsync(fixture.PakPath),
            "Resumed backup/source migration changed the PAK.");
    }

    private static async Task CheckBackupLegacyAndCurrentResumeAsync(
        string dataRoot,
        StoreCatalog catalog)
    {
        var fixture = await CreateSpanishLegacyFixtureAsync(dataRoot);
        var backupPath = GetBackupPath(dataRoot, EsProductId);
        await WriteFixtureFileAsync(backupPath, fixture.StateBytes);
        var store = new InstallationStateStore(dataRoot);
        var legacy = await store.LoadAsync(EsProductId)
            ?? throw new InvalidOperationException("Interrupted ES legacy fixture was not readable.");
        await store.SaveAsync(legacy with
        {
            SchemaVersion = 2,
            InstallationKey = AnvilInstallationKey
        });
        var currentPath = GetStatePath(dataRoot, AnvilInstallationKey);
        var currentBytes = await File.ReadAllBytesAsync(currentPath);

        await new InstallationStateMigrator(store).MigrateAsync(catalog);

        Assert(!File.Exists(fixture.SourcePath), "Interrupted cleanup retained the legacy ES source.");
        AssertBytesEqual(
            currentBytes,
            await File.ReadAllBytesAsync(currentPath),
            "Interrupted cleanup rewrote the already committed current state.");
        AssertBytesEqual(
            fixture.StateBytes,
            await File.ReadAllBytesAsync(backupPath),
            "Interrupted cleanup changed the exact backup.");
        AssertBytesEqual(
            fixture.PakBytes,
            await File.ReadAllBytesAsync(fixture.PakPath),
            "Interrupted backup/source/current cleanup changed the PAK.");
    }

    private static async Task CheckBackupAndCurrentIdempotenceAsync(
        string dataRoot,
        StoreCatalog catalog)
    {
        var fixture = await CreateSpanishLegacyFixtureAsync(dataRoot);
        var backupPath = GetBackupPath(dataRoot, EsProductId);
        await WriteFixtureFileAsync(backupPath, fixture.StateBytes);
        var store = new InstallationStateStore(dataRoot);
        var legacy = await store.LoadAsync(EsProductId)
            ?? throw new InvalidOperationException("Completed ES legacy fixture was not readable.");
        await store.SaveAsync(legacy with
        {
            SchemaVersion = 2,
            InstallationKey = AnvilInstallationKey
        });
        store.Delete(EsProductId);
        var currentPath = GetStatePath(dataRoot, AnvilInstallationKey);
        var currentBytes = await File.ReadAllBytesAsync(currentPath);

        var states = await new InstallationStateMigrator(store).MigrateAsync(catalog);

        Assert(states.Count == 1, "Completed migration was not idempotent.");
        Assert(states[0].ProductId == EsProductId, "Completed migration changed the active variant.");
        AssertBytesEqual(
            currentBytes,
            await File.ReadAllBytesAsync(currentPath),
            "Backup/current rerun rewrote current state.");
        AssertBytesEqual(
            fixture.StateBytes,
            await File.ReadAllBytesAsync(backupPath),
            "Backup/current rerun changed migration evidence.");
        AssertBytesEqual(
            fixture.PakBytes,
            await File.ReadAllBytesAsync(fixture.PakPath),
            "Backup/current rerun changed the PAK.");
    }

    private static async Task CheckFailClosedMigrationCasesAsync(string root)
    {
        await CheckLegacyCurrentConflictAsync(Path.Combine(root, "legacy-current"));
        await CheckTwoLegacyMembersAsync(Path.Combine(root, "two-legacy"));
        await CheckMalformedStateAsync(Path.Combine(root, "malformed"));
        await CheckAmbiguousCurrentStateAsync(Path.Combine(root, "ambiguous"));
        await CheckNonmatchingBackupAsync(Path.Combine(root, "nonmatching-backup"));
    }

    private static async Task CheckLegacyCurrentConflictAsync(string dataRoot)
    {
        var fixture = await CreateSpanishLegacyFixtureAsync(dataRoot);
        var store = new InstallationStateStore(dataRoot);
        await store.SaveAsync(CreateCurrentState(
            AnvilInstallationKey,
            RuProductId,
            fixture.GameRoot,
            fixture.PakBytes));

        await AssertMigrationFailsWithoutEvidenceChangesAsync(
            dataRoot,
            CreateCatalog(
                CreateVariantProduct(RuProductId, "ru"),
                CreateVariantProduct(EsProductId, "es")),
            fixture.PakPath,
            fixture.PakBytes,
            "A legacy/current slot conflict did not fail closed.");
    }

    private static async Task CheckTwoLegacyMembersAsync(string dataRoot)
    {
        var gameRoot = Path.Combine(dataRoot, "game");
        var pakPath = ResolveDestination(gameRoot, PakDestination);
        var pakBytes = Encoding.UTF8.GetBytes("two legacy members PAK evidence");
        await WriteFixtureFileAsync(pakPath, pakBytes);
        await WriteFixtureFileAsync(
            GetStatePath(dataRoot, RuProductId),
            CreateLiteralLegacyState(
                RuProductId,
                gameRoot,
                PakDestination,
                pakBytes,
                "1.0.0",
                "24378492",
                includeIsEnabled: true,
                isEnabled: true));
        await WriteFixtureFileAsync(
            GetStatePath(dataRoot, EsProductId),
            CreateLiteralLegacyState(
                EsProductId,
                gameRoot,
                PakDestination,
                pakBytes,
                "1.0.0",
                "24378492",
                includeIsEnabled: true,
                isEnabled: true));

        await AssertMigrationFailsWithoutEvidenceChangesAsync(
            dataRoot,
            CreateCatalog(
                CreateVariantProduct(RuProductId, "ru"),
                CreateVariantProduct(EsProductId, "es")),
            pakPath,
            pakBytes,
            "Two schema-v1 members of one slot did not fail closed.");
    }

    private static async Task CheckMalformedStateAsync(string dataRoot)
    {
        var gameRoot = Path.Combine(dataRoot, "game");
        var pakPath = ResolveDestination(gameRoot, PakDestination);
        var pakBytes = Encoding.UTF8.GetBytes("malformed state PAK evidence");
        await WriteFixtureFileAsync(pakPath, pakBytes);
        await WriteFixtureFileAsync(
            GetStatePath(dataRoot, RuProductId),
            Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"productId\":\"nfg.anvil-empires.ru\""));

        await AssertMigrationFailsWithoutEvidenceChangesAsync(
            dataRoot,
            CreateCatalog(CreateVariantProduct(RuProductId, "ru")),
            pakPath,
            pakBytes,
            "Malformed schema-v1 state did not fail closed.");
    }

    private static async Task CheckAmbiguousCurrentStateAsync(string dataRoot)
    {
        var gameRoot = Path.Combine(dataRoot, "game");
        var pakPath = ResolveDestination(gameRoot, PakDestination);
        var pakBytes = Encoding.UTF8.GetBytes("ambiguous state PAK evidence");
        await WriteFixtureFileAsync(pakPath, pakBytes);
        var store = new InstallationStateStore(dataRoot);
        await store.SaveAsync(CreateCurrentState(EsProductId, EsProductId, gameRoot, pakBytes));

        await AssertMigrationFailsWithoutEvidenceChangesAsync(
            dataRoot,
            CreateCatalog(CreateVariantProduct(EsProductId, "es")),
            pakPath,
            pakBytes,
            "A schema-v2 state whose key disagrees with the catalog did not fail closed.");
    }

    private static async Task CheckNonmatchingBackupAsync(string dataRoot)
    {
        var gameRoot = Path.Combine(dataRoot, "game");
        var pakPath = ResolveDestination(gameRoot, PakDestination);
        var pakBytes = Encoding.UTF8.GetBytes("nonmatching backup PAK evidence");
        await WriteFixtureFileAsync(pakPath, pakBytes);
        await WriteFixtureFileAsync(
            GetStatePath(dataRoot, RuProductId),
            CreateLiteralLegacyState(
                RuProductId,
                gameRoot,
                PakDestination,
                pakBytes,
                "1.0.0",
                "24378492",
                includeIsEnabled: true,
                isEnabled: true));
        await WriteFixtureFileAsync(
            GetBackupPath(dataRoot, RuProductId),
            Encoding.UTF8.GetBytes("not the original schema-v1 JSON"));

        await AssertMigrationFailsWithoutEvidenceChangesAsync(
            dataRoot,
            CreateCatalog(CreateVariantProduct(RuProductId, "ru")),
            pakPath,
            pakBytes,
            "A nonmatching migration backup did not fail closed.");
    }

    private static async Task AssertMigrationFailsWithoutEvidenceChangesAsync(
        string dataRoot,
        StoreCatalog catalog,
        string pakPath,
        byte[] expectedPakBytes,
        string message)
    {
        var before = CaptureStateEvidence(dataRoot);
        await AssertThrowsAsync<InstallationStateException>(
            () => new InstallationStateMigrator(new InstallationStateStore(dataRoot))
                .MigrateAsync(catalog),
            message);
        var after = CaptureStateEvidence(dataRoot);
        AssertEvidenceEqual(before, after, message);
        AssertBytesEqual(
            expectedPakBytes,
            await File.ReadAllBytesAsync(pakPath),
            $"{message} The PAK evidence changed.");
    }

    private static Dictionary<string, byte[]> CaptureStateEvidence(string dataRoot)
    {
        var evidence = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var relativeRoot in new[]
                 {
                     Path.Combine("state", "installations"),
                     Path.Combine("state", "migrations")
                 })
        {
            var root = Path.Combine(dataRoot, relativeRoot);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                evidence[Path.GetRelativePath(dataRoot, path)] = File.ReadAllBytes(path);
            }
        }

        return evidence;
    }

    private static void AssertEvidenceEqual(
        IReadOnlyDictionary<string, byte[]> before,
        IReadOnlyDictionary<string, byte[]> after,
        string message)
    {
        Assert(
            before.Keys.Order(StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(after.Keys.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase),
            $"{message} Installation-state evidence files changed.");
        foreach (var (path, bytes) in before)
        {
            Assert(
                after.TryGetValue(path, out var current),
                $"{message} Evidence file '{path}' disappeared.");
            AssertBytesEqual(bytes, current!, $"{message} Evidence file '{path}' changed.");
        }
    }

    private static async Task<SpanishLegacyFixture> CreateSpanishLegacyFixtureAsync(string dataRoot)
    {
        var gameRoot = Path.Combine(dataRoot, "game");
        var pakPath = ResolveDestination(gameRoot, PakDestination);
        var pakBytes = Encoding.UTF8.GetBytes("Spanish variant PAK evidence");
        await WriteFixtureFileAsync(pakPath, pakBytes);
        var stateBytes = CreateLiteralLegacyState(
            EsProductId,
            gameRoot,
            PakDestination,
            pakBytes,
            version: "1.4.2",
            detectedSteamBuildId: "24378492",
            includeIsEnabled: true,
            isEnabled: false);
        var sourcePath = GetStatePath(dataRoot, EsProductId);
        await WriteFixtureFileAsync(sourcePath, stateBytes);
        return new SpanishLegacyFixture(sourcePath, stateBytes, gameRoot, pakPath, pakBytes);
    }

    private static InstalledProductState CreateCurrentState(
        string installationKey,
        string productId,
        string gameRoot,
        byte[] pakBytes) => new()
        {
            SchemaVersion = 2,
            InstallationKey = installationKey,
            ProductId = productId,
            Version = "9.9.9",
            PackageSizeBytes = 123456,
            PackageSha256 = new string('c', 64),
            SteamAppId = "1373910",
            SteamBuildId = "24370001",
            DetectedSteamBuildId = "24378492",
            GameRoot = gameRoot,
            InstalledAt = DateTimeOffset.Parse("2026-07-19T10:11:12+03:00"),
            IsEnabled = true,
            Files =
            [
                new InstalledFileState
                {
                    Destination = PakDestination,
                    SizeBytes = pakBytes.LongLength,
                    Sha256 = Sha256(pakBytes)
                }
            ]
        };

    private static byte[] CreateLiteralLegacyState(
        string productId,
        string gameRoot,
        string destination,
        byte[] fileBytes,
        string version,
        string detectedSteamBuildId,
        bool includeIsEnabled,
        bool isEnabled)
    {
        const string template = """
            {
              "schemaVersion": 1,
              "productId": __PRODUCT_ID__,
              "version": __VERSION__,
              "packageSizeBytes": 987654321,
              "packageSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "steamAppId": "1373910",
              "steamBuildId": "24370001",
              "detectedSteamBuildId": __DETECTED_BUILD_ID__,
              "gameRoot": __GAME_ROOT__,
              "installedAt": "2026-07-18T19:20:21+03:00",
            __IS_ENABLED__  "files": [
                {
                  "destination": __DESTINATION__,
                  "sizeBytes": __FILE_SIZE__,
                  "sha256": __FILE_SHA256__
                }
              ]
            }
            """;
        var enabledLine = includeIsEnabled
            ? $"  \"isEnabled\": {isEnabled.ToString().ToLowerInvariant()},\n"
            : string.Empty;
        var json = template
            .Replace("__PRODUCT_ID__", JsonString(productId), StringComparison.Ordinal)
            .Replace("__VERSION__", JsonString(version), StringComparison.Ordinal)
            .Replace("__DETECTED_BUILD_ID__", JsonString(detectedSteamBuildId), StringComparison.Ordinal)
            .Replace("__GAME_ROOT__", JsonString(gameRoot), StringComparison.Ordinal)
            .Replace("__IS_ENABLED__", enabledLine, StringComparison.Ordinal)
            .Replace("__DESTINATION__", JsonString(destination), StringComparison.Ordinal)
            .Replace("__FILE_SIZE__", fileBytes.LongLength.ToString(), StringComparison.Ordinal)
            .Replace("__FILE_SHA256__", JsonString(Sha256(fileBytes)), StringComparison.Ordinal);
        return Encoding.UTF8.GetBytes(json);
    }

    private static string JsonString(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);

    private static StoreCatalog CreateCatalog(params ProductManifest[] products) =>
        new("nfg.test.installation-state-v2", "Installation State v2 Test", products);

    private static ProductManifest CreateVariantProduct(string id, string locale) =>
        CreateProduct(
            id,
            schemaVersion: 2,
            familyId: AnvilFamilyId,
            locale: locale,
            exclusiveGroup: AnvilInstallationKey);

    private static ProductManifest CreateProduct(
        string id,
        int schemaVersion,
        string? familyId = null,
        string? locale = null,
        string? exclusiveGroup = null) => new()
        {
            SchemaVersion = schemaVersion,
            Id = id,
            Type = familyId is null ? "utility" : "localization",
            FamilyId = familyId,
            Locale = locale,
            ExclusiveGroup = exclusiveGroup,
            Display = new ProductDisplay
            {
                Title = familyId is null ? "Anvil Empires Forge Helper" : "Anvil Empires Language Pack",
                Subtitle = "Synthetic migration fixture",
                Summary = "Synthetic migration fixture",
                Description = "Synthetic migration fixture"
            },
            Release = new ProductRelease
            {
                Version = "1.4.2",
                Channel = "stable",
                GameVersion = "steam-build-24370001",
                Payload = new ProductPayload
                {
                    Url = new Uri($"https://packages.test/{id}.zip"),
                    SizeBytes = 987654321,
                    Sha256 = new string('a', 64)
                }
            },
            Compatibility = new ProductCompatibility
            {
                Platforms = ["windows-x64"],
                GameVersion = "steam-build-24370001",
                Status = "verified"
            },
            Installation = new ProductInstallation
            {
                Strategy = "managed-files",
                SupportsRollback = true,
                RequiresElevation = "auto",
                Detection =
                [
                    new ProductDetectionRule
                    {
                        Provider = "steam",
                        ProductId = "1373910"
                    }
                ]
            },
            Dependencies = [],
            Progress = new ProductProgress
            {
                TranslationPercent = familyId is null ? null : 100,
                Label = "Ready"
            }
        };

    private static string GetStatePath(string dataRoot, string installationKey) =>
        Path.Combine(dataRoot, "state", "installations", $"{installationKey}.json");

    private static string GetBackupPath(string dataRoot, string productId) =>
        Path.Combine(
            dataRoot,
            "state",
            "migrations",
            "installations-v1",
            $"{productId}.json");

    private static string ResolveDestination(string gameRoot, string destination) =>
        Path.Combine(gameRoot, destination.Replace('/', Path.DirectorySeparatorChar));

    private static async Task WriteFixtureFileAsync(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

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

    private static void AssertBytesEqual(byte[] expected, byte[] actual, string message)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record SpanishLegacyFixture(
        string SourcePath,
        byte[] StateBytes,
        string GameRoot,
        string PakPath,
        byte[] PakBytes);
}
