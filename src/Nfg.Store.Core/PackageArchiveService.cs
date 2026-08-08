using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nfg.Store.Contracts;

namespace Nfg.Store.Core;

public sealed class PackageArchiveService
{
    public const string SupportedSchema = "nfg-package/1";
    public const string SupportedStrategy = "managed-files";

    private const string ManifestFileName = "nfg-package.json";
    private const string SteamBuildPrefix = "steam-build-";
    private const string SteamDestinationPrefix = "steam-game/";
    private const int MaximumManifestSizeBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public async Task<ValidatedPackage> ValidateAsync(
        string archivePath,
        ProductManifest product,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentNullException.ThrowIfNull(product);

        var payload = product.Release.Payload
            ?? throw new PackageValidationException(
                $"Product '{product.Id}' does not declare a downloadable payload.");
        ValidateExpectedPayload(product.Id, payload);

        var fullArchivePath = Path.GetFullPath(archivePath);
        await using var archiveStream = new FileStream(
            fullArchivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (archiveStream.Length != payload.SizeBytes)
        {
            throw new PackageValidationException(
                $"Package length mismatch for '{product.Id}': expected " +
                $"{payload.SizeBytes} bytes, received {archiveStream.Length} bytes.");
        }

        var outerDigest = await ComputeDigestAsync(archiveStream, cancellationToken);
        if (outerDigest.Length != payload.SizeBytes ||
            !outerDigest.Sha256.Equals(payload.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new PackageValidationException(
                $"Package SHA-256 mismatch for '{product.Id}'.");
        }

        archiveStream.Position = 0;

        try
        {
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
            return await ValidateArchiveAsync(
                fullArchivePath,
                archive,
                product,
                cancellationToken);
        }
        catch (PackageValidationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or JsonException or NotSupportedException)
        {
            throw new PackageValidationException(
                $"Package '{fullArchivePath}' is not a valid NFG package archive.",
                exception);
        }
    }

    private static async Task<ValidatedPackage> ValidateArchiveAsync(
        string archivePath,
        ZipArchive archive,
        ProductManifest product,
        CancellationToken cancellationToken)
    {
        var entries = IndexEntries(archive);
        var manifestEntries = entries.Values
            .Where(entry =>
                !IsDirectory(entry) &&
                GetFileName(entry.FullName).Equals(
                    ManifestFileName,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (manifestEntries.Length != 1)
        {
            throw new PackageValidationException(
                $"The archive must contain exactly one '{ManifestFileName}' file.");
        }

        var manifestEntry = manifestEntries[0];
        var manifestSegments = SplitArchivePath(manifestEntry.FullName);
        if (manifestSegments.Length != 2 ||
            !manifestSegments[1].Equals(ManifestFileName, StringComparison.Ordinal))
        {
            throw new PackageValidationException(
                $"'{ManifestFileName}' must be located exactly one directory deep.");
        }

        if (manifestEntry.Length is <= 0 or > MaximumManifestSizeBytes)
        {
            throw new PackageValidationException(
                $"'{ManifestFileName}' has an invalid size.");
        }

        var manifestBytes = await ReadEntryAsync(
            manifestEntry,
            MaximumManifestSizeBytes,
            cancellationToken);
        RejectDuplicateJsonProperties(manifestBytes, ManifestFileName);

        var manifest = JsonSerializer.Deserialize<NfgPackageManifest>(
            manifestBytes,
            SerializerOptions)
            ?? throw new PackageValidationException(
                $"'{ManifestFileName}' is empty or invalid.");

        ValidateManifestIdentity(manifest, product);

        var wrapperDirectory = manifestSegments[0];
        var validatedFiles = await ValidateFilesAsync(
            entries,
            wrapperDirectory,
            manifest,
            cancellationToken);

        return new ValidatedPackage(
            archivePath,
            wrapperDirectory,
            manifest,
            validatedFiles);
    }

    private static Dictionary<string, ZipArchiveEntry> IndexEntries(ZipArchive archive)
    {
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in archive.Entries)
        {
            ValidateArchivePath(entry.FullName, "archive entry");
            var comparisonPath = entry.FullName.TrimEnd('/');

            if (!entries.TryAdd(comparisonPath, entry))
            {
                throw new PackageValidationException(
                    $"The archive contains duplicate path '{entry.FullName}'.");
            }
        }

        return entries;
    }

    private static async Task<IReadOnlyList<ValidatedPackageFile>> ValidateFilesAsync(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        string wrapperDirectory,
        NfgPackageManifest manifest,
        CancellationToken cancellationToken)
    {
        if (manifest.Files is not { Count: > 0 })
        {
            throw new PackageValidationException("The package does not declare any files.");
        }

        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var validatedFiles = new List<ValidatedPackageFile>(manifest.Files.Count);

        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (file is null)
            {
                throw new PackageValidationException(
                    "The package contains an invalid null file declaration.");
            }

            ValidateRelativePath(file.Source, "package source");
            ValidateDestination(file.Destination);
            ValidateDigest(file.Sha256, $"file '{file.Source}'");

            if (file.SizeBytes < 0)
            {
                throw new PackageValidationException(
                    $"File '{file.Source}' has a negative declared size.");
            }

            if (!sources.Add(file.Source))
            {
                throw new PackageValidationException(
                    $"The package declares duplicate source '{file.Source}'.");
            }

            if (!destinations.Add(file.Destination))
            {
                throw new PackageValidationException(
                    $"The package declares duplicate destination '{file.Destination}'.");
            }

            var archiveEntryPath = $"{wrapperDirectory}/{file.Source}";
            if (!entries.TryGetValue(archiveEntryPath, out var entry) ||
                IsDirectory(entry) ||
                !entry.FullName.Equals(archiveEntryPath, StringComparison.Ordinal))
            {
                throw new PackageValidationException(
                    $"Declared source '{file.Source}' is missing from the archive.");
            }

            if (entry.Length != file.SizeBytes)
            {
                throw new PackageValidationException(
                    $"File size mismatch for '{file.Source}': expected " +
                    $"{file.SizeBytes} bytes, received {entry.Length} bytes.");
            }

            await using var entryStream = entry.Open();
            var digest = await ComputeDigestAsync(
                entryStream,
                cancellationToken,
                file.SizeBytes);
            if (digest.Length != file.SizeBytes ||
                !digest.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new PackageValidationException(
                    $"File SHA-256 mismatch for '{file.Source}'.");
            }

            validatedFiles.Add(new ValidatedPackageFile(
                entry.FullName,
                file.Destination,
                file.Destination[SteamDestinationPrefix.Length..],
                file.SizeBytes,
                file.Sha256.ToLowerInvariant()));
        }

        return validatedFiles.AsReadOnly();
    }

    private static void ValidateManifestIdentity(
        NfgPackageManifest manifest,
        ProductManifest product)
    {
        if (!string.Equals(manifest.Schema, SupportedSchema, StringComparison.Ordinal))
        {
            throw new PackageValidationException(
                $"Unsupported package schema '{manifest.Schema}'.");
        }

        if (!string.Equals(manifest.ProductId, product.Id, StringComparison.Ordinal))
        {
            throw new PackageValidationException(
                $"Package product id '{manifest.ProductId}' does not match catalog product " +
                $"'{product.Id}'.");
        }

        if (!string.Equals(
                manifest.Version,
                product.Release.Version,
                StringComparison.Ordinal))
        {
            throw new PackageValidationException(
                $"Package version '{manifest.Version}' does not match catalog version " +
                $"'{product.Release.Version}'.");
        }

        if (!string.Equals(
                manifest.Strategy,
                SupportedStrategy,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.Strategy,
                product.Installation.Strategy,
                StringComparison.Ordinal))
        {
            throw new PackageValidationException(
                $"Package installation strategy '{manifest.Strategy}' is not supported " +
                "or does not match the catalog.");
        }

        if (manifest.Steam is null)
        {
            throw new PackageValidationException(
                "The package does not declare Steam compatibility metadata.");
        }

        var expectedSteamAppId = GetExpectedSteamAppId(product);
        if (!IsAsciiDigits(manifest.Steam.AppId) ||
            !string.Equals(
                manifest.Steam.AppId,
                expectedSteamAppId,
                StringComparison.Ordinal))
        {
            throw new PackageValidationException(
                $"Package Steam app id '{manifest.Steam.AppId}' does not match catalog app id " +
                $"'{expectedSteamAppId}'.");
        }

        var expectedBuildId = GetExpectedSteamBuildId(product);
        if (!IsAsciiDigits(manifest.Steam.BuildId) ||
            !string.Equals(
                manifest.Steam.BuildId,
                expectedBuildId,
                StringComparison.Ordinal))
        {
            throw new PackageValidationException(
                $"Package Steam build id '{manifest.Steam.BuildId}' does not match catalog " +
                $"build id '{expectedBuildId}'.");
        }
    }

    private static string GetExpectedSteamAppId(ProductManifest product)
    {
        var appIds = product.Installation.Detection
            .Where(rule =>
                string.Equals(rule.Provider, "steam", StringComparison.OrdinalIgnoreCase))
            .Select(rule => rule.ProductId)
            .Where(IsAsciiDigits)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (appIds.Length != 1)
        {
            throw new PackageValidationException(
                $"Catalog product '{product.Id}' must declare exactly one Steam app id.");
        }

        return appIds[0]!;
    }

    private static string GetExpectedSteamBuildId(ProductManifest product)
    {
        var gameVersion = product.Compatibility.GameVersion;
        if (string.IsNullOrWhiteSpace(gameVersion) ||
            !gameVersion.StartsWith(SteamBuildPrefix, StringComparison.Ordinal) ||
            gameVersion.Length == SteamBuildPrefix.Length ||
            !IsAsciiDigits(gameVersion[SteamBuildPrefix.Length..]))
        {
            throw new PackageValidationException(
                $"Catalog product '{product.Id}' must declare a Steam build as " +
                $"'{SteamBuildPrefix}<build-id>'.");
        }

        return gameVersion[SteamBuildPrefix.Length..];
    }

    private static void ValidateExpectedPayload(string productId, ProductPayload payload)
    {
        if (payload.SizeBytes <= 0)
        {
            throw new PackageValidationException(
                $"Catalog payload for '{productId}' has an invalid size.");
        }

        ValidateDigest(payload.Sha256, $"catalog payload for '{productId}'");
    }

    private static void ValidateDestination(string destination)
    {
        ValidateRelativePath(destination, "package destination");

        if (!destination.StartsWith(SteamDestinationPrefix, StringComparison.Ordinal) ||
            destination.Length == SteamDestinationPrefix.Length)
        {
            throw new PackageValidationException(
                $"Package destination '{destination}' must start with " +
                $"'{SteamDestinationPrefix}' and name a file below it.");
        }
    }

    private static void ValidateRelativePath(string path, string description)
    {
        ValidateArchivePath(path, description);

        if (path.EndsWith("/", StringComparison.Ordinal))
        {
            throw new PackageValidationException(
                $"The {description} '{path}' must name a file.");
        }
    }

    private static void ValidateArchivePath(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Contains('\\', StringComparison.Ordinal) ||
            path.Contains('\0', StringComparison.Ordinal) ||
            path.StartsWith("/", StringComparison.Ordinal) ||
            Path.IsPathRooted(path))
        {
            throw new PackageValidationException(
                $"The {description} '{path}' is not a safe relative path.");
        }

        var segments = SplitArchivePath(path);
        if (segments.Length == 0 ||
            segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) ||
                segment is "." or ".." ||
                IsUnsafeWindowsPathSegment(segment)))
        {
            throw new PackageValidationException(
                $"The {description} '{path}' contains an invalid path segment.");
        }
    }

    private static string[] SplitArchivePath(string path)
    {
        var pathWithoutDirectoryMarker = path.EndsWith("/", StringComparison.Ordinal)
            ? path[..^1]
            : path;
        return pathWithoutDirectoryMarker.Split('/', StringSplitOptions.None);
    }

    private static string GetFileName(string path)
    {
        var separatorIndex = path.LastIndexOf('/');
        return separatorIndex < 0 ? path : path[(separatorIndex + 1)..];
    }

    private static bool IsDirectory(ZipArchiveEntry entry)
    {
        return entry.FullName.EndsWith("/", StringComparison.Ordinal);
    }

    private static void ValidateDigest(string sha256, string description)
    {
        if (sha256 is null ||
            sha256.Length != 64 ||
            !sha256.All(Uri.IsHexDigit))
        {
            throw new PackageValidationException(
                $"The SHA-256 for {description} must contain exactly 64 hexadecimal characters.");
        }
    }

    private static async Task<byte[]> ReadEntryAsync(
        ZipArchiveEntry entry,
        int maximumSize,
        CancellationToken cancellationToken)
    {
        if (entry.Length > maximumSize)
        {
            throw new PackageValidationException(
                $"Archive entry '{entry.FullName}' exceeds the allowed size.");
        }

        var contents = new byte[checked((int)entry.Length)];
        await using var stream = entry.Open();
        var totalBytesRead = 0;

        while (totalBytesRead < contents.Length)
        {
            var bytesRead = await stream.ReadAsync(
                contents.AsMemory(totalBytesRead),
                cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            totalBytesRead += bytesRead;
        }

        var trailingByte = new byte[1];
        var trailingByteCount = await stream.ReadAsync(
            trailingByte,
            cancellationToken);
        if (totalBytesRead != contents.Length || trailingByteCount != 0)
        {
            throw new PackageValidationException(
                $"Archive entry '{entry.FullName}' has an inconsistent size.");
        }

        return contents;
    }

    private static void RejectDuplicateJsonProperties(
        ReadOnlyMemory<byte> json,
        string source)
    {
        using var document = JsonDocument.Parse(json);
        RejectDuplicateJsonProperties(document.RootElement, source);
    }

    private static void RejectDuplicateJsonProperties(JsonElement element, string source)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var propertyNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!propertyNames.Add(property.Name))
                {
                    throw new PackageValidationException(
                        $"'{source}' contains duplicate JSON property '{property.Name}'.");
                }

                RejectDuplicateJsonProperties(property.Value, source);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateJsonProperties(item, source);
            }
        }
    }

    private static async Task<StreamDigest> ComputeDigestAsync(
        Stream stream,
        CancellationToken cancellationToken,
        long? maximumLength = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        long length = 0;

        try
        {
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(
                       buffer.AsMemory(0, buffer.Length),
                       cancellationToken)) != 0)
            {
                length = checked(length + bytesRead);
                if (maximumLength is { } limit && length > limit)
                {
                    throw new PackageValidationException(
                        $"A package stream exceeds its declared size of {limit} bytes.");
                }

                hash.AppendData(buffer, 0, bytesRead);
            }

            return new StreamDigest(
                length,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsAsciiDigits(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.All(character => character is >= '0' and <= '9');
    }

    private static bool IsUnsafeWindowsPathSegment(string segment)
    {
        if (segment.EndsWith(' ') ||
            segment.EndsWith('.') ||
            segment.Any(character =>
                char.IsControl(character) || character is '<' or '>' or '"' or ':' or '|' or '?' or '*'))
        {
            return true;
        }

        var deviceName = segment.Split('.', 2)[0];
        return deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               IsNumberedDeviceName(deviceName, "COM") ||
               IsNumberedDeviceName(deviceName, "LPT");
    }

    private static bool IsNumberedDeviceName(string value, string prefix)
    {
        return value.Length == prefix.Length + 1 &&
               value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               value[^1] is >= '1' and <= '9';
    }

    private sealed record StreamDigest(long Length, string Sha256);
}
