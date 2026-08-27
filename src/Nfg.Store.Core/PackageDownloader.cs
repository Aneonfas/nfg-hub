using System.Buffers;
using System.Security.Cryptography;
using Nfg.Store.Contracts;

namespace Nfg.Store.Core;

public sealed record PackageDownloadProgress(long BytesReceived, long TotalBytes)
{
    public double Percentage => TotalBytes == 0
        ? 0
        : Math.Min(100, BytesReceived * 100d / TotalBytes);
}

public sealed class PackageDownloader(HttpClient httpClient)
{
    /// <summary>Finds an intact cached ZIP without creating directories or using the network.</summary>
    public async Task<string?> TryGetCachedAsync(
        ProductManifest product,
        string downloadRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadRoot);
        var payload = product.Release.Payload
            ?? throw new PackageDownloadException(
                $"Product '{product.Id}' does not have a downloadable payload.");
        var productDirectory = ResolveInsideRoot(
            Path.GetFullPath(downloadRoot),
            SanitizeSegment(product.Id),
            SanitizeSegment(product.Release.Version));
        var path = Path.Combine(productDirectory, $"{payload.Sha256.ToLowerInvariant()}.zip");
        return await MatchesPayloadAsync(path, payload, cancellationToken) ? path : null;
    }

    public async Task<string> DownloadAsync(
        ProductManifest product,
        string downloadRoot,
        IProgress<PackageDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadRoot);

        var payload = product.Release.Payload
            ?? throw new PackageDownloadException(
                $"Product '{product.Id}' does not have a downloadable payload.");

        var root = Path.GetFullPath(downloadRoot);
        var productDirectory = ResolveInsideRoot(
            root,
            SanitizeSegment(product.Id),
            SanitizeSegment(product.Release.Version));
        Directory.CreateDirectory(productDirectory);

        var finalPath = Path.Combine(productDirectory, $"{payload.Sha256.ToLowerInvariant()}.zip");
        if (await MatchesPayloadAsync(finalPath, payload, cancellationToken))
        {
            progress?.Report(new PackageDownloadProgress(payload.SizeBytes, payload.SizeBytes));
            return finalPath;
        }

        var temporaryPath = Path.Combine(productDirectory, $"{Guid.NewGuid():N}.part");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, payload.Url);

            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is { } contentLength &&
                contentLength != payload.SizeBytes)
            {
                throw new PackageDownloadException(
                    $"Package size from the server is {contentLength}, expected {payload.SizeBytes}.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            long bytesReceived = 0;

            try
            {
                while (true)
                {
                    var count = await source.ReadAsync(buffer, cancellationToken);
                    if (count == 0)
                    {
                        break;
                    }

                    bytesReceived += count;
                    if (bytesReceived > payload.SizeBytes)
                    {
                        throw new PackageDownloadException(
                            $"Package exceeded its declared size of {payload.SizeBytes} bytes.");
                    }

                    hash.AppendData(buffer, 0, count);
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    progress?.Report(new PackageDownloadProgress(bytesReceived, payload.SizeBytes));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            await destination.FlushAsync(cancellationToken);

            if (bytesReceived != payload.SizeBytes)
            {
                throw new PackageDownloadException(
                    $"Downloaded package has {bytesReceived} bytes, expected {payload.SizeBytes}.");
            }

            var actualHash = Convert.ToHexString(hash.GetHashAndReset());
            if (!actualHash.Equals(payload.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new PackageDownloadException(
                    $"Downloaded package SHA-256 is {actualHash.ToLowerInvariant()}, expected {payload.Sha256.ToLowerInvariant()}.");
            }

            destination.Close();
            File.Move(temporaryPath, finalPath, overwrite: true);
            progress?.Report(new PackageDownloadProgress(payload.SizeBytes, payload.SizeBytes));
            return finalPath;
        }
        catch (PackageDownloadException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new PackageDownloadException(
                $"Package for '{product.Id}' could not be downloaded.",
                exception);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task<bool> MatchesPayloadAsync(
        string path,
        ProductPayload payload,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != payload.SizeBytes)
        {
            return false;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(digest).Equals(payload.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveInsideRoot(string root, params string[] segments)
    {
        var path = Path.GetFullPath(segments.Aggregate(root, Path.Combine));
        var relative = Path.GetRelativePath(root, path);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new PackageDownloadException("Download path resolves outside the Hub data directory.");
        }

        return path;
    }

    private static string SanitizeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized)
            ? throw new PackageDownloadException("Package path segment is empty.")
            : sanitized;
    }
}
