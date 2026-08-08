using System.Text.Json;
using System.Text.Json.Serialization;
using Nfg.Store.Contracts;

namespace Nfg.Store.Core;

public sealed class CatalogService
{
    private const int MaximumInformationItemCount = 100;

    private readonly HttpClient _httpClient;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public CatalogService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    public async Task<CatalogLoadResult> LoadRemoteFirstAsync(
        Uri catalogUri,
        string cacheRoot,
        string bundledCatalogRoot,
        CancellationToken cancellationToken = default,
        IReadOnlySet<string>? requiredProductIds = null)
    {
        ValidateLoadArguments(catalogUri, cacheRoot, bundledCatalogRoot);

        Exception? remoteError;

        try
        {
            var remoteCatalog = await DownloadAsync(catalogUri, cancellationToken);
            ValidateRequiredProducts(remoteCatalog.Catalog, requiredProductIds);
            await WriteCacheAsync(cacheRoot, remoteCatalog.Files, cancellationToken);

            return new CatalogLoadResult(
                remoteCatalog.Catalog,
                CatalogSourceKind.Remote,
                catalogUri,
                Path.GetFullPath(cacheRoot));
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            remoteError = exception;
        }
        catch (Exception exception)
        {
            remoteError = exception;
        }

        try
        {
            var cachedCatalog = await LoadAsync(cacheRoot, cancellationToken);
            ValidateRequiredProducts(cachedCatalog, requiredProductIds);
            return new CatalogLoadResult(
                cachedCatalog,
                CatalogSourceKind.Cache,
                catalogUri,
                Path.GetFullPath(cacheRoot));
        }
        catch (Exception cacheError) when (cacheError is not OperationCanceledException)
        {
            try
            {
                var bundledCatalog = await LoadAsync(bundledCatalogRoot, cancellationToken);
                ValidateRequiredProducts(bundledCatalog, requiredProductIds);
                return new CatalogLoadResult(
                    bundledCatalog,
                    CatalogSourceKind.Bundled,
                    catalogUri,
                    Path.GetFullPath(cacheRoot));
            }
            catch (Exception bundledError) when (bundledError is not OperationCanceledException)
            {
                throw new CatalogValidationException(
                    "Remote, cached, and bundled catalogs could not be loaded.",
                    new AggregateException(remoteError, cacheError, bundledError));
            }
        }
    }

    public async Task<CatalogLoadResult> LoadLocalFirstAsync(
        Uri catalogUri,
        string cacheRoot,
        string bundledCatalogRoot,
        CancellationToken cancellationToken = default,
        IReadOnlySet<string>? requiredProductIds = null)
    {
        ValidateLoadArguments(catalogUri, cacheRoot, bundledCatalogRoot);

        try
        {
            var cachedCatalog = await LoadAsync(cacheRoot, cancellationToken);
            ValidateRequiredProducts(cachedCatalog, requiredProductIds);
            return new CatalogLoadResult(
                cachedCatalog,
                CatalogSourceKind.Cache,
                catalogUri,
                Path.GetFullPath(cacheRoot));
        }
        catch (Exception cacheError) when (cacheError is not OperationCanceledException)
        {
            try
            {
                var bundledCatalog = await LoadAsync(bundledCatalogRoot, cancellationToken);
                ValidateRequiredProducts(bundledCatalog, requiredProductIds);
                return new CatalogLoadResult(
                    bundledCatalog,
                    CatalogSourceKind.Bundled,
                    catalogUri,
                    Path.GetFullPath(cacheRoot));
            }
            catch (Exception bundledError) when (bundledError is not OperationCanceledException)
            {
                throw new CatalogValidationException(
                    "Cached and bundled catalogs could not be loaded.",
                    new AggregateException(cacheError, bundledError));
            }
        }
    }

    public async Task<StoreCatalog> LoadAsync(
        string catalogRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogRoot);

        var root = Path.GetFullPath(catalogRoot);
        var indexPath = Path.Combine(root, "catalog.json");
        var catalog = await ReadFileAsync<CatalogManifest>(indexPath, cancellationToken);
        ValidateCatalog(catalog);

        var products = new List<ProductManifest>(catalog.Products.Count);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relativePath in catalog.Products)
        {
            var normalizedPath = NormalizeProductPath(relativePath);
            var productPath = ResolveInsideRoot(root, normalizedPath);
            var product = await ReadFileAsync<ProductManifest>(productPath, cancellationToken);
            ValidateProduct(product);

            if (!ids.Add(product.Id))
            {
                throw new CatalogValidationException(
                    $"Duplicate product id '{product.Id}' in catalog '{catalog.CatalogId}'.");
            }

            products.Add(product);
        }

        return new StoreCatalog(catalog.CatalogId, catalog.DisplayName, products);
    }

    private async Task<RemoteCatalog> DownloadAsync(
        Uri catalogUri,
        CancellationToken cancellationToken)
    {
        var indexBytes = await _httpClient.GetByteArrayAsync(catalogUri, cancellationToken);
        var catalog = ReadBytes<CatalogManifest>(indexBytes, catalogUri.ToString());
        ValidateCatalog(catalog);

        var products = new List<ProductManifest>(catalog.Products.Count);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["catalog.json"] = indexBytes
        };
        var baseUri = new Uri(catalogUri, ".");

        foreach (var relativePath in catalog.Products)
        {
            var normalizedPath = NormalizeProductPath(relativePath);
            var productUri = new Uri(baseUri, normalizedPath);
            var productBytes = await _httpClient.GetByteArrayAsync(productUri, cancellationToken);
            var product = ReadBytes<ProductManifest>(productBytes, productUri.ToString());
            ValidateProduct(product);

            if (!ids.Add(product.Id))
            {
                throw new CatalogValidationException(
                    $"Duplicate product id '{product.Id}' in catalog '{catalog.CatalogId}'.");
            }

            products.Add(product);
            files[normalizedPath] = productBytes;
        }

        return new RemoteCatalog(
            new StoreCatalog(catalog.CatalogId, catalog.DisplayName, products),
            files);
    }

    private static async Task WriteCacheAsync(
        string cacheRoot,
        IReadOnlyDictionary<string, byte[]> files,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(cacheRoot);
        Directory.CreateDirectory(root);

        foreach (var (relativePath, contents) in files
                     .OrderBy(pair => pair.Key.Equals("catalog.json", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var destination = ResolveInsideRoot(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            var temporaryPath = $"{destination}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, contents, cancellationToken);
                File.Move(temporaryPath, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }

    private static string ResolveInsideRoot(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new CatalogValidationException("Product paths must be relative to the catalog root.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new CatalogValidationException(
                $"Product path '{relativePath}' resolves outside the catalog root.");
        }

        return fullPath;
    }

    private static string NormalizeProductPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Contains('?', StringComparison.Ordinal) ||
            relativePath.Contains('#', StringComparison.Ordinal))
        {
            throw new CatalogValidationException("Product paths must be relative catalog paths.");
        }

        var normalized = relativePath.Replace('\\', '/');
        var segments = normalized.Split('/');
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            throw new CatalogValidationException(
                $"Product path '{relativePath}' contains an invalid segment.");
        }

        return normalized;
    }

    private static async Task<T> ReadFileAsync<T>(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var value = await JsonSerializer.DeserializeAsync<T>(
                stream,
                SerializerOptions,
                cancellationToken);

            return value ?? throw new CatalogValidationException(
                $"Manifest '{path}' is empty or invalid.");
        }
        catch (JsonException exception)
        {
            throw new CatalogValidationException(
                $"Manifest '{path}' contains invalid JSON.",
                exception);
        }
    }

    private static T ReadBytes<T>(ReadOnlySpan<byte> bytes, string source)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, SerializerOptions)
                ?? throw new CatalogValidationException(
                    $"Manifest '{source}' is empty or invalid.");
        }
        catch (JsonException exception)
        {
            throw new CatalogValidationException(
                $"Manifest '{source}' contains invalid JSON.",
                exception);
        }
    }

    private static void ValidateCatalog(CatalogManifest catalog)
    {
        if (catalog.SchemaVersion != 1)
        {
            throw new CatalogValidationException(
                $"Unsupported catalog schema version: {catalog.SchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(catalog.CatalogId) ||
            string.IsNullOrWhiteSpace(catalog.DisplayName) ||
            catalog.Products is null ||
            catalog.Products.Count == 0)
        {
            throw new CatalogValidationException("Catalog metadata or product list is incomplete.");
        }
    }

    private static void ValidateRequiredProducts(
        StoreCatalog catalog,
        IReadOnlySet<string>? requiredProductIds)
    {
        if (requiredProductIds is null || requiredProductIds.Count == 0)
        {
            return;
        }

        var availableProductIds = catalog.Products
            .Select(product => product.Id)
            .ToHashSet(StringComparer.Ordinal);
        var missingProductIds = requiredProductIds
            .Where(productId => !availableProductIds.Contains(productId))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (missingProductIds.Length != 0)
        {
            throw new CatalogValidationException(
                "Catalog is missing products retained in the local library: " +
                string.Join(", ", missingProductIds));
        }
    }

    private static void ValidateProduct(ProductManifest product)
    {
        if (product.SchemaVersion != 1)
        {
            throw new CatalogValidationException(
                $"Product '{product.Id}' has unsupported schema version {product.SchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(product.Id) || !product.Id.Contains('.', StringComparison.Ordinal))
        {
            throw new CatalogValidationException("Product ids must be stable reverse-DNS identifiers.");
        }

        if (product.Display is null ||
            product.Release is null ||
            product.Releases is null ||
            product.Compatibility is null ||
            product.Installation is null ||
            product.Dependencies is null ||
            product.Progress is null)
        {
            throw new CatalogValidationException(
                $"Product '{product.Id}' has incomplete required structure.");
        }

        if (string.IsNullOrWhiteSpace(product.Type) ||
            string.IsNullOrWhiteSpace(product.Display.Title) ||
            string.IsNullOrWhiteSpace(product.Display.Subtitle) ||
            string.IsNullOrWhiteSpace(product.Display.Summary) ||
            string.IsNullOrWhiteSpace(product.Display.Description) ||
            string.IsNullOrWhiteSpace(product.Compatibility.Status) ||
            product.Compatibility.Platforms is null ||
            product.Compatibility.Platforms.Any(string.IsNullOrWhiteSpace) ||
            string.IsNullOrWhiteSpace(product.Installation.Strategy) ||
            string.IsNullOrWhiteSpace(product.Installation.RequiresElevation) ||
            product.Installation.Detection is null ||
            product.Installation.Detection.Any(rule =>
                rule is null || string.IsNullOrWhiteSpace(rule.Provider)) ||
            product.Dependencies.Any(dependency =>
                dependency is null ||
                string.IsNullOrWhiteSpace(dependency.ProductId) ||
                string.IsNullOrWhiteSpace(dependency.VersionRange)) ||
            string.IsNullOrWhiteSpace(product.Progress.Label))
        {
            throw new CatalogValidationException(
                $"Product '{product.Id}' has incomplete required metadata.");
        }

        if (product.Progress.TranslationPercent is < 0 or > 100)
        {
            throw new CatalogValidationException(
                $"Product '{product.Id}' has translation progress outside 0..100.");
        }

        ValidateInformationItems(product.Id, "display.features", product.Display.Features);
        ValidateGameVersion(
            product.Id,
            "compatibility.gameVersion",
            product.Compatibility.GameVersion);

        var releaseVersions = new HashSet<string>(StringComparer.Ordinal);
        ValidateRelease(product.Id, "release", product.Release);
        releaseVersions.Add(product.Release.Version);

        for (var index = 0; index < product.Releases.Count; index++)
        {
            var release = product.Releases[index];
            if (release is null)
            {
                throw new CatalogValidationException(
                    $"Product '{product.Id}' field 'releases[{index}]' cannot be null.");
            }

            var fieldName = $"releases[{index}]";
            ValidateRelease(product.Id, fieldName, release);
            if (!releaseVersions.Add(release.Version))
            {
                throw new CatalogValidationException(
                    $"Product '{product.Id}' declares release version " +
                    $"'{release.Version}' more than once.");
            }
        }
    }

    private static void ValidateRelease(
        string productId,
        string fieldName,
        ProductRelease release)
    {
        if (!SemanticVersionComparer.IsValid(release.Version))
        {
            throw new CatalogValidationException(
                $"Product '{productId}' field '{fieldName}.version' must be a valid " +
                "Semantic Versioning 2.0.0 version.");
        }

        if (string.IsNullOrWhiteSpace(release.Channel))
        {
            throw new CatalogValidationException(
                $"Product '{productId}' field '{fieldName}.channel' cannot be blank.");
        }

        ValidateGameVersion(productId, $"{fieldName}.gameVersion", release.GameVersion);
        ValidateInformationItems(productId, $"{fieldName}.highlights", release.Highlights);
        ValidateInformationItems(productId, $"{fieldName}.knownIssues", release.KnownIssues);

        if (release.NotesUrl is { } notesUrl &&
            (!notesUrl.IsAbsoluteUri || notesUrl.Scheme != Uri.UriSchemeHttps))
        {
            throw new CatalogValidationException(
                $"Product '{productId}' field '{fieldName}.notesUrl' must be absolute HTTPS.");
        }

        if (release.Payload is { } payload)
        {
            if (payload.Url is null ||
                !payload.Url.IsAbsoluteUri ||
                payload.Url.Scheme != Uri.UriSchemeHttps)
            {
                throw new CatalogValidationException(
                    $"Product '{productId}' field '{fieldName}.payload.url' must be absolute HTTPS.");
            }

            if (payload.SizeBytes <= 0 ||
                string.IsNullOrWhiteSpace(payload.Sha256) ||
                payload.Sha256.Length != 64 ||
                !payload.Sha256.All(Uri.IsHexDigit))
            {
                throw new CatalogValidationException(
                    $"Product '{productId}' field '{fieldName}.payload' metadata is incomplete.");
            }
        }
    }

    private static void ValidateGameVersion(
        string productId,
        string fieldName,
        string? gameVersion)
    {
        if (gameVersion is null)
        {
            return;
        }

        var buildId = gameVersion.StartsWith(ProductReleaseCatalog.SteamBuildPrefix, StringComparison.Ordinal)
            ? gameVersion[ProductReleaseCatalog.SteamBuildPrefix.Length..]
            : string.Empty;
        if (buildId.Length == 0 ||
            buildId.Any(character => character is < '0' or > '9'))
        {
            throw new CatalogValidationException(
                $"Product '{productId}' field '{fieldName}' must use the format " +
                $"'{ProductReleaseCatalog.SteamBuildPrefix}<build-id>'.");
        }
    }

    private static void ValidateInformationItems(
        string productId,
        string fieldName,
        IReadOnlyList<string>? items)
    {
        if (items is null)
        {
            throw new CatalogValidationException(
                $"Product '{productId}' field '{fieldName}' must be an array when present.");
        }

        if (items.Count > MaximumInformationItemCount)
        {
            throw new CatalogValidationException(
                $"Product '{productId}' field '{fieldName}' exceeds " +
                $"the {MaximumInformationItemCount}-item limit.");
        }

        if (items.Any(string.IsNullOrWhiteSpace))
        {
            throw new CatalogValidationException(
                $"Product '{productId}' field '{fieldName}' contains a blank item.");
        }
    }

    private static void ValidateLoadArguments(
        Uri catalogUri,
        string cacheRoot,
        string bundledCatalogRoot)
    {
        ArgumentNullException.ThrowIfNull(catalogUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundledCatalogRoot);

        if (!catalogUri.IsAbsoluteUri || catalogUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException(
                "The remote catalog URI must be an absolute HTTPS URI.",
                nameof(catalogUri));
        }
    }

    private sealed record RemoteCatalog(
        StoreCatalog Catalog,
        IReadOnlyDictionary<string, byte[]> Files);
}
