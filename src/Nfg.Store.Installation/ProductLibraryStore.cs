using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nfg.Store.Installation;

public sealed class ProductLibraryStore
{
    private const int CurrentSchemaVersion = 1;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathGates =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowDuplicateProperties = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private readonly string _libraryPath;
    private readonly SemaphoreSlim _pathGate;

    public ProductLibraryStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _libraryPath = Path.GetFullPath(
            Path.Combine(dataRoot, "state", "library.json"));
        _pathGate = PathGates.GetOrAdd(_libraryPath, static _ => new SemaphoreSlim(1, 1));
    }

    public async Task<IReadOnlySet<string>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _pathGate.WaitAsync(cancellationToken);
        try
        {
            var productIds = await LoadMutableAsync(cancellationToken);
            return productIds.ToFrozenSet(StringComparer.Ordinal);
        }
        finally
        {
            _pathGate.Release();
        }
    }

    public async Task AddAsync(
        string productId,
        CancellationToken cancellationToken = default)
    {
        ValidateProductId(productId);

        await _pathGate.WaitAsync(cancellationToken);
        try
        {
            var productIds = await LoadMutableAsync(cancellationToken);
            if (!productIds.Add(productId))
            {
                return;
            }

            await SaveAsync(productIds, cancellationToken);
        }
        finally
        {
            _pathGate.Release();
        }
    }

    public async Task RemoveAsync(
        string productId,
        CancellationToken cancellationToken = default)
    {
        ValidateProductId(productId);

        await _pathGate.WaitAsync(cancellationToken);
        try
        {
            var productIds = await LoadMutableAsync(cancellationToken);
            if (!productIds.Remove(productId))
            {
                return;
            }

            await SaveAsync(productIds, cancellationToken);
        }
        finally
        {
            _pathGate.Release();
        }
    }

    private async Task<HashSet<string>> LoadMutableAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_libraryPath))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        try
        {
            await using var stream = new FileStream(
                _libraryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16384,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var state = await JsonSerializer.DeserializeAsync<ProductLibraryState>(
                stream,
                SerializerOptions,
                cancellationToken);
            return ValidateState(state);
        }
        catch (JsonException exception)
        {
            throw new ProductLibraryException(
                "Product library state contains invalid JSON.",
                exception);
        }
    }

    private async Task SaveAsync(
        IReadOnlySet<string> productIds,
        CancellationToken cancellationToken)
    {
        var stateRoot = Path.GetDirectoryName(_libraryPath)
            ?? throw new ProductLibraryException("Product library path does not have a parent directory.");
        Directory.CreateDirectory(stateRoot);
        var temporaryPath = $"{_libraryPath}.{Guid.NewGuid():N}.tmp";
        var state = new ProductLibraryState
        {
            SchemaVersion = CurrentSchemaVersion,
            ProductIds = productIds.Order(StringComparer.Ordinal).ToArray()
        };

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

            File.Move(temporaryPath, _libraryPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static HashSet<string> ValidateState(ProductLibraryState? state)
    {
        if (state is null ||
            state.SchemaVersion != CurrentSchemaVersion ||
            state.ProductIds is null)
        {
            throw new ProductLibraryException("Product library state is invalid.");
        }

        var productIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var productId in state.ProductIds)
        {
            ValidateProductId(productId);
            if (!productIds.Add(productId))
            {
                throw new ProductLibraryException(
                    $"Product library state contains duplicate product id '{productId}'.");
            }
        }

        return productIds;
    }

    private static void ValidateProductId(string? productId)
    {
        if (string.IsNullOrWhiteSpace(productId) ||
            !productId.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'))
        {
            throw new ProductLibraryException(
                $"Product id '{productId}' cannot be stored in the product library.");
        }
    }

    private sealed record ProductLibraryState
    {
        public required int SchemaVersion { get; init; }

        public required string[] ProductIds { get; init; }
    }
}
