using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;

namespace Counterplay;

/// Прокси для привязки локализованных строк в XAML. При смене языка уведомляет
/// все привязки (через индексатор), и текст обновляется без перезапуска.
public sealed class LocProxy : INotifyPropertyChanged
{
    public static LocProxy Instance { get; } = new();

    private LocProxy()
    {
        Loc.LanguageChanged += () =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    public string this[string key] => Loc.T(key);

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// Использование в XAML: Text="{loc:T metric.base}".
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new System.Windows.Data.Binding($"[{Key}]")
        {
            Source = LocProxy.Instance,
            Mode = System.Windows.Data.BindingMode.OneWay
        }.ProvideValue(serviceProvider);
}
