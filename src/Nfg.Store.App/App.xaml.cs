using System.IO;
using System.Net.Http;
using System.Reflection;
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
    private string? _startupSmokeRoot;

    public App()
    {
        var informationalVersion = typeof(App).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var productVersion = informationalVersion?.Split('+', 2)[0] ?? "0.0.0";
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"NFG-Hub/{productVersion}");
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var isStartupSmoke = e.Args.Contains("--startup-smoke", StringComparer.Ordinal);

        try
        {
            var localApplicationDataRoot = isStartupSmoke
                ? _startupSmokeRoot = Path.Combine(
                    Path.GetTempPath(),
                    $"NFG-Hub-startup-smoke-{Environment.ProcessId}")
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dataResolution = AppDataMigration.Resolve(
                localApplicationDataRoot);
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
            var catalogResult = settings.CheckUpdatesAutomatically && !isStartupSmoke
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
            if (isStartupSmoke)
            {
                await Dispatcher.InvokeAsync(() => Shutdown(0));
                return;
            }
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
            if (isStartupSmoke)
            {
                Console.Error.WriteLine(details);
                Shutdown(1);
                return;
            }
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
        if (_startupSmokeRoot is not null && Directory.Exists(_startupSmokeRoot))
        {
            Directory.Delete(_startupSmokeRoot, recursive: true);
        }
        base.OnExit(e);
    }
}
