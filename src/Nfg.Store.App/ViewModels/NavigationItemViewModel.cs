using Nfg.Store.App.Infrastructure;

namespace Nfg.Store.App.ViewModels;

public sealed class NavigationItemViewModel(
    string labelKey,
    string glyph,
    PageViewModel page) : LocalizedObservableObject
{
    private bool _isSelected;

    public string Label => Text.Get(labelKey);

    public string Glyph { get; } = glyph;

    public PageViewModel Page { get; } = page;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
