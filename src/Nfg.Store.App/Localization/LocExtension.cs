using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace Nfg.Store.App.Localization;

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension(string key) : MarkupExtension
{
    public string Key { get; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding
        {
            Source = LocalizationService.Instance,
            Path = new PropertyPath($"[{Key}]"),
            Mode = BindingMode.OneWay
        };
        return binding.ProvideValue(serviceProvider);
    }
}
