using System.IO;
using Nfg.Store.App.Services;
using Nfg.Store.App.Localization;
using Nfg.Store.Core;

namespace Nfg.Store.App.ViewModels;

public sealed class SettingsViewModel : PageViewModel
{
    private readonly CatalogLoadResult _catalogResult;
    private readonly AppSettingsStore _settingsStore;
    private readonly bool _checkedCatalogRemotelyAtStartup;
    private bool _checkUpdatesAutomatically;
    private LanguageOption _selectedLanguage;
    private string _settingsSaveError = string.Empty;

    public SettingsViewModel(
        CatalogLoadResult catalogResult,
        string dataPath,
        AppSettings settings,
        AppSettingsStore settingsStore)
        : base("Nav.Settings", localizeTitle: true)
    {
        _catalogResult = catalogResult;
        _settingsStore = settingsStore;
        _checkedCatalogRemotelyAtStartup = settings.CheckUpdatesAutomatically;
        _checkUpdatesAutomatically = settings.CheckUpdatesAutomatically;
        _selectedLanguage = LocalizationService.SupportedLanguages.First(language =>
            language.Code == LocalizationService.Instance.CurrentLanguage);
        DataPath = dataPath;
    }

    public string DataPath { get; }

    public string CatalogSource => _catalogResult.Source switch
    {
        CatalogSourceKind.Remote => _catalogResult.RemoteUri.ToString(),
        CatalogSourceKind.Cache => Text.Format("Settings.Source.Cache", _catalogResult.CachePath),
        _ => Text.Get("Settings.Source.Bundled")
    };

    public string CatalogSourceDescription => _catalogResult.Source switch
    {
        CatalogSourceKind.Remote => Text.Get("Settings.Source.Remote.Description"),
        CatalogSourceKind.Cache when !_checkedCatalogRemotelyAtStartup =>
            Text.Get("Settings.Source.Cache.AutoOff"),
        CatalogSourceKind.Cache => Text.Get("Settings.Source.Cache.Fallback"),
        _ when !_checkedCatalogRemotelyAtStartup =>
            Text.Get("Settings.Source.Bundled.AutoOff"),
        _ => Text.Get("Settings.Source.Bundled.Fallback")
    };

    public IReadOnlyList<LanguageOption> Languages => LocalizationService.SupportedLanguages;

    public LanguageOption SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (value is null || !SetProperty(ref _selectedLanguage, value))
            {
                return;
            }

            var previousLanguage = LocalizationService.Instance.CurrentLanguage;
            LocalizationService.Instance.SetLanguage(value.Code);
            if (!TrySaveSettings())
            {
                LocalizationService.Instance.SetLanguage(previousLanguage);
                _selectedLanguage = Languages.First(language => language.Code == previousLanguage);
                OnPropertyChanged(nameof(SelectedLanguage));
            }
        }
    }

    public string SettingsSaveError
    {
        get => _settingsSaveError;
        private set
        {
            if (SetProperty(ref _settingsSaveError, value))
            {
                OnPropertyChanged(nameof(HasSettingsSaveError));
            }
        }
    }

    public bool HasSettingsSaveError => !string.IsNullOrEmpty(SettingsSaveError);

    public bool CheckUpdatesAutomatically
    {
        get => _checkUpdatesAutomatically;
        set
        {
            var previousValue = _checkUpdatesAutomatically;
            if (!SetProperty(ref _checkUpdatesAutomatically, value))
            {
                return;
            }

            try
            {
                if (!TrySaveSettings())
                {
                    _checkUpdatesAutomatically = previousValue;
                    OnPropertyChanged(nameof(CheckUpdatesAutomatically));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                HandleSaveError(exception);
            }
        }
    }

    private bool TrySaveSettings()
    {
        try
        {
            _settingsStore.Save(new AppSettings
            {
                CheckUpdatesAutomatically = _checkUpdatesAutomatically,
                UiLanguage = LocalizationService.Instance.CurrentLanguage
            });
            SettingsSaveError = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            HandleSaveError(exception);
            return false;
        }
    }

    private void HandleSaveError(Exception exception) =>
        SettingsSaveError = Text.Format("Settings.SaveError", exception.Message);
}
