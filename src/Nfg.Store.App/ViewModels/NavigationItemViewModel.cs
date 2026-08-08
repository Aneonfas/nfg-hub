using Nfg.Store.App.Infrastructure;

namespace Nfg.Store.App.ViewModels;

public sealed class NavigationItemViewModel(
    string label,
    string glyph,
    PageViewModel page) : ObservableObject
{
    private bool _isSelected;

    public string Label { get; } = label;

    public string Glyph { get; } = glyph;

    public PageViewModel Page { get; } = page;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
