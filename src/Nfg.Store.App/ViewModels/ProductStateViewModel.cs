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
    private readonly AsyncRelayCommand _removeCommand;
    private readonly AsyncRelayCommand _toggleProductCommand;
    private readonly IProductDeletionPrompt _deletionPrompt;
    private readonly string _installationKey;
    private ManagedFamilyInventory _inventory;
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
        string? detectedGameBuildId = null,
        ManagedFamilyInventory? inventory = null)
        : this(
            CreateVariantOptions(manifests),
            new ProductInstallationOperations(installationCoordinator),
            libraryStore,
            installedState,
            isInLibrary,
            detectedGameBuildId,
            inventory,
            deletionPrompt: null)
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
        string? detectedGameBuildId = null,
        ManagedFamilyInventory? inventory = null)
        : this(
            CreateSingleVariantOption(product, manifest),
            new ProductInstallationOperations(installationCoordinator),
            libraryStore,
            installedState,
            isInLibrary,
            detectedGameBuildId,
            inventory,
            deletionPrompt: null)
    {
    }

    internal ProductStateViewModel(
        IReadOnlyList<ProductManifest> manifests,
        IProductInstallationOperations installationCoordinator,
        ProductLibraryStore libraryStore,
        InstalledProductState? installedState,
        bool isInLibrary,
        string? detectedGameBuildId = null,
        ManagedFamilyInventory? inventory = null,
        IProductDeletionPrompt? deletionPrompt = null)
        : this(
            CreateVariantOptions(manifests),
            installationCoordinator,
            libraryStore,
            installedState,
            isInLibrary,
            detectedGameBuildId,
            inventory,
            deletionPrompt)
    {
    }

    private ProductStateViewModel(
        IReadOnlyList<ProductVariantOptionViewModel> variants,
        IProductInstallationOperations installationCoordinator,
        ProductLibraryStore libraryStore,
        InstalledProductState? installedState,
        bool isInLibrary,
        string? detectedGameBuildId,
        ManagedFamilyInventory? inventory,
        IProductDeletionPrompt? deletionPrompt)
        : base(variants[0].Product.Title)
    {
        AvailableVariants = variants;
        _manifests = variants.Select(variant => variant.Manifest).ToArray();
        _installationCoordinator = installationCoordinator;
        _libraryStore = libraryStore;
        _installationKey = ProductInstallationKey.FromManifest(variants[0].Manifest);
        _managedState = installedState;
        _inventory = inventory ?? ManagedFamilyInventory.FromState(
            _installationKey,
            installedState);
        _deletionPrompt = deletionPrompt ?? new ProductDeletionPrompt();
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
        _removeCommand = new AsyncRelayCommand(
            RemoveAsync,
            () => CanRemove,
            HandleUnexpectedOperationError);
        _toggleProductCommand = new AsyncRelayCommand(
            ToggleProductAsync,
            () => CanToggleProduct,
            HandleUnexpectedOperationError);

        ApplyVariantCommand = _applyVariantCommand;
        InstallCommand = _installCommand;
        UpdateCommand = _updateCommand;
        RemoveCommand = _removeCommand;
        ToggleProductCommand = _toggleProductCommand;
    }

    public ProductViewModel Product => _product;

    public override string Title => _product.Title;

    public ProductViewModel FeaturedProduct => _product;

    public string InstallationKey => _installationKey;

    public string? FamilyId => _manifest.FamilyId;

    public ICommand ApplyVariantCommand { get; }

    public ICommand InstallCommand { get; }

    public ICommand UpdateCommand { get; }

    public ICommand RemoveCommand { get; }

    // Compatibility aliases for older callers; the UI exposes one delete action.
    public ICommand RemoveFromDeviceCommand => RemoveCommand;

    public ICommand RemoveFromLibraryCommand => RemoveCommand;

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

    public string? SelectedVersionValue
    {
        get => _selectedVersion?.Version;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            SelectedVersion = AvailableVersions.FirstOrDefault(version =>
                version.Version.Equals(value, StringComparison.Ordinal));
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
        SelectedVersion?.CompatibilityLabel ?? Text.Get("State.NoVersion");

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

    public bool CanRemove =>
        !IsBusy &&
        IsInLibrary &&
        (!HasMultipleVariants ||
         _inventory.HasVariant(_selectedVariant.ProductId) ||
         !_inventory.HasAnyInstalledVariants);

    public bool CanRemoveFromDevice => CanRemove;

    public bool CanRemoveFromLibrary => CanRemove;

    public bool HasOtherInstalledVariants =>
        _inventory.HasOtherInstalledVariant(_selectedVariant.ProductId);

    public bool CanToggleProduct => !IsBusy && IsInstalled;

    public string ActivationStatusText => IsProductEnabled
        ? Text.Get("State.Enabled")
        : Text.Get("State.Disabled");

    public string ToggleAutomationName => IsProductEnabled
        ? Text.Get("State.Disable")
        : Text.Get("State.Enable");

    public string CatalogStatusText => IsUpdateInProgress
        ? IsVariantSwitchInProgress
            ? Text.Get("State.SwitchingLanguage")
            : Text.Get("State.SwitchingVersion")
        : IsInstallationInProgress
            ? Text.Get("State.Installing")
            : HasVariantChange
                ? Text.Get("State.SwitchLanguage")
                : HasRecommendedVersionChange
                    ? Text.Get("State.SwitchVersion")
                    : IsInstalled
                        ? Text.Get("Common.Installed")
                        : IsInLibrary
                            ? Text.Get("State.InLibrary")
                            : IsCatalogVersionUnverified
                                ? Text.Get("State.Unverified")
                                : Text.Get("Common.Available");

    public string UpdateActionText => PrimaryActionText;

    public string UpdateVersionText => GetRecommendedVersionText();

    public string PrimaryActionText
    {
        get
        {
            if (IsInstallationInProgress || IsUpdateInProgress)
            {
                return Text.Get("State.Working");
            }

            if (IsBusy)
            {
                return Text.Get("State.Working");
            }

            if (SelectedVersion?.IsPublished != true)
            {
                return Text.Get("State.BuildPreparing");
            }

            if (!IsInstalled)
            {
                return IsSelectedVersionExact
                    ? Text.Format("State.InstallVersion", SelectedVersion.Version)
                    : Text.Get("State.InstallAnyway");
            }

            if (HasVariantChange)
            {
                return Text.Format(
                    "State.SwitchVariant",
                    InstalledVariantLabel,
                    SelectedVariantLabel);
            }

            if (!HasVersionChange)
            {
                return Text.Get("Common.Installed");
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
                return Text.Format(
                    "State.CompatibilityUnverified",
                    SelectedVersionLabel,
                    LocalCompatibilityLabel);
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
                : Text.Get("State.NotPublished");
        }
    }

    public bool HasAvailabilityMessage =>
        !string.IsNullOrWhiteSpace(AvailabilityMessage);

    public string SelectedReleaseChangesTitle =>
        SelectedVersion?.ReleaseChangesTitle ?? Text.Get("State.ReleaseChanges");

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
            ? Text.Get("State.PreparingInstall")
            : wasVariantChange
                ? Text.Get("State.PreparingLanguage")
                : Text.Get("State.PreparingVersion");

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
            try
            {
                ApplyInventory(
                    await _installationCoordinator.ReconcileFamilyAsync(_manifests),
                    preferManagedVariant: true);
            }
            catch
            {
                SetManagedState(result.State);
            }
            await _libraryStore.AddAsync(selectedManifest.Id);
            SetLibraryState(isInLibrary: true);
            OperationProgress = 100;
            OperationStatus = previousState is null
                ? result.Outcome switch
                {
                    ManagedInstallOutcome.Adopted =>
                        Text.Get("State.Adopted"),
                    ManagedInstallOutcome.AlreadyInstalled =>
                        Text.Format("State.AlreadyInstalled", Product.Title),
                    _ => Text.Format("State.InstallSuccess", Product.Title)
                }
                : wasVariantChange
                    ? Text.Format(
                        "State.LanguageSuccess",
                        previousVariantLabel,
                        SelectedVariantLabel)
                    : versionComparison > 0
                        ? Text.Format("State.UpdateSuccess", Product.Title, result.State.Version)
                        : Text.Format("State.SwitchSuccess", Product.Title, result.State.Version);
        }
        catch (Exception exception)
        {
            await HandleOperationErrorAsync(exception, Text.Get("State.OperationStopped"));
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
            ? Text.Get("State.Enabling")
            : Text.Get("State.Disabling");

        try
        {
            var state = await _installationCoordinator.SetEnabledAsync(
                _installationKey,
                expectedState.ProductId,
                enable);
            SetManagedState(state);
            OperationStatus = state.IsEnabled
                ? Text.Get("State.EnabledSuccess")
                : Text.Get("State.DisabledSuccess");
        }
        catch (Exception exception)
        {
            await HandleOperationErrorAsync(exception, Text.Get("State.OperationStopped"));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RemoveAsync()
    {
        var selectedProductId = _selectedVariant.ProductId;
        var selectedVariantLabel = SelectedVariantLabel;
        var choice = _deletionPrompt.Confirm(
            selectedVariantLabel,
            HasOtherInstalledVariants,
            HasMultipleVariants);
        if (choice == ProductDeletionChoice.Cancel)
        {
            return;
        }

        _operationHasError = false;
        IsBusy = true;
        OperationProgress = 0;
        var scope = choice == ProductDeletionChoice.AllVariants
            ? ManagedVariantRemovalScope.AllVariants
            : ManagedVariantRemovalScope.SelectedVariant;
        OperationStatus = scope == ManagedVariantRemovalScope.AllVariants
            ? Text.Get("State.RemovingAllLanguages")
            : Text.Format("State.RemovingSelectedLanguage", selectedVariantLabel);

        try
        {
            if (!HasMultipleVariants)
            {
                if (_managedState is { } standaloneState)
                {
                    await _installationCoordinator.RemoveFromDeviceAsync(
                        _installationKey,
                        standaloneState.ProductId);
                }

                await _libraryStore.RemoveAsync(selectedProductId);
                _inventory = ManagedFamilyInventory.FromState(_installationKey, state: null);
                SetManagedState(state: null);
                SetLibraryState(isInLibrary: false);
                OperationStatus = Text.Get("State.RemovedProduct");
                return;
            }

            if (!_inventory.HasAnyInstalledVariants)
            {
                foreach (var productId in _manifests
                             .Select(manifest => manifest.Id)
                             .Distinct(StringComparer.Ordinal))
                {
                    await _libraryStore.RemoveAsync(productId);
                }

                SetLibraryState(isInLibrary: false);
                OperationStatus = Text.Get("State.RemovedProduct");
                return;
            }

            var inventory = await _installationCoordinator.RemoveFamilyVariantAsync(
                _manifests,
                selectedProductId,
                scope);
            ApplyInventory(inventory, preferManagedVariant: true);

            if (scope == ManagedVariantRemovalScope.AllVariants ||
                !inventory.HasAnyInstalledVariants)
            {
                foreach (var productId in _manifests
                             .Select(manifest => manifest.Id)
                             .Distinct(StringComparer.Ordinal))
                {
                    await _libraryStore.RemoveAsync(productId);
                }

                SetLibraryState(isInLibrary: false);
            }
            else
            {
                await _libraryStore.RemoveAsync(selectedProductId);
                foreach (var remaining in inventory.Variants.Where(variant =>
                             variant.IsInstalled))
                {
                    await _libraryStore.AddAsync(remaining.ProductId);
                }

                SetLibraryState(isInLibrary: true);
            }

            OperationStatus = scope == ManagedVariantRemovalScope.AllVariants
                ? Text.Get("State.RemovedAllLanguages")
                : Text.Format("State.RemovedSelectedLanguage", selectedVariantLabel);
        }
        catch (Exception exception)
        {
            await HandleOperationErrorAsync(
                exception,
                Text.Get("State.RemovalStopped"),
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
            throw new InvalidOperationException(Text.Get("State.SelectedUnavailable"));
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
            : Text.Get("State.UnspecifiedBuild");
        var detectedBuildMessage = string.IsNullOrWhiteSpace(detectedGameBuildId)
            ? Text.Get("State.DeviceBuildUnknown")
            : Text.Format("State.DeviceBuild", detectedGameBuildId);
        var message = Text.Format(
            "State.CompatibilityPrompt",
            SelectedVersion.Version,
            testedBuild,
            detectedBuildMessage);

        _isCompatibilityPromptOpen = true;
        try
        {
            return MessageBox.Show(
                message,
                Text.Get("State.CompatibilityPromptTitle"),
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
            return Text.Get("Common.Installed");
        }

        if (SelectedVersion?.IsPublished != true)
        {
            return Text.Get("State.VersionUnavailable");
        }

        if (IsSelectedVersionUnverified)
        {
            return Text.Get("State.InstallAnyway");
        }

        return CompareSelectedToInstalledVersion() > 0
            ? Text.Format("State.UpdateTo", SelectedVersion.Version)
            : Text.Format("State.SwitchTo", SelectedVersion.Version);
    }

    private string GetRecommendedVersionText()
    {
        if (!HasRecommendedVersionChange || _recommendedVersion is null)
        {
            return string.Empty;
        }

        if (!_recommendedVersion.IsExactlyCompatible)
        {
            return Text.Format("State.RecommendedUnverified", _recommendedVersion.Version);
        }

        return CompareRecommendedToInstalledVersion() > 0
            ? Text.Format("State.RecommendedUpdate", _recommendedVersion.Version)
            : Text.Format("State.RecommendedVersion", _recommendedVersion.Version);
    }

    private string GetRecommendedAvailabilityMessage()
    {
        if (_recommendedVersion is null)
        {
            return string.Empty;
        }

        if (!_recommendedVersion.IsExactlyCompatible)
        {
            return Text.Format("State.RecommendedUnverifiedLong", _recommendedVersion.Version);
        }

        return CompareRecommendedToInstalledVersion() > 0
            ? Text.Format(
                "State.RecommendedUpdateLong",
                InstalledVersionLabel,
                _recommendedVersion.Version)
            : Text.Format(
                "State.RecommendedVersionLong",
                _recommendedVersion.Version,
                InstalledVersionLabel);
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
        OnPropertyChanged(nameof(Title));
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
        OnPropertyChanged(nameof(SelectedVersionValue));
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
        var actualOutcome = Text.Get("State.ActualReadFailed");
        try
        {
            var inventory = await _installationCoordinator.ReconcileFamilyAsync(
                _manifests,
                CancellationToken.None);
            var actualState = inventory.State;
            ApplyInventory(inventory, preferManagedVariant: true);
            actualOutcome = actualState is null
                ? Text.Get("State.ActualNotInstalled")
                : Text.Format(
                    "State.ActualInstalled",
                    GetVariantLabel(actualState.ProductId),
                    actualState.Version,
                    actualState.IsEnabled
                        ? Text.Get("State.ActualEnabled")
                        : Text.Get("State.ActualDisabled"));
        }
        catch (Exception reloadException)
        {
            actualOutcome = Text.Format(
                "State.ActualReadFailedDetails",
                reloadException.Message);
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
        OperationStatus = Text.Format("State.UnexpectedError", exception.Message);
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
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(HasOtherInstalledVariants));
        OnPropertyChanged(nameof(CanToggleProduct));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(UpdateActionText));
        OnPropertyChanged(nameof(AvailabilityMessage));
        OnPropertyChanged(nameof(HasAvailabilityMessage));
        _applyVariantCommand.NotifyCanExecuteChanged();
        _installCommand.NotifyCanExecuteChanged();
        _updateCommand.NotifyCanExecuteChanged();
        _removeCommand.NotifyCanExecuteChanged();
        _toggleProductCommand.NotifyCanExecuteChanged();
    }

    private void SetManagedState(InstalledProductState? state)
    {
        _managedState = state;
        _inventory = _inventory with
        {
            State = state,
            Variants = _inventory.Variants
                .Select(variant => state is not null && variant.ProductId.Equals(
                    state.ProductId,
                    StringComparison.Ordinal)
                    ? variant with
                    {
                        Version = state.Version,
                        IsInstalled = true,
                        IsEnabled = state.IsEnabled,
                        HasRelatedArtifacts = true
                    }
                    : variant)
                .ToArray()
        };
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

    private void ApplyInventory(
        ManagedFamilyInventory inventory,
        bool preferManagedVariant)
    {
        _inventory = inventory;
        SetManagedState(inventory.State);
        if (preferManagedVariant && inventory.State is { } state)
        {
            var stateVariant = AvailableVariants.FirstOrDefault(variant =>
                variant.ProductId.Equals(state.ProductId, StringComparison.Ordinal));
            if (stateVariant is not null && !ReferenceEquals(_selectedVariant, stateVariant))
            {
                _selectedVariant = stateVariant;
                _manifest = stateVariant.Manifest;
                _product = stateVariant.Product;
                _detectedGameBuildId = _installationCoordinator
                    .DetectGameInstallation(_manifest, state.GameRoot)
                    ?.BuildId;
                RebuildAvailableVersions(
                    preferredVersion: state.Version,
                    followAutomaticChoice: false);
                NotifySelectedVariantChanged();
            }
        }

        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(HasOtherInstalledVariants));
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
