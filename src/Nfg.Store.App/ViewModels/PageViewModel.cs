using Nfg.Store.App.Infrastructure;

namespace Nfg.Store.App.ViewModels;

public abstract class PageViewModel(string title) : ObservableObject
{
    public string Title { get; } = title;
}
