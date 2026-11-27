using System.Windows.Markup;

namespace PiuLink.Host.Localization;

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension(string key) : MarkupExtension
{
    public string Key { get; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new System.Windows.Data.Binding($"[{Key}]")
        {
            Source = LocalizationService.Instance,
            Mode = System.Windows.Data.BindingMode.OneWay
        }.ProvideValue(serviceProvider);
}
