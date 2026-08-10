using System.Collections.Concurrent;

namespace Nfg.Store.Installation;

internal sealed class InstallationOperationLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessGates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _dataRoot;
    private readonly string _lockRoot;

    public InstallationOperationLock(string dataRoot)
    {
        _dataRoot = Path.GetFullPath(dataRoot);
        _lockRoot = Path.Combine(_dataRoot, "state", "locks");
    }

    public async Task<T> ExecuteAsync<T>(
        string installationKey,
        CancellationToken cancellationToken,
        Func<Task<T>> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationKey);
        ArgumentNullException.ThrowIfNull(operation);
        InstallationStateStore.SanitizeInstallationKey(installationKey);

        var processKey = $"{_dataRoot}|{installationKey}";
        var gate = ProcessGates.GetOrAdd(processKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var interprocessLock = await AcquireInterprocessLockAsync(
                installationKey,
                cancellationToken);
            return await operation();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<FileStream> AcquireInterprocessLockAsync(
        string installationKey,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_lockRoot);
        var lockPath = Path.Combine(_lockRoot, $"{installationKey}.lock");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.Asynchronous);
            }
            catch (IOException exception) when (IsSharingOrLockViolation(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }
        }
    }

    private static bool IsSharingOrLockViolation(IOException exception)
    {
        const int SharingViolation = 32;
        const int LockViolation = 33;
        var errorCode = exception.HResult & 0xFFFF;
        return errorCode is SharingViolation or LockViolation;
    }
}
