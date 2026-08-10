using Nfg.Store.App.Localization;

namespace Nfg.Store.App.Infrastructure;

public abstract class LocalizedObservableObject : ObservableObject
{
    protected LocalizedObservableObject()
    {
        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
    }

    protected static LocalizationService Text => LocalizationService.Instance;

    protected virtual void OnLanguageChanged(object? sender, EventArgs eventArgs) =>
        OnPropertyChanged(string.Empty);
}
