using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;
using ApexHighlighter.Core;

namespace ApexHighlighter.App;

/// <summary>Bindable view of <see cref="Loc"/>: XAML binds to this["key"] and refreshes when the language changes.</summary>
public sealed class LocSource : INotifyPropertyChanged
{
    public static LocSource Instance { get; } = new();

    private LocSource() => Loc.Changed += () => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));

    public string this[string key] => Loc.T(key);

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>XAML: Content="{l:Tr btn.import}" — live-updating localized text.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }
    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = LocSource.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}
