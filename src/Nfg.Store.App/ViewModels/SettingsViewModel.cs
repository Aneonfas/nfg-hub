using System.IO;
using Nfg.Store.App.Services;
using Nfg.Store.Core;

namespace Nfg.Store.App.ViewModels;

public sealed class SettingsViewModel : PageViewModel
{
    private readonly CatalogLoadResult _catalogResult;
    private readonly AppSettingsStore _settingsStore;
    private readonly bool _checkedCatalogRemotelyAtStartup;
    private bool _checkUpdatesAutomatically;
    private string _settingsSaveError = string.Empty;

    public SettingsViewModel(
        CatalogLoadResult catalogResult,
        string dataPath,
        AppSettings settings,
        AppSettingsStore settingsStore)
        : base("Настройки")
    {
        _catalogResult = catalogResult;
        _settingsStore = settingsStore;
        _checkedCatalogRemotelyAtStartup = settings.CheckUpdatesAutomatically;
        _checkUpdatesAutomatically = settings.CheckUpdatesAutomatically;
        DataPath = dataPath;
    }

    public string DataPath { get; }

    public string CatalogSource => _catalogResult.Source switch
    {
        CatalogSourceKind.Remote => _catalogResult.RemoteUri.ToString(),
        CatalogSourceKind.Cache => $"Кэш · {_catalogResult.CachePath}",
        _ => "Встроенный резервный каталог"
    };

    public string CatalogSourceDescription => _catalogResult.Source switch
    {
        CatalogSourceKind.Remote => "Каталог получен из GitHub и сохранён в локальный кэш.",
        CatalogSourceKind.Cache when !_checkedCatalogRemotelyAtStartup =>
            "Автоматическая проверка при запуске отключена — используется последняя проверенная копия из кэша.",
        CatalogSourceKind.Cache => "Сеть недоступна или каталог некорректен — используется последняя проверенная копия.",
        _ when !_checkedCatalogRemotelyAtStartup =>
            "Автоматическая проверка при запуске отключена, а локальный кэш недоступен — используется каталог из поставки приложения.",
        _ => "Сеть и локальный кэш недоступны — используется каталог из поставки приложения."
    };

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
                _settingsStore.Save(new AppSettings
                {
                    CheckUpdatesAutomatically = value
                });
                SettingsSaveError = string.Empty;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _checkUpdatesAutomatically = previousValue;
                OnPropertyChanged(nameof(CheckUpdatesAutomatically));
                SettingsSaveError = $"Не удалось сохранить настройку: {exception.Message}";
            }
        }
    }
}
