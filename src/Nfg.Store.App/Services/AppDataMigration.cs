using System.IO;

namespace Nfg.Store.App.Services;

public sealed record AppDataResolution(string DataRoot);

public static class AppDataMigration
{
    public static AppDataResolution Resolve(string localApplicationDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationDataRoot);

        var nfgRoot = Path.Combine(Path.GetFullPath(localApplicationDataRoot), "NFG");
        var hubRoot = Path.Combine(nfgRoot, "Hub");
        var legacyRoot = Path.Combine(nfgRoot, "Store");
        if (Directory.Exists(hubRoot) || !Directory.Exists(legacyRoot))
        {
            return new AppDataResolution(hubRoot);
        }

        try
        {
            CopyLegacyDataAtomically(legacyRoot, hubRoot);
            return new AppDataResolution(hubRoot);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                "Не удалось перенести данные NFG Store в новую папку NFG Hub. " +
                "Запуск остановлен, чтобы сохранить исходные данные без изменений.",
                exception);
        }
    }

    private static void CopyLegacyDataAtomically(string legacyRoot, string hubRoot)
    {
        var parentRoot = Path.GetDirectoryName(hubRoot)
            ?? throw new IOException("Не удалось определить папку данных NFG Hub.");
        Directory.CreateDirectory(parentRoot);

        var temporaryRoot = Path.Combine(
            parentRoot,
            $".Hub.migration-{Guid.NewGuid():N}");
        try
        {
            CopyDirectory(legacyRoot, temporaryRoot);
            try
            {
                Directory.Move(temporaryRoot, hubRoot);
            }
            catch (IOException) when (Directory.Exists(hubRoot))
            {
                // Another Hub process completed the same migration first.
            }
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    private static void CopyDirectory(string sourceRoot, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);

        foreach (var directory in Directory.EnumerateDirectories(sourceRoot))
        {
            CopyDirectory(
                directory,
                Path.Combine(destinationRoot, Path.GetFileName(directory)));
        }

        foreach (var file in Directory.EnumerateFiles(sourceRoot))
        {
            File.Copy(file, Path.Combine(destinationRoot, Path.GetFileName(file)));
        }
    }
}
