using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Globalization;
using System.Windows;
using Nfg.Store.App.Localization;
using Nfg.Store.App.Services;
using Nfg.Store.App.ViewModels;
using Nfg.Store.Core;
using Nfg.Store.Installation;

namespace Nfg.Store.App;

public partial class App : Application
{
    private static readonly Uri CatalogUri = new(
        "https://raw.githubusercontent.com/Aneonfas/nfg-hub-catalog/main/v2/catalog.json");

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
        var localization = LocalizationService.Instance;
        localization.SetLanguage(LocalizationService.DetectLanguage(CultureInfo.CurrentUICulture));

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
            var cacheRoot = Path.Combine(dataRoot, "cache", "catalog-v2");
            var bundledCatalogRoot = Path.Combine(AppContext.BaseDirectory, "catalog-v2");
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
                localization.SetLanguage(settings.UiLanguage ?? localization.CurrentLanguage);
            }
            catch (InvalidDataException exception)
            {
                settings = new AppSettings();
                var settingsWarning = localization.Format(
                    "Startup.SettingsWarning",
                    exception.Message,
                    settingsStore.SettingsPath);
                startupWarning = settingsWarning;
            }

            var libraryStore = new ProductLibraryStore(dataRoot);
            var libraryProductIds = new HashSet<string>(
                await libraryStore.LoadAsync(),
                StringComparer.Ordinal);
            var discoveredStates = await stateStore.LoadAllAsync();
            var requiredProductIds = InstallationCatalogRequirements.UnionProductIds(
                libraryProductIds,
                discoveredStates);
            var catalogService = new CatalogService(_httpClient);
            var catalogResult = settings.CheckUpdatesAutomatically && !isStartupSmoke
                ? await catalogService.LoadRemoteFirstAsync(
                    CatalogUri,
                    cacheRoot,
                    bundledCatalogRoot,
                    requiredProductIds: requiredProductIds)
                : await catalogService.LoadLocalFirstAsync(
                    CatalogUri,
                    cacheRoot,
                    bundledCatalogRoot,
                    requiredProductIds: requiredProductIds);
            var migratedStates = await new InstallationStateMigrator(stateStore)
                .MigrateAsync(catalogResult.Catalog);
            var installedStates = migratedStates.ToDictionary(
                state => state.InstallationKey!,
                state => (InstalledProductState?)state,
                StringComparer.Ordinal);

            foreach (var installedState in migratedStates)
            {
                if (libraryProductIds.Add(installedState.ProductId))
                {
                    await libraryStore.AddAsync(installedState.ProductId);
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
                    localization.Get("Startup.WarningTitle"),
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
                localization.Format("Startup.Error", details),
                localization.Get("Startup.ErrorTitle"),
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
