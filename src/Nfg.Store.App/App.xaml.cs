using System.IO;
using System.Net.Http;
using System.Windows;
using Nfg.Store.App.Services;
using Nfg.Store.App.ViewModels;
using Nfg.Store.Core;
using Nfg.Store.Installation;

namespace Nfg.Store.App;

public partial class App : Application
{
    private static readonly Uri CatalogUri = new(
        "https://raw.githubusercontent.com/Aneonfas/nfg-hub-catalog/main/catalog.json");

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(2)
    };

    public App()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("NFG-Hub/0.1");
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var dataResolution = AppDataMigration.Resolve(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            var dataRoot = dataResolution.DataRoot;
            var cacheRoot = Path.Combine(dataRoot, "cache", "catalog");
            var bundledCatalogRoot = Path.Combine(AppContext.BaseDirectory, "catalog");
            var stateStore = new InstallationStateStore(dataRoot);
            var installationCoordinator = new ProductInstallationCoordinator(
                _httpClient,
                dataRoot,
                stateStore);
            await installationCoordinator.RecoverPendingOperationsAsync();

            var settingsStore = new AppSettingsStore(dataRoot);
            AppSettings settings;
            string? startupWarning = null;
            try
            {
                settings = settingsStore.Load();
            }
            catch (InvalidDataException exception)
            {
                settings = new AppSettings();
                var settingsWarning =
                    $"Файл настроек повреждён или имеет неподдерживаемую версию. " +
                    $"В этом запуске используются настройки по умолчанию.\n\n" +
                    $"{exception.Message}\n\n" +
                    $"Исходный файл сохранён без изменений:\n{settingsStore.SettingsPath}";
                startupWarning = settingsWarning;
            }

            var libraryStore = new ProductLibraryStore(dataRoot);
            var libraryProductIds = new HashSet<string>(
                await libraryStore.LoadAsync(),
                StringComparer.Ordinal);
            var catalogService = new CatalogService(_httpClient);
            var catalogResult = settings.CheckUpdatesAutomatically
                ? await catalogService.LoadRemoteFirstAsync(
                    CatalogUri,
                    cacheRoot,
                    bundledCatalogRoot,
                    requiredProductIds: libraryProductIds)
                : await catalogService.LoadLocalFirstAsync(
                    CatalogUri,
                    cacheRoot,
                    bundledCatalogRoot,
                    requiredProductIds: libraryProductIds);
            var installedStates = new Dictionary<string, InstalledProductState?>(
                StringComparer.Ordinal);

            foreach (var product in catalogResult.Catalog.Products)
            {
                var installedState = await stateStore.LoadAsync(product.Id);
                installedStates[product.Id] = installedState;
                if (installedState is not null && libraryProductIds.Add(product.Id))
                {
                    await libraryStore.AddAsync(product.Id);
                }
            }

            var window = new MainWindow
            {
                DataContext = new MainWindowViewModel(
                    catalogResult,
                    dataRoot,
                    installationCoordinator,
                    libraryStore,
                    settings,
                    settingsStore,
                    installedStates,
                    libraryProductIds)
            };

            MainWindow = window;
            window.Show();
            if (startupWarning is not null)
            {
                MessageBox.Show(
                    window,
                    startupWarning,
                    "Предупреждение NFG Hub",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception exception)
        {
            var rootCause = exception.GetBaseException();
            var details = ReferenceEquals(rootCause, exception)
                ? exception.Message
                : $"{exception.Message}\n\n{rootCause.Message}";
            MessageBox.Show(
                $"NFG Hub не смог запуститься.\n\n{details}",
                "Ошибка запуска",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _httpClient.Dispose();
        base.OnExit(e);
    }
}
