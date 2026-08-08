using System.Windows;
using System.Windows.Input;
using Nfg.Store.App.Infrastructure;
using Nfg.Store.App.Services;
using Nfg.Store.Contracts;
using Nfg.Store.Core;
using Nfg.Store.Installation;

namespace Nfg.Store.App.ViewModels;

public sealed class ProductStateViewModel : PageViewModel
{
    private readonly ProductManifest _manifest;
    private readonly ProductInstallationCoordinator _installationCoordinator;
    private readonly ProductLibraryStore _libraryStore;
    private readonly AsyncRelayCommand _installCommand;
    private readonly AsyncRelayCommand _updateCommand;
    private readonly AsyncRelayCommand _removeFromDeviceCommand;
    private readonly AsyncRelayCommand _removeFromLibraryCommand;
    private readonly AsyncRelayCommand _toggleProductCommand;
    private ProductReleaseOptionViewModel? _recommendedVersion;
    private string? _detectedGameBuildId;
    private string? _managedGameRoot;
    private bool _isBusy;
    private bool _isInstallationInProgress;
    private bool _isUpdateInProgress;
    private bool _isCompatibilityPromptOpen;
    private bool _isInstalled;
    private bool _isInLibrary;
    private bool _operationHasError;
    private bool _isProductEnabled;
    private string? _managedVersion;
    private ProductReleaseOptionViewModel? _selectedVersion;
    private double _operationProgress;
    private string _operationStatus;

    public ProductStateViewModel(
        ProductViewModel product,
        ProductManifest manifest,
        ProductInstallationCoordinator installationCoordinator,
        ProductLibraryStore libraryStore,
        InstalledProductState? installedState,
        bool isInLibrary,
        string? detectedGameBuildId = null)
        : base(product.Title)
    {
        Product = product;
        FeaturedProduct = product;
        _manifest = manifest;
        _installationCoordinator = installationCoordinator;
        _libraryStore = libraryStore;
        _detectedGameBuildId = detectedGameBuildId;
        _managedVersion = installedState?.Version;
        _managedGameRoot = installedState?.GameRoot;
        _isInstalled = installedState is not null;
        _isInLibrary = isInLibrary || _isInstalled;
        _isProductEnabled = installedState?.IsEnabled == true;
        _operationStatus = string.Empty;

        var recommendedRelease = ProductReleaseCatalog.SelectRecommendedRelease(
            manifest,
            detectedGameBuildId);
        AvailableVersions = ProductReleaseCatalog.GetAvailableReleases(manifest)
            .Where(ProductReleaseCatalog.IsPublished)
            .Select(release => new ProductReleaseOptionViewModel(
                manifest,
                release,
                detectedGameBuildId,
                string.Equals(
                    release.Version,
                    recommendedRelease?.Version,
                    StringComparison.Ordinal)))
            .ToArray();
        _recommendedVersion = AvailableVersions.FirstOrDefault(version =>
            string.Equals(
                version.Version,
                recommendedRelease?.Version,
                StringComparison.Ordinal));
        _selectedVersion = _recommendedVersion ?? AvailableVersions.FirstOrDefault();

        _installCommand = new AsyncRelayCommand(
            InstallAsync,
            () => CanInstall,
            HandleOperationError);
        _updateCommand = new AsyncRelayCommand(
            UpdateAsync,
            () => CanUpdate,
            HandleUpdateError);
        _removeFromDeviceCommand = new AsyncRelayCommand(
            RemoveFromDeviceAsync,
            () => CanRemoveFromDevice,
            HandleOperationError);
        _removeFromLibraryCommand = new AsyncRelayCommand(
            RemoveFromLibraryAsync,
            () => CanRemoveFromLibrary,
            HandleOperationError);
        _toggleProductCommand = new AsyncRelayCommand(
            ToggleProductAsync,
            () => CanToggleProduct,
            HandleOperationError);

        InstallCommand = _installCommand;
        UpdateCommand = _updateCommand;
        RemoveFromDeviceCommand = _removeFromDeviceCommand;
        RemoveFromLibraryCommand = _removeFromLibraryCommand;
        ToggleProductCommand = _toggleProductCommand;
    }

    public ProductViewModel Product { get; }

    public ProductViewModel FeaturedProduct { get; }

    public ICommand InstallCommand { get; }

    public ICommand UpdateCommand { get; }

    public ICommand RemoveFromDeviceCommand { get; }

    public ICommand RemoveFromLibraryCommand { get; }

    public ICommand ToggleProductCommand { get; }

    public IReadOnlyList<ProductReleaseOptionViewModel> AvailableVersions { get; private set; }

    public bool HasMultipleVersions => AvailableVersions.Count > 1;

    public ProductReleaseOptionViewModel? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (value is null ||
                !AvailableVersions.Contains(value) ||
                !SetProperty(ref _selectedVersion, value))
            {
                return;
            }

            NotifySelectedVersionChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CatalogStatusText));
                RefreshActionState();
            }
        }
    }

    public bool IsInstallationInProgress
    {
        get => _isInstallationInProgress;
        private set
        {
            if (SetProperty(ref _isInstallationInProgress, value))
            {
                OnPropertyChanged(nameof(IsProgressVisible));
                OnPropertyChanged(nameof(CatalogStatusText));
                OnPropertyChanged(nameof(PrimaryActionText));
            }
        }
    }

    public bool IsUpdateInProgress
    {
        get => _isUpdateInProgress;
        private set
        {
            if (SetProperty(ref _isUpdateInProgress, value))
            {
                OnPropertyChanged(nameof(IsProgressVisible));
                OnPropertyChanged(nameof(CatalogStatusText));
                OnPropertyChanged(nameof(UpdateActionText));
            }
        }
    }

    public bool IsProgressVisible => IsInstallationInProgress || IsUpdateInProgress;

    public bool IsInstalled => _isInstalled;

    public bool IsNotInstalled => !IsInstalled;

    public bool IsInLibrary => _isInLibrary;

    public bool IsProductEnabled => _isProductEnabled;

    public string InstalledVersionLabel => _managedVersion ?? "—";

    public string SelectedVersionLabel => SelectedVersion?.Version ?? "—";

    public bool IsSelectedVersionExact => SelectedVersion?.IsExactlyCompatible == true;

    public bool IsSelectedVersionUnverified =>
        SelectedVersion is not null && !IsSelectedVersionExact;

    public string LocalCompatibilityLabel =>
        SelectedVersion?.CompatibilityLabel ?? "Нет доступной версии";

    public string CatalogVersionLabel =>
        _recommendedVersion?.Version ?? SelectedVersionLabel;

    public string CatalogCompatibilityLabel =>
        _recommendedVersion?.CompatibilityLabel ?? LocalCompatibilityLabel;

    public bool IsCatalogVersionUnverified =>
        _recommendedVersion is not null && !_recommendedVersion.IsExactlyCompatible;

    public bool HasSelectedVersionChange =>
        IsInstalled &&
        _managedVersion is not null &&
        SelectedVersion is not null &&
        !string.Equals(
            SelectedVersion.Version,
            _managedVersion,
            StringComparison.Ordinal);

    public bool HasRecommendedVersionChange =>
        IsInstalled &&
        _managedVersion is not null &&
        _recommendedVersion is not null &&
        !string.Equals(
            _recommendedVersion.Version,
            _managedVersion,
            StringComparison.Ordinal);

    // Kept for existing catalog bindings. A compatible release can be either
    // newer or older than the version currently installed on the device.
    public bool HasNewerCatalogVersion => HasRecommendedVersionChange;

    public bool IsUpdateAvailable =>
        HasSelectedVersionChange && SelectedVersion?.IsPublished == true;

    public bool CanInstall =>
        !IsBusy &&
        !IsInstalled &&
        SelectedVersion?.IsPublished == true;

    public bool CanUpdate => !IsBusy && IsUpdateAvailable;

    public bool CanSelectVersion => !IsBusy;

    public bool CanRemoveFromDevice => !IsBusy && IsInstalled;

    public bool CanRemoveFromLibrary => !IsBusy && IsInLibrary;

    public bool CanToggleProduct => !IsBusy && IsInstalled;

    public string ActivationStatusText => IsProductEnabled ? "Включён" : "Выключен";

    public string ToggleAutomationName => IsProductEnabled
        ? "Выключить продукт"
        : "Включить продукт";

    public string CatalogStatusText => IsUpdateInProgress
        ? "СМЕНА ВЕРСИИ"
        : IsInstallationInProgress
            ? "УСТАНОВКА"
            : HasRecommendedVersionChange
                ? "СМЕНИТЬ ВЕРСИЮ"
                : IsInstalled
                    ? "УСТАНОВЛЕНО"
                    : IsInLibrary
                        ? "В БИБЛИОТЕКЕ"
                        : IsCatalogVersionUnverified
                            ? "НЕ ПРОВЕРЕНО"
                            : "ДОСТУПНО";

    public string UpdateActionText => IsUpdateInProgress
        ? "Устанавливается…"
        : IsBusy
            ? "Выполняется…"
            : GetVersionSwitchActionText();

    public string UpdateVersionText => GetRecommendedVersionText();

    public string PrimaryActionText
    {
        get
        {
            if (IsInstallationInProgress)
            {
                return "Устанавливается…";
            }

            if (IsBusy)
            {
                return "Выполняется…";
            }

            if (IsInstalled)
            {
                return "Установлено";
            }

            if (SelectedVersion?.IsPublished != true)
            {
                return "Сборка готовится";
            }

            return IsSelectedVersionExact
                ? $"Установить {SelectedVersion.Version}"
                : "Установить всё равно";
        }
    }

    public string AvailabilityMessage
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(OperationStatus) &&
                (IsBusy || _operationHasError))
            {
                return OperationStatus;
            }

            if (IsInstalled && !string.IsNullOrWhiteSpace(OperationStatus))
            {
                return OperationStatus;
            }

            if (IsSelectedVersionUnverified)
            {
                return $"Совместимость версии {SelectedVersionLabel} не подтверждена: {LocalCompatibilityLabel}.";
            }

            if (HasRecommendedVersionChange)
            {
                return GetRecommendedAvailabilityMessage();
            }

            if (IsInstalled)
            {
                return OperationStatus;
            }

            return SelectedVersion?.IsPublished == true
                ? string.Empty
                : "Установка появится после публикации сборки и проверки совместимости.";
        }
    }

    public bool HasAvailabilityMessage =>
        !string.IsNullOrWhiteSpace(AvailabilityMessage);

    public string SelectedReleaseChangesTitle =>
        SelectedVersion?.ReleaseChangesTitle ?? "Изменения версии";

    public IReadOnlyList<string> SelectedReleaseHighlights =>
        SelectedVersion?.ReleaseHighlights ?? [];

    public IReadOnlyList<string> SelectedKnownIssues =>
        SelectedVersion?.KnownIssues ?? [];

    public string? SelectedNotesUrl => SelectedVersion?.NotesUrl;

    public string SelectedChannelLabel => SelectedVersion?.ChannelLabel ?? "—";

    public bool HasSelectedReleaseHighlights =>
        SelectedVersion?.HasReleaseHighlights == true;

    public bool HasSelectedKnownIssues =>
        SelectedVersion?.HasKnownIssues == true;

    public bool HasSelectedNotesUrl => SelectedVersion?.HasNotesUrl == true;

    public void RefreshGameCompatibility()
    {
        if (IsBusy || _isCompatibilityPromptOpen)
        {
            return;
        }

        var detectedGameBuildId = _installationCoordinator
            .DetectGameInstallation(_manifest, _managedGameRoot)
            ?.BuildId;
        if (string.Equals(
                detectedGameBuildId,
                _detectedGameBuildId,
                StringComparison.Ordinal))
        {
            return;
        }

        var selectedVersion = _selectedVersion?.Version;
        var followAutomaticChoice = _selectedVersion?.IsAutomaticChoice != false;
        _detectedGameBuildId = detectedGameBuildId;

        var recommendedRelease = ProductReleaseCatalog.SelectRecommendedRelease(
            _manifest,
            detectedGameBuildId);
        AvailableVersions = ProductReleaseCatalog.GetAvailableReleases(_manifest)
            .Where(ProductReleaseCatalog.IsPublished)
            .Select(release => new ProductReleaseOptionViewModel(
                _manifest,
                release,
                detectedGameBuildId,
                string.Equals(
                    release.Version,
                    recommendedRelease?.Version,
                    StringComparison.Ordinal)))
            .ToArray();
        _recommendedVersion = AvailableVersions.FirstOrDefault(version =>
            string.Equals(
                version.Version,
                recommendedRelease?.Version,
                StringComparison.Ordinal));
        _selectedVersion = followAutomaticChoice
            ? _recommendedVersion ?? AvailableVersions.FirstOrDefault()
            : AvailableVersions.FirstOrDefault(version =>
                  string.Equals(version.Version, selectedVersion, StringComparison.Ordinal))
              ?? _recommendedVersion
              ?? AvailableVersions.FirstOrDefault();

        OnPropertyChanged(nameof(AvailableVersions));
        OnPropertyChanged(nameof(HasMultipleVersions));
        OnPropertyChanged(nameof(SelectedVersion));
        OnPropertyChanged(nameof(CatalogVersionLabel));
        OnPropertyChanged(nameof(CatalogCompatibilityLabel));
        OnPropertyChanged(nameof(IsCatalogVersionUnverified));
        OnPropertyChanged(nameof(HasRecommendedVersionChange));
        OnPropertyChanged(nameof(HasNewerCatalogVersion));
        OnPropertyChanged(nameof(UpdateVersionText));
        NotifySelectedVersionChanged();
    }

    public double OperationProgress
    {
        get => _operationProgress;
        private set => SetProperty(ref _operationProgress, value);
    }

    public string OperationStatus
    {
        get => _operationStatus;
        private set
        {
            if (SetProperty(ref _operationStatus, value))
            {
                OnPropertyChanged(nameof(AvailabilityMessage));
                OnPropertyChanged(nameof(HasAvailabilityMessage));
            }
        }
    }

    private async Task InstallAsync()
    {
        var selectedManifest = GetSelectedManifest();
        if (!ConfirmUnverifiedCompatibility())
        {
            return;
        }

        _operationHasError = false;
        IsBusy = true;
        IsInstallationInProgress = true;
        OperationProgress = 0;
        OperationStatus = "Добавление в библиотеку…";

        try
        {
            if (!IsInLibrary)
            {
                await _libraryStore.AddAsync(_manifest.Id);
                SetLibraryState(isInLibrary: true);
            }

            OperationStatus = "Подготовка установки…";
            var progress = new Progress<ProductInstallProgress>(value =>
            {
                OperationProgress = value.Percentage;
                OperationStatus = value.Message;
            });
            var result = await _installationCoordinator.InstallAsync(selectedManifest, progress);
            SetManagedState(result.State);
            OperationProgress = 100;
            OperationStatus = result.Outcome switch
            {
                ManagedInstallOutcome.Adopted =>
                    "Уже установленные идентичные файлы приняты под управление NFG Hub.",
                ManagedInstallOutcome.AlreadyInstalled =>
                    $"{Product.Title} уже установлен и прошёл проверку.",
                _ => $"{Product.Title} успешно установлен."
            };
        }
        finally
        {
            IsInstallationInProgress = false;
            IsBusy = false;
        }
    }

    private async Task UpdateAsync()
    {
        var selectedManifest = GetSelectedManifest();
        if (!ConfirmUnverifiedCompatibility())
        {
            return;
        }

        var versionComparison = CompareSelectedToInstalledVersion();
        _operationHasError = false;
        IsBusy = true;
        IsUpdateInProgress = true;
        OperationProgress = 0;
        OperationStatus = "Подготовка смены версии…";

        try
        {
            var progress = new Progress<ProductInstallProgress>(value =>
            {
                OperationProgress = value.Percentage;
                OperationStatus = value.Message;
            });
            var result = await _installationCoordinator.SwitchVersionAsync(selectedManifest, progress);
            SetManagedState(result.State);
            OperationProgress = 100;
            OperationStatus = versionComparison > 0
                ? $"{Product.Title} обновлён до версии {result.State.Version}."
                : $"{Product.Title} переключён на версию {result.State.Version}.";
        }
        finally
        {
            IsUpdateInProgress = false;
            IsBusy = false;
        }
    }

    private async Task ToggleProductAsync()
    {
        _operationHasError = false;
        var enable = !IsProductEnabled;
        IsBusy = true;
        OperationProgress = 0;
        OperationStatus = enable
            ? "Включение продукта…"
            : "Выключение продукта…";

        try
        {
            var state = await _installationCoordinator.SetEnabledAsync(_manifest.Id, enable);
            SetManagedState(state);
            OperationStatus = state.IsEnabled
                ? "Продукт включён."
                : "Продукт выключен. Файлы сохранены на устройстве.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RemoveFromDeviceAsync()
    {
        _operationHasError = false;
        IsBusy = true;
        OperationProgress = 0;
        OperationStatus = "Проверка файлов продукта…";

        try
        {
            await _installationCoordinator.RemoveFromDeviceAsync(_manifest.Id);
            SetManagedState(null);
            OperationStatus = "Удалено с устройства. Продукт остаётся в библиотеке.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RemoveFromLibraryAsync()
    {
        _operationHasError = false;
        IsBusy = true;
        OperationProgress = 0;
        OperationStatus = IsInstalled
            ? "Удаление с устройства и из библиотеки…"
            : "Удаление из библиотеки…";

        try
        {
            if (IsInstalled)
            {
                await _installationCoordinator.RemoveFromDeviceAsync(_manifest.Id);
                SetManagedState(null);
            }

            await _libraryStore.RemoveAsync(_manifest.Id);
            SetLibraryState(isInLibrary: false);
            OperationStatus = string.Empty;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private ProductManifest GetSelectedManifest()
    {
        if (SelectedVersion?.IsPublished != true)
        {
            throw new InvalidOperationException("Выбранная версия недоступна для установки.");
        }

        return ProductReleaseCatalog.CreateManifestForRelease(
            _manifest,
            SelectedVersion.Release);
    }

    private bool ConfirmUnverifiedCompatibility()
    {
        if (SelectedVersion is null)
        {
            return true;
        }

        var selectedManifest = ProductReleaseCatalog.CreateManifestForRelease(
            _manifest,
            SelectedVersion.Release);
        var detectedGameBuildId = _installationCoordinator
            .DetectGameInstallation(selectedManifest, _managedGameRoot)
            ?.BuildId;
        if (ProductReleaseCatalog.IsExactlyCompatible(
                _manifest,
                SelectedVersion.Release,
                detectedGameBuildId))
        {
            return true;
        }

        var testedBuild = SelectedVersion.TestedGameBuildLabel is { } value
            ? $"Steam BuildID {value}"
            : "неуказанной сборке игры";
        var detectedBuildMessage = string.IsNullOrWhiteSpace(detectedGameBuildId)
            ? "Версию игры на этом устройстве определить не удалось."
            : $"На этом устройстве обнаружен Steam BuildID {detectedGameBuildId}.";
        var message =
            $"Версия {SelectedVersion.Version} проверена на {testedBuild}. " +
            $"{detectedBuildMessage}\n\n" +
            "Продукт, вероятно, будет работать, но совместимость не подтверждена. " +
            "Установить выбранную версию всё равно?";

        _isCompatibilityPromptOpen = true;
        try
        {
            return MessageBox.Show(
                message,
                "Совместимость не подтверждена",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) == MessageBoxResult.Yes;
        }
        finally
        {
            _isCompatibilityPromptOpen = false;
        }
    }

    private string GetVersionSwitchActionText()
    {
        if (!HasSelectedVersionChange)
        {
            return "Установлено";
        }

        if (SelectedVersion?.IsPublished != true)
        {
            return "Версия недоступна";
        }

        if (IsSelectedVersionUnverified)
        {
            return "Установить всё равно";
        }

        return CompareSelectedToInstalledVersion() > 0
            ? $"Обновить до {SelectedVersion.Version}"
            : $"Переключить на {SelectedVersion.Version}";
    }

    private string GetRecommendedVersionText()
    {
        if (!HasRecommendedVersionChange || _recommendedVersion is null)
        {
            return string.Empty;
        }

        if (!_recommendedVersion.IsExactlyCompatible)
        {
            return $"Доступна версия {_recommendedVersion.Version}; совместимость не подтверждена";
        }

        return CompareRecommendedToInstalledVersion() > 0
            ? $"Рекомендуется обновить до {_recommendedVersion.Version}"
            : $"Для вашей сборки рекомендуется {_recommendedVersion.Version}";
    }

    private string GetRecommendedAvailabilityMessage()
    {
        if (_recommendedVersion is null)
        {
            return string.Empty;
        }

        if (!_recommendedVersion.IsExactlyCompatible)
        {
            return $"Доступна версия {_recommendedVersion.Version}, но её совместимость с текущей сборкой игры не подтверждена.";
        }

        return CompareRecommendedToInstalledVersion() > 0
            ? $"Для текущей сборки игры рекомендуется обновление: {InstalledVersionLabel} → {_recommendedVersion.Version}."
            : $"Для текущей сборки игры рекомендуется версия {_recommendedVersion.Version} вместо {InstalledVersionLabel}.";
    }

    private int CompareSelectedToInstalledVersion() =>
        CompareToInstalledVersion(SelectedVersion?.Version);

    private int CompareRecommendedToInstalledVersion() =>
        CompareToInstalledVersion(_recommendedVersion?.Version);

    private int CompareToInstalledVersion(string? version)
    {
        return version is not null &&
               _managedVersion is not null &&
               SemanticVersionComparer.TryCompare(version, _managedVersion, out var comparison)
            ? comparison
            : 0;
    }

    private void NotifySelectedVersionChanged()
    {
        _operationHasError = false;
        OperationStatus = string.Empty;
        OnPropertyChanged(nameof(SelectedVersionLabel));
        OnPropertyChanged(nameof(IsSelectedVersionExact));
        OnPropertyChanged(nameof(IsSelectedVersionUnverified));
        OnPropertyChanged(nameof(LocalCompatibilityLabel));
        OnPropertyChanged(nameof(HasSelectedVersionChange));
        OnPropertyChanged(nameof(SelectedReleaseChangesTitle));
        OnPropertyChanged(nameof(SelectedReleaseHighlights));
        OnPropertyChanged(nameof(SelectedKnownIssues));
        OnPropertyChanged(nameof(SelectedNotesUrl));
        OnPropertyChanged(nameof(SelectedChannelLabel));
        OnPropertyChanged(nameof(HasSelectedReleaseHighlights));
        OnPropertyChanged(nameof(HasSelectedKnownIssues));
        OnPropertyChanged(nameof(HasSelectedNotesUrl));
        OnPropertyChanged(nameof(CatalogStatusText));
        RefreshActionState();
    }

    private void HandleOperationError(Exception exception)
    {
        _operationHasError = true;
        OperationStatus = $"Операция остановлена: {exception.Message}";
        OperationProgress = 0;
        RefreshActionState();
    }

    private void HandleUpdateError(Exception exception)
    {
        _operationHasError = true;
        OperationStatus = $"Смена версии остановлена: {exception.Message}";
        OperationProgress = 0;
        RefreshActionState();
    }

    private void RefreshActionState()
    {
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanUpdate));
        OnPropertyChanged(nameof(CanSelectVersion));
        OnPropertyChanged(nameof(CanRemoveFromDevice));
        OnPropertyChanged(nameof(CanRemoveFromLibrary));
        OnPropertyChanged(nameof(CanToggleProduct));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(UpdateActionText));
        OnPropertyChanged(nameof(AvailabilityMessage));
        OnPropertyChanged(nameof(HasAvailabilityMessage));
        _installCommand.NotifyCanExecuteChanged();
        _updateCommand.NotifyCanExecuteChanged();
        _removeFromDeviceCommand.NotifyCanExecuteChanged();
        _removeFromLibraryCommand.NotifyCanExecuteChanged();
        _toggleProductCommand.NotifyCanExecuteChanged();
    }

    private void SetManagedState(InstalledProductState? state)
    {
        _managedVersion = state?.Version;
        _managedGameRoot = state?.GameRoot;
        _isInstalled = state is not null;
        _isProductEnabled = state?.IsEnabled == true;
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(IsNotInstalled));
        OnPropertyChanged(nameof(IsProductEnabled));
        OnPropertyChanged(nameof(InstalledVersionLabel));
        OnPropertyChanged(nameof(HasSelectedVersionChange));
        OnPropertyChanged(nameof(HasRecommendedVersionChange));
        OnPropertyChanged(nameof(HasNewerCatalogVersion));
        OnPropertyChanged(nameof(IsUpdateAvailable));
        OnPropertyChanged(nameof(UpdateVersionText));
        OnPropertyChanged(nameof(ActivationStatusText));
        OnPropertyChanged(nameof(ToggleAutomationName));
        OnPropertyChanged(nameof(CatalogStatusText));
        RefreshActionState();
    }

    private void SetLibraryState(bool isInLibrary)
    {
        if (_isInLibrary == isInLibrary)
        {
            return;
        }

        _isInLibrary = isInLibrary;
        OnPropertyChanged(nameof(IsInLibrary));
        OnPropertyChanged(nameof(CatalogStatusText));
        RefreshActionState();
    }

}
