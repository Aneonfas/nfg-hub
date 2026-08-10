using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Nfg.Store.App.Localization;

public sealed class LocalizationService : INotifyPropertyChanged
{
    private CultureInfo _culture = CultureInfo.GetCultureInfo("en");

    private LocalizationService()
    {
    }

    public static LocalizationService Instance { get; } = new();

    public static IReadOnlyList<LanguageOption> SupportedLanguages { get; } =
    [
        new("en", "English"),
        new("ru", "Русский"),
        new("es", "Español")
    ];

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? LanguageChanged;

    public string CurrentLanguage => _culture.TwoLetterISOLanguageName;

    public string this[string key] => UiStrings.Get(CurrentLanguage, key);

    public string Get(string key) => this[key];

    public string Format(string key, params object?[] arguments) =>
        string.Format(_culture, this[key], arguments);

    public void SetLanguage(string? languageCode)
    {
        var normalized = NormalizeLanguage(languageCode);
        if (string.Equals(CurrentLanguage, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _culture = CultureInfo.GetCultureInfo(normalized);
        CultureInfo.CurrentUICulture = _culture;
        CultureInfo.DefaultThreadCurrentUICulture = _culture;
        OnPropertyChanged(nameof(CurrentLanguage));
        OnPropertyChanged("Item[]");
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public static string DetectLanguage(CultureInfo culture) =>
        NormalizeLanguage(culture.TwoLetterISOLanguageName);

    public static string NormalizeLanguage(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return "en";
        }

        var primary = languageCode.Trim().Split('-', '_')[0].ToLowerInvariant();
        return primary is "ru" or "es" ? primary : "en";
    }

    public static bool IsSupported(string? languageCode) =>
        !string.IsNullOrWhiteSpace(languageCode) &&
        SupportedLanguages.Any(language =>
            string.Equals(language.Code, languageCode, StringComparison.OrdinalIgnoreCase));

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
