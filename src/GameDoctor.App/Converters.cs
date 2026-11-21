using GameDoctor.Core.Localization;
using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using GameDoctor.Core;

namespace GameDoctor.App;

/// <summary>Farbe je Schweregrad.</summary>
public sealed class SeverityBrushConverter : IValueConverter
{
    // Statusfarben des Designs: status-critical, status-warning, status-info, status-success
    private static readonly Brush Critical = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x4D)));
    private static readonly Brush Warning = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x00)));
    private static readonly Brush Hint = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF)));
    private static readonly Brush Info = Freeze(new SolidColorBrush(Color.FromRgb(0x55, 0xB8, 0xFF)));
    /// <summary>Grün für „erledigt / geholfen“.</summary>
    public static readonly Brush Good = Freeze(new SolidColorBrush(Color.FromRgb(0x20, 0xD6, 0xA0)));

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    public static Brush For(Severity s) => s switch
    {
        Severity.Critical => Critical,
        Severity.Warning => Warning,
        Severity.Hint => Hint,
        _ => Info,
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Severity s ? For(s) : Info;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class SeverityLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        Severity.Critical => L.T("KRITISCH"),
        Severity.Warning => L.T("WARNUNG"),
        Severity.Hint => L.T("HINWEIS"),
        _ => "INFO",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Anteil (0–1) als Stern-Breite für Grid-Spalten.</summary>
public sealed class ShareToStarConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new GridLength(value is double d ? Math.Max(d, 0.0001) : 0.0001, GridUnitType.Star);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Blendet Elemente aus, wenn der Wert null, leer oder eine leere Liste ist.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        null => Visibility.Collapsed,
        string s when string.IsNullOrWhiteSpace(s) => Visibility.Collapsed,
        ICollection { Count: 0 } => Visibility.Collapsed,
        _ => Visibility.Visible,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
