using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nfg.Store.App.Localization;

namespace Nfg.Store.App.Services;

public sealed record AppSettings
{
    [JsonRequired]
    public int SchemaVersion { get; init; } = 1;

    [JsonRequired]
    public bool CheckUpdatesAutomatically { get; init; } = true;

    public string? UiLanguage { get; init; }
}

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowDuplicateProperties = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public AppSettingsStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _settingsPath = Path.Combine(
            Path.GetFullPath(dataRoot),
            "state",
            "settings.json");
    }

    public string SettingsPath => _settingsPath;

    public AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        try
        {
            using var stream = File.OpenRead(_settingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(stream, SerializerOptions);
            Validate(settings);
            return settings!;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                LocalizationService.Instance.Get("Settings.InvalidJson"),
                exception);
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings);

        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_settingsPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, settings, SerializerOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void Validate(AppSettings? settings)
    {
        if (settings is not { SchemaVersion: 1 })
        {
            throw new InvalidDataException(
                LocalizationService.Instance.Get("Settings.UnsupportedSchema"));
        }

        if (settings.UiLanguage is not null &&
            !LocalizationService.IsSupported(settings.UiLanguage))
        {
            throw new InvalidDataException(
                LocalizationService.Instance.Get("Settings.UnsupportedLanguage"));
        }
    }
}
