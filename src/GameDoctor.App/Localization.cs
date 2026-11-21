using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using GameDoctor.Core.Localization;

namespace GameDoctor.App;

/// <summary>
/// XAML-Übersetzung: <c>Text="{local:T 'Abstürze'}"</c>. Der deutsche Text ist der Schlüssel; beim Sprachwechsel
/// aktualisieren sich alle so gebundenen Texte sofort.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        // Bindung an einen Zähler, der sich bei jedem Sprachwechsel ändert; der Konverter liefert den übersetzten Text.
        var binding = new Binding(nameof(LocSource.Version))
        {
            Source = LocSource.Instance,
            Mode = BindingMode.OneWay,
            Converter = LocConverter.Instance,
            ConverterParameter = Key,
        };
        return binding.ProvideValue(serviceProvider);
    }
}

/// <summary>Meldet Sprachwechsel an alle XAML-Bindungen.</summary>
public sealed class LocSource : INotifyPropertyChanged
{
    public static LocSource Instance { get; } = new();
    private int _version;

    private LocSource() => L.LanguageChanged += () =>
    {
        _version++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Version)));
    };

    public int Version => _version;
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class LocConverter : IValueConverter
{
    public static LocConverter Instance { get; } = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => L.T(parameter as string ?? "");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
