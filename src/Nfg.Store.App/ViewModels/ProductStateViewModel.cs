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
    private readonly IReadOnlyList<ProductManifest> _manifests;
    private readonly IProductInstallationOperations _installationCoordinator;
    private readonly ProductLibraryStore _libraryStore;
    private readonly AsyncRelayCommand _applyVariantCommand;
    private readonly AsyncRelayCommand _installCommand;
    private readonly AsyncRelayCommand _updateCommand;
    private readonly AsyncRelayCommand _removeFromDeviceCommand;
    private readonly AsyncRelayCommand _removeFromLibraryCommand;
    private readonly AsyncRelayCommand _toggleProductCommand;
    private readonly string _installationKey;
    private ProductManifest _manifest;
    private ProductViewModel _product;
    private ProductVariantOptionViewModel _selectedVariant;
    private ProductReleaseOptionViewModel? _recommendedVersion;
    private ProductReleaseOptionViewModel? _selectedVersion;
    private InstalledProductState? _managedState;
    private string? _detectedGameBuildId;
    private bool _isBusy;
    private bool _isInstallationInProgress;
    private bool _isUpdateInProgress;
    private bool _isVariantSwitchInProgress;
    private bool _isCompatibilityPromptOpen;
    private bool _isInLibrary;
    private bool _operationHasError;
    private double _operationProgress;
    private string _operationStatus;

    public ProductStateViewModel(
        IReadOnlyList<ProductManifest> manifests,
        ProductInstallationCoordinator installationCoordinator,
        ProductLibraryStore libraryStore,
        InstalledProductState? installedState,
        bool isInLibrary,
        string? detectedGameBuildId = null)
        : this(
            CreateVariantOptions(manifests),
            new ProductInstallationOperations(installationCoordinator),
            libraryStore,
            installedState,
            isInLibrary,
            detectedGameBuildId)
    {
    }

    // Compatibility constructor retained for standalone products and existing callers.
    public ProductStateViewModel(
        ProductViewModel product,
        ProductManifest manifest,
        ProductInstallationCoordinator installationCoordinator,
        ProductLibraryStore libraryStore,
        InstalledProductState? installedState,
        bool isInLibrary,
        string? detectedGameBuildId = null)
        : this(
            CreateSingleVariantOption(product, manifest),
            new ProductInstallationOperations(installationCoordinator),
            libraryStore,
            installedState,
            isInLibrary,
            detectedGameBuildId)
    {
    }

    internal ProductStateViewModel(
        IReadOnlyList<ProductManifest> manifests,
        IProductInstallationOperations installationCoordinator,
        ProductLibraryStore libraryStore,
        InstalledProductState? installedState,
        bool isInLibrary,
        string? detectedGameBuildId = null)
        : this(
            CreateVariantOptions(manifests),
            installationCoordinator,
            libraryStore,
            installedState,
            isInLibrary,
            detectedGameBuildId)
    {
    }

    private ProductStateViewModel(
        IReadOnlyList<ProductVariantOptionViewModel> variants,
        IProductInstallationOperations installationCoordinator,
        ProductLibraryStore libraryStore,
        InstalledProductState? installedState,
        bool isInLibrary,
        string? detectedGameBuildId)
        : base(variants[0].Product.Title)
    {
        AvailableVariants = variants;
        _manifests = variants.Select(variant => variant.Manifest).ToArray();
        _installationCoordinator = installationCoordinator;
        _libraryStore = libraryStore;
        _installationKey = ProductInstallationKey.FromManifest(variants[0].Manifest);
        _managedState = installedState;
        _isInLibrary = isInLibrary || installedState is not null;
        _detectedGameBuildId = detectedGameBuildId;
        _operationStatus = string.Empty;

        _selectedVariant = variants.FirstOrDefault(variant =>
                string.Equals(
                    variant.ProductId,
                    installedState?.ProductId,
                    StringComparison.Ordinal))
            ?? variants[0];
        _manifest = _selectedVariant.Manifest;
        _product = _selectedVariant.Product;
        RebuildAvailableVersions(
            preferredVersion: null,
            followAutomaticChoice: true);

        _applyVariantCommand = new AsyncRelayCommand(
            ApplySelectedAsync,
            () => CanApply,
            HandleUnexpectedOperationError);
        _installCommand = new AsyncRelayCommand(
            ApplySelectedAsync,
            () => CanInstall,
            HandleUnexpectedOperationError);
        _updateCommand = new AsyncRelayCommand(
            ApplySelectedAsync,
            () => CanUpdate,
            HandleUnexpectedOperationError);
        _removeFromDeviceCommand = new AsyncRelayCommand(
            RemoveFromDeviceAsync,
            () => CanRemoveFromDevice,
            HandleUnexpectedOperationError);
        _removeFromLibraryCommand = new AsyncRelayCommand(
            RemoveFromLibraryAsync,
            () => CanRemoveFromLibrary,
            HandleUnexpectedOperationError);
        _toggleProductCommand = new AsyncRelayCommand(
            ToggleProductAsync,
            () => CanToggleProduct,
            HandleUnexpectedOperationError);

        ApplyVariantCommand = _applyVariantCommand;
        InstallCommand = _installCommand;
        UpdateCommand = _updateCommand;
        RemoveFromDeviceCommand = _removeFromDeviceCommand;
        RemoveFromLibraryCommand = _removeFromLibraryCommand;
        ToggleProductCommand = _toggleProductCommand;
    }

    public ProductViewModel Product => _product;

    public ProductViewModel FeaturedProduct => _product;

    public string InstallationKey => _installationKey;

    public string? FamilyId => _manifest.FamilyId;

    public ICommand ApplyVariantCommand { get; }

    public ICommand InstallCommand { get; }

    public ICommand UpdateCommand { get; }

    public ICommand RemoveFromDeviceCommand { get; }

    public ICommand RemoveFromLibraryCommand { get; }

    public ICommand ToggleProductCommand { get; }

    public IReadOnlyList<ProductVariantOptionViewModel> AvailableVariants { get; }

    public bool HasMultipleVariants => AvailableVariants.Count > 1;

    public ProductVariantOptionViewModel SelectedVariant
    {
        get => _selectedVariant;
        set
        {
            if (value is null ||
                IsBusy ||
                !AvailableVariants.Contains(value) ||
                ReferenceEquals(_selectedVariant, value))
            {
                return;
            }

            _selectedVariant = value;
            _manifest = value.Manifest;
            _product = value.Product;
            _detectedGameBuildId = _installationCoordinator
                .DetectGameInstallation(_manifest, _managedState?.GameRoot)
                ?.BuildId;
            RebuildAvailableVersions(
                preferredVersion: null,
                followAutomaticChoice: true);
            NotifySelectedVariantChanged();
        }
    }

    public IReadOnlyList<ProductReleaseOptionViewModel> AvailableVersions { get; private set; } = [];

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
                OnPropertyChanged(nameof(PrimaryActionText));
            }
        }
    }

    public bool IsVariantSwitchInProgress
    {
        get => _isVariantSwitchInProgress;
        private set
        {
            if (SetProperty(ref _isVariantSwitchInProgress, value))
            {
                OnPropertyChanged(nameof(CatalogStatusText));
            }
        }
    }

    public bool IsProgressVisible => IsInstallationInProgress || IsUpdateInProgress;

    public bool IsInstalled => _managedState is not null;

    public bool IsNotInstalled => !IsInstalled;

    public bool IsInLibrary => _isInLibrary;

    public bool IsProductEnabled => _managedState?.IsEnabled == true;

    public string InstalledProductId => _managedState?.ProductId ?? string.Empty;

    public string InstalledVariantLabel => _managedState is null
        ? "—"
        : GetVariantLabel(_managedState.ProductId);

    public string SelectedVariantLabel => _selectedVariant.LocaleLabel;

    public string InstalledVersionLabel => _managedState?.Version ?? "—";

    public string SelectedVersionLabel => SelectedVersion?.Version ?? "—";

    public string InstalledSelectionText => !IsInstalled
        ? "—"
        : HasMultipleVariants
            ? $"{InstalledVariantLabel} · {InstalledVersionLabel}"
            : InstalledVersionLabel;

    public string SelectedSelectionText => HasMultipleVariants
        ? $"{SelectedVariantLabel} · {SelectedVersionLabel}"
        : SelectedVersionLabel;

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

    public bool HasVariantChange =>
        _managedState is not null &&
        !string.Equals(
            _selectedVariant.ProductId,
            _managedState.ProductId,
            StringComparison.Ordinal);

    public bool HasVersionChange =>
        _managedState is not null &&
        !HasVariantChange &&
        SelectedVersion is not null &&
        !string.Equals(
            SelectedVersion.Version,
            _managedState.Version,
            StringComparison.Ordinal);

    public bool HasSelectionChange => HasVariantChange || HasVersionChange;

    // Compatibility name retained for existing bindings and smoke checks.
    public bool HasSelectedVersionChange => HasSelectionChange;

    public bool HasRecommendedVersionChange =>
        _managedState is not null &&
        string.Equals(
            _selectedVariant.ProductId,
            _managedState.ProductId,
            StringComparison.Ordinal) &&
        _recommendedVersion is not null &&
        !string.Equals(
            _recommendedVersion.Version,
            _managedState.Version,
            StringComparison.Ordinal);

    // Kept for existing catalog bindings. A compatible release can be either
    // newer or older than the version currently installed on the device.
    public bool HasNewerCatalogVersion => HasRecommendedVersionChange;

    public bool IsUpdateAvailable =>
        IsInstalled && HasSelectionChange && SelectedVersion?.IsPublished == true;

    public bool CanApply =>
        !IsBusy &&
        SelectedVersion?.IsPublished == true &&
        (!IsInstalled || HasSelectionChange);

    public bool CanInstall => !IsInstalled && CanApply;

    public bool CanUpdate => IsInstalled && CanApply;

    public bool CanSelectVariant => !IsBusy;

    public bool CanSelectVersion => !IsBusy;

    public bool CanRemoveFromDevice => !IsBusy && IsInstalled;

    public bool CanRemoveFromLibrary => !IsBusy && IsInLibrary;

    public bool CanToggleProduct => !IsBusy && IsInstalled;

    public string ActivationStatusText => IsProductEnabled ? "Включён" : "Выключен";

    public string ToggleAutomationName => IsProductEnabled
        ? "Выключить продукт"
        : "Включить продукт";

    public string CatalogStatusText => IsUpdateInProgress
        ? IsVariantSwitchInProgress
            ? "СМЕНА ЯЗЫКА"
            : "СМЕНА ВЕРСИИ"
        : IsInstallationInProgress
            ? "УСТАНОВКА"
            : HasVariantChange
                ? "СМЕНИТЬ ЯЗЫК"
                : HasRecommendedVersionChange
                    ? "СМЕНИТЬ ВЕРСИЮ"
                    : IsInstalled
                        ? "УСТАНОВЛЕНО"
                        : IsInLibrary
                            ? "В БИБЛИОТЕКЕ"
                            : IsCatalogVersionUnverified
                                ? "НЕ ПРОВЕРЕНО"
                                : "ДОСТУПНО";

    public string UpdateActionText => PrimaryActionText;

    public string UpdateVersionText => GetRecommendedVersionText();

    public string PrimaryActionText
    {
        get
        {
            if (IsInstallationInProgress || IsUpdateInProgress)
            {
                return "Выполняется…";
            }

            if (IsBusy)
            {
                return "Выполняется…";
            }

            if (SelectedVersion?.IsPublished != true)
            {
                return "Сборка готовится";
            }

            if (!IsInstalled)
            {
                return IsSelectedVersionExact
                    ? $"Установить {SelectedVersion.Version}"
                    : "Установить всё равно";
            }

            if (HasVariantChange)
            {
                return $"Переключить {InstalledVariantLabel} → {SelectedVariantLabel}";
            }

            if (!HasVersionChange)
            {
                return "Установлено";
            }

            return GetVersionSwitchActionText();
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

    public void RefreshGameCompatibility()
    {
        if (IsBusy || _isCompatibilityPromptOpen)
        {
            return;
        }

        var detectedGameBuildId = _installationCoordinator
            .DetectGameInstallation(_manifest, _managedState?.GameRoot)
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
        RebuildAvailableVersions(selectedVersion, followAutomaticChoice);

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

    private async Task ApplySelectedAsync()
    {
        var selectedManifest = GetSelectedManifest();
        if (!ConfirmUnverifiedCompatibility())
        {
            return;
        }

        var previousState = _managedState;
        var previousVariantLabel = previousState is null
            ? null
            : GetVariantLabel(previousState.ProductId);
        var wasVariantChange = HasVariantChange;
        var versionComparison = CompareSelectedToInstalledVersion();

        _operationHasError = false;
        IsBusy = true;
        IsInstallationInProgress = previousState is null;
        IsUpdateInProgress = previousState is not null;
        IsVariantSwitchInProgress = wasVariantChange;
        OperationProgress = 0;
        OperationStatus = previousState is null
            ? "Подготовка установки…"
            : wasVariantChange
                ? "Подготовка смены языка…"
                : "Подготовка смены версии…";

        try
        {
            var progress = new Progress<ProductInstallProgress>(value =>
            {
                OperationProgress = value.Percentage;
                OperationStatus = value.Message;
            });
            var result = await _installationCoordinator.ApplyVariantAsync(
                selectedManifest,
                previousState?.ProductId,
                progress);
            SetManagedState(result.State);
            await _libraryStore.AddAsync(selectedManifest.Id);
            SetLibraryState(isInLibrary: true);
            OperationProgress = 100;
            OperationStatus = previousState is null
                ? result.Outcome switch
                {
                    ManagedInstallOutcome.Adopted =>
                        "Уже установленные идентичные файлы приняты под управление NFG Hub.",
                    ManagedInstallOutcome.AlreadyInstalled =>
                        $"{Product.Title} уже установлен и прошёл проверку.",
                    _ => $"{Product.Title} успешно установлен."
                }
                : wasVariantChange
                    ? $"Язык переключён: {previousVariantLabel} → {SelectedVariantLabel}."
                    : versionComparison > 0
                        ? $"{Product.Title} обновлён до версии {result.State.Version}."
                        : $"{Product.Title} переключён на версию {result.State.Version}.";
        }
        catch (Exception exception)
        {
            await HandleOperationErrorAsync(exception, "Операция остановлена");
        }
        finally
        {
            IsInstallationInProgress = false;
            IsUpdateInProgress = false;
            IsVariantSwitchInProgress = false;
            IsBusy = false;
        }
    }

    private async Task ToggleProductAsync()
    {
        var expectedState = _managedState;
        if (expectedState is null)
        {
            return;
        }

        _operationHasError = false;
        var enable = !expectedState.IsEnabled;
        IsBusy = true;
        OperationProgress = 0;
        OperationStatus = enable
            ? "Включение продукта…"
            : "Выключение продукта…";

        try
        {
            var state = await _installationCoordinator.SetEnabledAsync(
                _installationKey,
                expectedState.ProductId,
                enable);
            SetManagedState(state);
            OperationStatus = state.IsEnabled
                ? "Продукт включён."
                : "Продукт выключен. Файлы сохранены на устройстве.";
        }
        catch (Exception exception)
        {
            await HandleOperationErrorAsync(exception, "Операция остановлена");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RemoveFromDeviceAsync()
    {
        var expectedState = _managedState;
        if (expectedState is null)
        {
            return;
        }

        _operationHasError = false;
        IsBusy = true;
        OperationProgress = 0;
        OperationStatus = "Проверка файлов продукта…";

        try
        {
            await _installationCoordinator.RemoveFromDeviceAsync(
                _installationKey,
                expectedState.ProductId);
            SetManagedState(null);
            OperationStatus = "Удалено с устройства. Продукт остаётся в библиотеке.";
        }
        catch (Exception exception)
        {
            await HandleOperationErrorAsync(exception, "Удаление остановлено");
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
            var expectedState = _managedState;
            if (expectedState is not null)
            {
                await _installationCoordinator.RemoveFromDeviceAsync(
                    _installationKey,
                    expectedState.ProductId);
                SetManagedState(null);
            }

            foreach (var productId in _manifests
                         .Select(manifest => manifest.Id)
                         .Distinct(StringComparer.Ordinal))
            {
                await _libraryStore.RemoveAsync(productId);
            }

            SetLibraryState(isInLibrary: false);
            OperationStatus = string.Empty;
        }
        catch (Exception exception)
        {
            await HandleOperationErrorAsync(
                exception,
                "Удаление из библиотеки остановлено",
                reloadLibrary: true);
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
            .DetectGameInstallation(selectedManifest, _managedState?.GameRoot)
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
        if (!HasVersionChange)
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
               _managedState is not null &&
               SemanticVersionComparer.TryCompare(
                   version,
                   _managedState.Version,
                   out var comparison)
            ? comparison
            : 0;
    }

    private void RebuildAvailableVersions(
        string? preferredVersion,
        bool followAutomaticChoice)
    {
        var recommendedRelease = ProductReleaseCatalog.SelectRecommendedRelease(
            _manifest,
            _detectedGameBuildId);
        AvailableVersions = ProductReleaseCatalog.GetAvailableReleases(_manifest)
            .Where(ProductReleaseCatalog.IsPublished)
            .Select(release => new ProductReleaseOptionViewModel(
                _manifest,
                release,
                _detectedGameBuildId,
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
                  string.Equals(
                      version.Version,
                      preferredVersion,
                      StringComparison.Ordinal))
              ?? _recommendedVersion
              ?? AvailableVersions.FirstOrDefault();
    }

    private void NotifySelectedVariantChanged()
    {
        _operationHasError = false;
        OperationStatus = string.Empty;
        OnPropertyChanged(nameof(SelectedVariant));
        OnPropertyChanged(nameof(SelectedVariantLabel));
        OnPropertyChanged(nameof(Product));
        OnPropertyChanged(nameof(FeaturedProduct));
        OnPropertyChanged(nameof(FamilyId));
        OnPropertyChanged(nameof(AvailableVersions));
        OnPropertyChanged(nameof(HasMultipleVersions));
        OnPropertyChanged(nameof(SelectedVersion));
        OnPropertyChanged(nameof(CatalogVersionLabel));
        OnPropertyChanged(nameof(CatalogCompatibilityLabel));
        OnPropertyChanged(nameof(IsCatalogVersionUnverified));
        NotifySelectedVersionChanged();
    }

    private void NotifySelectedVersionChanged()
    {
        _operationHasError = false;
        OperationStatus = string.Empty;
        OnPropertyChanged(nameof(SelectedVersionLabel));
        OnPropertyChanged(nameof(SelectedSelectionText));
        OnPropertyChanged(nameof(IsSelectedVersionExact));
        OnPropertyChanged(nameof(IsSelectedVersionUnverified));
        OnPropertyChanged(nameof(LocalCompatibilityLabel));
        OnPropertyChanged(nameof(HasVariantChange));
        OnPropertyChanged(nameof(HasVersionChange));
        OnPropertyChanged(nameof(HasSelectionChange));
        OnPropertyChanged(nameof(HasSelectedVersionChange));
        OnPropertyChanged(nameof(HasRecommendedVersionChange));
        OnPropertyChanged(nameof(HasNewerCatalogVersion));
        OnPropertyChanged(nameof(IsUpdateAvailable));
        OnPropertyChanged(nameof(SelectedReleaseChangesTitle));
        OnPropertyChanged(nameof(SelectedReleaseHighlights));
        OnPropertyChanged(nameof(SelectedKnownIssues));
        OnPropertyChanged(nameof(SelectedNotesUrl));
        OnPropertyChanged(nameof(SelectedChannelLabel));
        OnPropertyChanged(nameof(HasSelectedReleaseHighlights));
        OnPropertyChanged(nameof(HasSelectedKnownIssues));
        OnPropertyChanged(nameof(HasSelectedNotesUrl));
        OnPropertyChanged(nameof(CatalogStatusText));
        OnPropertyChanged(nameof(UpdateVersionText));
        RefreshActionState();
    }

    private async Task HandleOperationErrorAsync(
        Exception exception,
        string prefix,
        bool reloadLibrary = false)
    {
        _operationHasError = true;
        var actualOutcome = "Фактическое состояние не удалось перечитать.";
        try
        {
            var actualState = await _installationCoordinator.LoadInstalledStateAsync(
                _installationKey,
                CancellationToken.None);
            SetManagedState(actualState);
            actualOutcome = actualState is null
                ? "Фактически сейчас слот не установлен."
                : $"Фактически сейчас установлено {GetVariantLabel(actualState.ProductId)} " +
                  $"{actualState.Version}; " +
                  (actualState.IsEnabled ? "продукт включён." : "продукт выключен.");
        }
        catch (Exception reloadException)
        {
            actualOutcome =
                $"Фактическое состояние не удалось перечитать: {reloadException.Message}";
        }

        if (reloadLibrary)
        {
            try
            {
                var libraryProductIds = await _libraryStore.LoadAsync(CancellationToken.None);
                SetLibraryState(_manifests.Any(manifest =>
                    libraryProductIds.Contains(manifest.Id)));
            }
            catch
            {
                // Installation state remains authoritative for mutation safety.
            }
        }

        OperationStatus = $"{prefix}: {exception.Message} {actualOutcome}";
        OperationProgress = 0;
        RefreshActionState();
    }

    private void HandleUnexpectedOperationError(Exception exception)
    {
        _operationHasError = true;
        OperationStatus = $"Операция остановлена: {exception.Message}";
        OperationProgress = 0;
        RefreshActionState();
    }

    private void RefreshActionState()
    {
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanUpdate));
        OnPropertyChanged(nameof(CanSelectVariant));
        OnPropertyChanged(nameof(CanSelectVersion));
        OnPropertyChanged(nameof(CanRemoveFromDevice));
        OnPropertyChanged(nameof(CanRemoveFromLibrary));
        OnPropertyChanged(nameof(CanToggleProduct));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(UpdateActionText));
        OnPropertyChanged(nameof(AvailabilityMessage));
        OnPropertyChanged(nameof(HasAvailabilityMessage));
        _applyVariantCommand.NotifyCanExecuteChanged();
        _installCommand.NotifyCanExecuteChanged();
        _updateCommand.NotifyCanExecuteChanged();
        _removeFromDeviceCommand.NotifyCanExecuteChanged();
        _removeFromLibraryCommand.NotifyCanExecuteChanged();
        _toggleProductCommand.NotifyCanExecuteChanged();
    }

    private void SetManagedState(InstalledProductState? state)
    {
        _managedState = state;
        if (state is not null)
        {
            SetLibraryState(isInLibrary: true);
        }

        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(IsNotInstalled));
        OnPropertyChanged(nameof(IsProductEnabled));
        OnPropertyChanged(nameof(InstalledProductId));
        OnPropertyChanged(nameof(InstalledVariantLabel));
        OnPropertyChanged(nameof(InstalledVersionLabel));
        OnPropertyChanged(nameof(InstalledSelectionText));
        OnPropertyChanged(nameof(HasVariantChange));
        OnPropertyChanged(nameof(HasVersionChange));
        OnPropertyChanged(nameof(HasSelectionChange));
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

    private string GetVariantLabel(string productId)
    {
        return AvailableVariants.FirstOrDefault(variant =>
                   string.Equals(
                       variant.ProductId,
                       productId,
                       StringComparison.Ordinal))
               ?.LocaleLabel
               ?? productId;
    }

    private static IReadOnlyList<ProductVariantOptionViewModel> CreateVariantOptions(
        IReadOnlyList<ProductManifest> manifests)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        if (manifests.Count == 0)
        {
            throw new ArgumentException(
                "A product family must contain at least one manifest.",
                nameof(manifests));
        }

        var variants = manifests
            .Select(manifest => new ProductVariantOptionViewModel(manifest))
            .ToArray();
        ValidateVariantOptions(variants);
        return variants;
    }

    private static IReadOnlyList<ProductVariantOptionViewModel> CreateSingleVariantOption(
        ProductViewModel product,
        ProductManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(product.Id, manifest.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The product view model and manifest must identify the same product.",
                nameof(product));
        }

        return [new ProductVariantOptionViewModel(manifest, product)];
    }

    private static void ValidateVariantOptions(
        IReadOnlyList<ProductVariantOptionViewModel> variants)
    {
        var installationKey = ProductInstallationKey.FromManifest(variants[0].Manifest);
        var productIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variant in variants)
        {
            if (!productIds.Add(variant.ProductId))
            {
                throw new ArgumentException(
                    $"Product family contains duplicate product id '{variant.ProductId}'.",
                    nameof(variants));
            }

            if (!string.Equals(
                    ProductInstallationKey.FromManifest(variant.Manifest),
                    installationKey,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "All product family variants must use the same installation key.",
                    nameof(variants));
            }
        }
    }
}
