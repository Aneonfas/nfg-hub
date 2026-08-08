using Microsoft.Win32;

namespace Nfg.Store.Platform.Windows;

public sealed class SteamGameLocator(
    IEnumerable<string>? additionalLibraryRoots = null,
    bool includeConfiguredSteam = true)
{
    private readonly IReadOnlyList<string> _additionalLibraryRoots =
        additionalLibraryRoots?.ToArray() ?? [];

    public IReadOnlyList<SteamGameInstallation> Find(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        if (!appId.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("Steam App ID must contain decimal digits only.", nameof(appId));
        }

        var installations = new List<SteamGameInstallation>();
        foreach (var libraryRoot in FindLibraryRoots())
        {
            var manifestPath = Path.Combine(libraryRoot, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                var installation = ReadInstallation(manifestPath, libraryRoot);
                if (installation.AppId.Equals(appId, StringComparison.Ordinal))
                {
                    installations.Add(installation);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (FormatException)
            {
            }
        }

        return installations;
    }

    private IEnumerable<string> FindLibraryRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in _additionalLibraryRoots)
        {
            AddExistingDirectory(roots, root);
        }

        if (includeConfiguredSteam)
        {
            foreach (var steamRoot in FindSteamRoots())
            {
                AddExistingDirectory(roots, steamRoot);
                AddLibrariesFromVdf(roots, steamRoot);
            }
        }

        return roots;
    }

    private static IEnumerable<string> FindSteamRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ReadRegistryRoot(roots, RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam");
        ReadRegistryRoot(roots, RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam");
        ReadRegistryRoot(roots, RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Valve\Steam");

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            AddExistingDirectory(roots, Path.Combine(programFilesX86, "Steam"));
        }

        return roots;
    }

    private static void ReadRegistryRoot(
        ISet<string> roots,
        RegistryHive hive,
        RegistryView view,
        string subKey)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey);
            if (key?.GetValue("SteamPath") is string steamPath)
            {
                AddExistingDirectory(roots, steamPath);
            }

            if (key?.GetValue("InstallPath") is string installPath)
            {
                AddExistingDirectory(roots, installPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void AddLibrariesFromVdf(ISet<string> roots, string steamRoot)
    {
        var path = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var parsed = ValveKeyValuesParser.Parse(File.ReadAllText(path));
            if (!TryGetObject(parsed, "libraryfolders", out var libraries))
            {
                return;
            }

            foreach (var value in libraries.Values)
            {
                if (value is IReadOnlyDictionary<string, object> item &&
                    TryGetString(item, "path", out var libraryPath))
                {
                    AddExistingDirectory(roots, libraryPath);
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or FormatException)
        {
        }
    }

    private static SteamGameInstallation ReadInstallation(string manifestPath, string libraryRoot)
    {
        var parsed = ValveKeyValuesParser.Parse(File.ReadAllText(manifestPath));
        if (!TryGetObject(parsed, "AppState", out var appState) ||
            !TryGetString(appState, "appid", out var appId) ||
            !TryGetString(appState, "installdir", out var installDirectoryName) ||
            !TryGetString(appState, "buildid", out var buildId))
        {
            throw new FormatException($"Steam app manifest '{manifestPath}' is incomplete.");
        }

        var commonRoot = Path.GetFullPath(Path.Combine(libraryRoot, "steamapps", "common"));
        var gameRoot = Path.GetFullPath(Path.Combine(commonRoot, installDirectoryName));
        var relative = Path.GetRelativePath(commonRoot, gameRoot);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new FormatException("Steam install directory resolves outside steamapps/common.");
        }

        TryGetString(appState, "TargetBuildID", out var targetBuildId);
        string? language = null;
        if (TryGetObject(appState, "UserConfig", out var userConfig) &&
            TryGetString(userConfig, "language", out var configuredLanguage))
        {
            language = configuredLanguage;
        }

        return new SteamGameInstallation(
            appId,
            buildId,
            targetBuildId,
            language,
            installDirectoryName,
            gameRoot,
            Path.GetFullPath(manifestPath));
    }

    private static bool TryGetObject(
        IReadOnlyDictionary<string, object> values,
        string key,
        out IReadOnlyDictionary<string, object> result)
    {
        if (values.TryGetValue(key, out var value) &&
            value is IReadOnlyDictionary<string, object> dictionary)
        {
            result = dictionary;
            return true;
        }

        result = new Dictionary<string, object>();
        return false;
    }

    private static bool TryGetString(
        IReadOnlyDictionary<string, object> values,
        string key,
        out string result)
    {
        if (values.TryGetValue(key, out var value) && value is string text)
        {
            result = text;
            return true;
        }

        result = string.Empty;
        return false;
    }

    private static void AddExistingDirectory(ISet<string> roots, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var normalized = Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(normalized))
        {
            roots.Add(normalized);
        }
    }
}
