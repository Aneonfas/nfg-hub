using Nfg.Store.App.Infrastructure;

namespace Nfg.Store.App.ViewModels;

public abstract class PageViewModel(string title, bool localizeTitle = false) : LocalizedObservableObject
{
    public virtual string Title => localizeTitle ? Text.Get(title) : title;
}
