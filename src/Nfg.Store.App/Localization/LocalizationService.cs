using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Nfg.Store.App.Localization;

public sealed class LocalizationService : INotifyPropertyChanged
{
    private CultureInfo _culture = CultureInfo.GetCultureInfo("en");
    private string _languageCode = "en";

    private LocalizationService()
    {
    }

    public static LocalizationService Instance { get; } = new();

    public static IReadOnlyList<LanguageOption> SupportedLanguages { get; } =
    [
        new("en", "English"),
        new("ru", "Русский"),
        new("es", "Español"),
        new("de", "Deutsch"),
        new("fr", "Français"),
        new("pt-br", "Português (Brasil)"),
        new("zh-cn", "简体中文"),
        new("ja", "日本語"),
        new("ko", "한국어"),
        new("tr", "Türkçe")
    ];

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? LanguageChanged;

    public string CurrentLanguage => _languageCode;

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

        _languageCode = normalized;
        _culture = CultureInfo.GetCultureInfo(normalized switch
        {
            "pt-br" => "pt-BR",
            "zh-cn" => "zh-CN",
            _ => normalized
        });
        CultureInfo.CurrentUICulture = _culture;
        CultureInfo.DefaultThreadCurrentUICulture = _culture;
        OnPropertyChanged(nameof(CurrentLanguage));
        OnPropertyChanged("Item[]");
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public static string DetectLanguage(CultureInfo culture) =>
        NormalizeLanguage(culture.Name);

    public static string NormalizeLanguage(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return "en";
        }

        var normalized = languageCode.Trim().Replace('_', '-').ToLowerInvariant();
        var primary = normalized.Split('-')[0];
        return primary switch
        {
            "ru" => "ru",
            "es" => "es",
            "de" => "de",
            "fr" => "fr",
            "pt" => "pt-br",
            "zh" when normalized is "zh" or "zh-cn" or "zh-hans" or "zh-sg" => "zh-cn",
            "ja" => "ja",
            "ko" => "ko",
            "tr" => "tr",
            _ => "en"
        };
    }

    public static bool IsSupported(string? languageCode) =>
        !string.IsNullOrWhiteSpace(languageCode) &&
        SupportedLanguages.Any(language =>
            string.Equals(language.Code, languageCode, StringComparison.OrdinalIgnoreCase));

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
