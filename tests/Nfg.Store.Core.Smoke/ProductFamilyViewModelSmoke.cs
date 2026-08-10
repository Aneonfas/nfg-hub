using System.Net;
using Nfg.Store.App.Services;
using Nfg.Store.App.ViewModels;
using Nfg.Store.Contracts;
using Nfg.Store.Core;
using Nfg.Store.Installation;
using Nfg.Store.Platform.Windows;

internal static class ProductFamilyViewModelSmoke
{
    private const string InstallationKey = "nfg.anvil-empires.ru";
    private const string FamilyId = "nfg.anvil-empires.localization";
    private const string RuProductId = "nfg.anvil-empires.ru";
    private const string EsProductId = "nfg.anvil-empires.es";
    private const string ForgeProductId = "nfg.anvil-empires.forge-helper";

    public static async Task RunAsync(string testRoot)
    {
        var dataRoot = Path.Combine(testRoot, "product-family-view-model");
        var gameRoot = Path.Combine(dataRoot, "game");
        Directory.CreateDirectory(gameRoot);

        var russian = CreateVariant(RuProductId, "ru", additionalVersion: null);
        var spanish = CreateVariant(EsProductId, "es", additionalVersion: "0.9.0");
        var forge = CreateForge();
        var installedRussian = CreateState(
            RuProductId,
            version: "1.0.0",
            gameRoot,
            isEnabled: false);
        var operations = new BlockingInstallationOperations(installedRussian, gameRoot);
        var libraryStore = new ProductLibraryStore(dataRoot);
        await libraryStore.AddAsync(RuProductId);

        var family = new ProductStateViewModel(
            [russian, spanish],
            operations,
            libraryStore,
            installedRussian,
            isInLibrary: true,
            detectedGameBuildId: "100",
            deletionPrompt: new StubDeletionPrompt(ProductDeletionChoice.SelectedVariant));

        Assert(family.AvailableVariants.Count == 2, "RU/ES family did not expose two variants.");
        Assert(family.SelectedVariant.ProductId == RuProductId,
            "The installed RU variant was not selected initially.");
        Assert(family.InstalledSelectionText == "RU · 1.0.0",
            "The installed RU state was not presented independently.");
        Assert(family.SelectedSelectionText == "RU · 1.0.0",
            "The initial selected RU state was not presented independently.");
        Assert(!family.IsProductEnabled,
            "A disabled persisted slot was presented as enabled.");
        Assert(!family.CanApply, "The installed RU version unexpectedly exposed an apply action.");
        Assert(family.AvailableVersions.Count == 1,
            "RU release options were not derived from the selected RU manifest.");

        family.SelectedVariant = family.AvailableVariants.Single(variant =>
            variant.ProductId == EsProductId);

        Assert(family.SelectedVariantLabel == "ES", "The ES locale label was not selected.");
        Assert(family.InstalledVariantLabel == "RU",
            "Selecting ES overwrote the persisted installed RU variant.");
        Assert(family.SelectedSelectionText == "ES · 1.0.0",
            "Selected ES was not displayed separately from installed RU.");
        Assert(family.AvailableVersions.Count == 2,
            "Selecting language before version did not rebuild ES release options.");
        Assert(family.HasVariantChange && !family.HasVersionChange,
            "A same-SemVer RU to ES transition was not classified as a variant switch.");
        Assert(family.CanApply, "A same-SemVer RU to ES transition was not actionable.");
        Assert(family.PrimaryActionText == "Переключить RU → ES",
            "The RU to ES primary action was not explicit.");

        family.ApplyVariantCommand.Execute(null);
        var applyCall = await operations.ApplyEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert(applyCall.InstallationKey == InstallationKey,
            "Variant Apply used the wrong installation slot.");
        Assert(applyCall.TargetProductId == EsProductId,
            "Variant Apply did not use the selected ES manifest.");
        Assert(applyCall.ExpectedProductId == RuProductId,
            "Variant Apply did not guard the active RU product.");
        Assert(family.IsBusy && family.IsVariantSwitchInProgress,
            "The family did not expose shared busy state during variant Apply.");
        Assert(!family.CanSelectVariant && !family.CanSelectVersion && !family.CanApply,
            "Family selectors or mutations remained enabled during variant Apply.");

        operations.CompleteApply(new ManagedInstallResult(
            ManagedInstallOutcome.Updated,
            CreateState(
                EsProductId,
                version: "1.0.0",
                gameRoot,
                isEnabled: false)));
        await WaitUntilAsync(() => !family.IsBusy, "Variant Apply did not finish.");

        Assert(family.InstalledSelectionText == "ES · 1.0.0",
            "The completed ES state was not broadcast to the family row.");
        Assert(!family.IsProductEnabled,
            "A disabled RU slot did not remain disabled after the ES switch fixture.");

        family.SelectedVariant = family.AvailableVariants.Single(variant =>
            variant.ProductId == RuProductId);
        family.ToggleProductCommand.Execute(null);
        var toggleCall = await operations.ToggleEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => !family.IsBusy, "Enable operation did not finish.");
        Assert(toggleCall.InstallationKey == InstallationKey &&
               toggleCall.ExpectedProductId == EsProductId,
            "Inactive selected RU was allowed to replace the active ES expected-product guard.");

        family.RemoveFromDeviceCommand.Execute(null);
        var removeCall = await operations.FamilyRemoveEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => !family.IsBusy, "Remove operation did not finish.");
        Assert(removeCall.SelectedProductId == RuProductId &&
               removeCall.Scope == ManagedVariantRemovalScope.SelectedVariant,
            "Remove did not target the language selected in the dropdown.");
        Assert(family.InstalledVariantLabel == "ES",
            "Removing selected RU unexpectedly removed the active ES variant.");

        await CheckApplyFailureReloadsCommittedStateAsync(
            Path.Combine(dataRoot, "apply-failure-after-commit"),
            gameRoot,
            russian,
            spanish,
            installedRussian);
        await CheckRemoveFamilyFromLibraryAsync(
            Path.Combine(dataRoot, "remove-family-library"),
            gameRoot,
            russian,
            spanish);
        await CheckFamilyGroupingAsync(
            dataRoot,
            russian,
            spanish,
            forge,
            installedRussian);
        CheckStandaloneForge(
            Path.Combine(dataRoot, "forge"),
            operations,
            forge);
    }

    private static async Task CheckApplyFailureReloadsCommittedStateAsync(
        string dataRoot,
        string gameRoot,
        ProductManifest russian,
        ProductManifest spanish,
        InstalledProductState installedRussian)
    {
        var operations = new BlockingInstallationOperations(installedRussian, gameRoot);
        var libraryStore = new ProductLibraryStore(dataRoot);
        await libraryStore.AddAsync(RuProductId);
        var family = new ProductStateViewModel(
            [russian, spanish],
            operations,
            libraryStore,
            installedRussian,
            isInLibrary: true,
            detectedGameBuildId: "100");
        family.SelectedVariant = family.AvailableVariants.Single(variant =>
            variant.ProductId == EsProductId);

        family.ApplyVariantCommand.Execute(null);
        await operations.ApplyEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        operations.FailApplyAfterPersisting(
            CreateState(
                EsProductId,
                version: "1.0.0",
                gameRoot,
                isEnabled: false),
            new InvalidOperationException("Injected failure after state commit."));
        await WaitUntilAsync(() => !family.IsBusy,
            "Post-commit Apply failure handling did not finish.");

        Assert(family.InstalledSelectionText == "ES · 1.0.0",
            "Post-error reload did not adopt the actually committed ES state.");
        Assert(family.SelectedSelectionText == "ES · 1.0.0" && !family.HasSelectionChange,
            "Installed and selected outcomes diverged after committed ES was re-read.");
        Assert(family.OperationStatus.Contains(
                "Injected failure after state commit.",
                StringComparison.Ordinal) &&
               family.OperationStatus.Contains(
                   "Фактически сейчас установлено ES 1.0.0",
                   StringComparison.Ordinal),
            "Post-error status did not report the re-read factual ES outcome.");
    }

    private static async Task CheckRemoveFamilyFromLibraryAsync(
        string dataRoot,
        string gameRoot,
        ProductManifest russian,
        ProductManifest spanish)
    {
        var operations = new BlockingInstallationOperations(initialState: null, gameRoot);
        var libraryStore = new ProductLibraryStore(dataRoot);
        await libraryStore.AddAsync(RuProductId);
        await libraryStore.AddAsync(EsProductId);
        var family = new ProductStateViewModel(
            [russian, spanish],
            operations,
            libraryStore,
            installedState: null,
            isInLibrary: true,
            detectedGameBuildId: "100",
            deletionPrompt: new StubDeletionPrompt(ProductDeletionChoice.SelectedVariant));

        family.RemoveFromLibraryCommand.Execute(null);
        await WaitUntilAsync(() => !family.IsInLibrary,
            "Family RemoveFromLibrary did not finish.");
        var remainingProductIds = await libraryStore.LoadAsync();

        Assert(!remainingProductIds.Contains(RuProductId) &&
               !remainingProductIds.Contains(EsProductId),
            "Family RemoveFromLibrary retained an RU or ES sibling id.");
        Assert(!operations.RemoveEntered.Task.IsCompleted,
            "Family without device state attempted a guarded device removal.");
    }

    private static async Task CheckFamilyGroupingAsync(
        string dataRoot,
        ProductManifest russian,
        ProductManifest spanish,
        ProductManifest forge,
        InstalledProductState installedRussian)
    {
        var mainRoot = Path.Combine(dataRoot, "main-window");
        Directory.CreateDirectory(mainRoot);
        using var httpClient = new HttpClient(new OfflineHandler());
        var coordinator = new ProductInstallationCoordinator(
            httpClient,
            mainRoot,
            new InstallationStateStore(mainRoot),
            new SteamGameLocator([], includeConfiguredSteam: false));
        var libraryStore = new ProductLibraryStore(mainRoot);
        await libraryStore.AddAsync(RuProductId);
        await libraryStore.AddAsync(EsProductId);
        var settingsStore = new AppSettingsStore(mainRoot);
        var catalogResult = new CatalogLoadResult(
            new StoreCatalog("nfg.test", "NFG Test", [russian, spanish, forge]),
            CatalogSourceKind.Bundled,
            new Uri("https://catalog.test/catalog.json"),
            Path.Combine(mainRoot, "cache"));
        var main = new MainWindowViewModel(
            catalogResult,
            mainRoot,
            coordinator,
            libraryStore,
            new AppSettings(),
            settingsStore,
            new Dictionary<string, InstalledProductState?>(StringComparer.Ordinal)
            {
                [InstallationKey] = installedRussian
            },
            new HashSet<string>([RuProductId, EsProductId], StringComparer.Ordinal));

        var catalog = (CatalogViewModel)main.Navigation.Single(item =>
            item.Page is CatalogViewModel).Page;
        var library = (LibraryViewModel)main.Navigation.Single(item =>
            item.Page is LibraryViewModel).Page;
        Assert(catalog.Products.Count == 2,
            "RU and ES produced duplicate catalog cards instead of one family plus Forge.");
        var family = catalog.Products.Single(product => product.HasMultipleVariants);
        Assert(library.Products.Count == 1,
            "RU and ES produced duplicate Library rows.");
        Assert(ReferenceEquals(family, library.Products[0]),
            "Catalog and Library did not share one synchronized family view model.");
    }

    private static void CheckStandaloneForge(
        string dataRoot,
        IProductInstallationOperations operations,
        ProductManifest forge)
    {
        var viewModel = new ProductStateViewModel(
            [forge],
            operations,
            new ProductLibraryStore(dataRoot),
            installedState: null,
            isInLibrary: false,
            detectedGameBuildId: "100");

        Assert(viewModel.InstallationKey == ForgeProductId,
            "Standalone Forge did not retain productId as its installation key.");
        Assert(viewModel.AvailableVariants.Count == 1 && !viewModel.HasMultipleVariants,
            "Standalone Forge was incorrectly presented as a language family.");
        Assert(viewModel.SelectedSelectionText == "1.0.0",
            "Standalone Forge selection gained a language prefix.");
    }

    private static ProductManifest CreateVariant(
        string productId,
        string locale,
        string? additionalVersion)
    {
        var current = CreateRelease("1.0.0");
        return new ProductManifest
        {
            SchemaVersion = 2,
            Id = productId,
            Type = "localization",
            FamilyId = FamilyId,
            Locale = locale,
            ExclusiveGroup = InstallationKey,
            Display = new ProductDisplay
            {
                Title = "Anvil Empires Localization",
                Subtitle = locale == "ru" ? "Русский" : "Español",
                Summary = "Language variant",
                Description = "Language variant fixture",
                Features = []
            },
            Release = current,
            Releases = additionalVersion is null
                ? []
                : [CreateRelease(additionalVersion)],
            Compatibility = CreateCompatibility(),
            Installation = CreateInstallation(),
            Dependencies = [],
            Progress = new ProductProgress
            {
                TranslationPercent = 100,
                Label = "Complete"
            }
        };
    }

    private static ProductManifest CreateForge()
    {
        return new ProductManifest
        {
            SchemaVersion = 2,
            Id = ForgeProductId,
            Type = "tool",
            Display = new ProductDisplay
            {
                Title = "Anvil Forge Helper",
                Subtitle = "Forge",
                Summary = "Standalone helper",
                Description = "Standalone helper fixture",
                Features = []
            },
            Release = CreateRelease("1.0.0"),
            Releases = [],
            Compatibility = CreateCompatibility(),
            Installation = CreateInstallation(),
            Dependencies = [],
            Progress = new ProductProgress
            {
                TranslationPercent = null,
                Label = "Ready"
            }
        };
    }

    private static ProductRelease CreateRelease(string version) => new()
    {
        Version = version,
        Channel = "stable",
        GameVersion = "steam-build-100",
        PublishedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        Highlights = [],
        KnownIssues = [],
        NotesUrl = null,
        Payload = new ProductPayload
        {
            Url = new Uri($"https://packages.test/{version}.zip"),
            SizeBytes = 1,
            Sha256 = new string('a', 64)
        }
    };

    private static ProductCompatibility CreateCompatibility() => new()
    {
        Platforms = ["windows-x64"],
        GameVersion = "steam-build-100",
        Status = "compatible"
    };

    private static ProductInstallation CreateInstallation() => new()
    {
        Strategy = "managed-files",
        SupportsRollback = true,
        RequiresElevation = "never",
        Detection =
        [
            new ProductDetectionRule
            {
                Provider = "steam",
                ProductId = "2383950"
            }
        ]
    };

    private static InstalledProductState CreateState(
        string productId,
        string version,
        string gameRoot,
        bool isEnabled) => new()
        {
            SchemaVersion = 2,
            InstallationKey = InstallationKey,
            ProductId = productId,
            Version = version,
            PackageSizeBytes = 1,
            PackageSha256 = new string('b', 64),
            SteamAppId = "2383950",
            SteamBuildId = "100",
            DetectedSteamBuildId = "100",
            GameRoot = gameRoot,
            InstalledAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            IsEnabled = isEnabled,
            Files = []
        };

    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert(condition(), message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class BlockingInstallationOperations(
        InstalledProductState? initialState,
        string gameRoot) : IProductInstallationOperations
    {
        private readonly TaskCompletionSource<ManagedInstallResult> _applyCompletion = NewSignal<ManagedInstallResult>();
        private readonly HashSet<string> _installedProductIds = initialState is null
            ? []
            : [initialState.ProductId];
        private InstalledProductState? _state = initialState;

        public TaskCompletionSource<ApplyCall> ApplyEntered { get; } = NewSignal<ApplyCall>();

        public TaskCompletionSource<GuardedCall> ToggleEntered { get; } = NewSignal<GuardedCall>();

        public TaskCompletionSource<GuardedCall> RemoveEntered { get; } = NewSignal<GuardedCall>();

        public TaskCompletionSource<FamilyRemoveCall> FamilyRemoveEntered { get; } =
            NewSignal<FamilyRemoveCall>();

        public ProductGameInstallation? DetectGameInstallation(
            ProductManifest product,
            string? preferredGameRoot = null) => new(
            "2383950",
            "100",
            null,
            gameRoot,
            Path.Combine(gameRoot, "appmanifest_2383950.acf"));

        public async Task<ManagedInstallResult> ApplyVariantAsync(
            ProductManifest product,
            string? expectedProductId,
            IProgress<ProductInstallProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ApplyEntered.TrySetResult(new ApplyCall(
                ProductInstallationKey.FromManifest(product),
                product.Id,
                expectedProductId));
            var result = await _applyCompletion.Task.WaitAsync(cancellationToken);
            _state = result.State;
            _installedProductIds.Add(result.State.ProductId);
            return result;
        }

        public Task<InstalledProductState?> LoadInstalledStateAsync(
            string installationKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_state);

        public Task<ManagedFamilyInventory> ReconcileFamilyAsync(
            IReadOnlyList<ProductManifest> products,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateInventory(products));

        public Task<ManagedFamilyInventory> RemoveFamilyVariantAsync(
            IReadOnlyList<ProductManifest> products,
            string selectedProductId,
            ManagedVariantRemovalScope scope,
            CancellationToken cancellationToken = default)
        {
            FamilyRemoveEntered.TrySetResult(new FamilyRemoveCall(selectedProductId, scope));
            if (scope == ManagedVariantRemovalScope.AllVariants ||
                string.Equals(_state?.ProductId, selectedProductId, StringComparison.Ordinal))
            {
                _state = null;
            }

            if (scope == ManagedVariantRemovalScope.AllVariants)
            {
                _installedProductIds.Clear();
            }
            else
            {
                _installedProductIds.Remove(selectedProductId);
            }

            return Task.FromResult(CreateInventory(products));
        }

        public Task<InstalledProductState> SetEnabledAsync(
            string installationKey,
            string expectedProductId,
            bool isEnabled,
            CancellationToken cancellationToken = default)
        {
            ToggleEntered.TrySetResult(new GuardedCall(installationKey, expectedProductId));
            _state = (_state ?? throw new InvalidOperationException("No fake state to toggle.")) with
            {
                IsEnabled = isEnabled
            };
            return Task.FromResult(_state);
        }

        public Task RemoveFromDeviceAsync(
            string installationKey,
            string expectedProductId,
            CancellationToken cancellationToken = default)
        {
            RemoveEntered.TrySetResult(new GuardedCall(installationKey, expectedProductId));
            _installedProductIds.Remove(expectedProductId);
            _state = null;
            return Task.CompletedTask;
        }

        public void CompleteApply(ManagedInstallResult result) =>
            _applyCompletion.TrySetResult(result);

        public void FailApplyAfterPersisting(
            InstalledProductState persistedState,
            Exception exception)
        {
            _state = persistedState;
            _installedProductIds.Add(persistedState.ProductId);
            _applyCompletion.TrySetException(exception);
        }

        private static TaskCompletionSource<T> NewSignal<T>() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private ManagedFamilyInventory CreateInventory(
            IReadOnlyList<ProductManifest> products) => new(
            InstallationKey,
            _state,
            products.Select(product => new ManagedVariantPresence(
                product.Id,
                _state is not null && _state.ProductId == product.Id
                    ? _state.Version
                    : product.Release.Version,
                IsInstalled: _installedProductIds.Contains(product.Id),
                IsEnabled: _state is not null &&
                           _state.ProductId == product.Id &&
                           _state.IsEnabled,
                HasRelatedArtifacts: _installedProductIds.Contains(product.Id))).ToArray());
    }

    private sealed class StubDeletionPrompt(ProductDeletionChoice choice)
        : IProductDeletionPrompt
    {
        public ProductDeletionChoice Confirm(
            string selectedVariantLabel,
            bool hasOtherInstalledVariants,
            bool isProductFamily) => choice;
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    private sealed record ApplyCall(
        string InstallationKey,
        string TargetProductId,
        string? ExpectedProductId);

    private sealed record GuardedCall(
        string InstallationKey,
        string ExpectedProductId);

    private sealed record FamilyRemoveCall(
        string SelectedProductId,
        ManagedVariantRemovalScope Scope);
}
