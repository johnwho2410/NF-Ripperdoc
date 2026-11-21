using System.Windows;
using System.Windows.Media;

namespace GameDoctor.App;

/// <summary>Kleine Frametime-Kurve fürs Overlay. Hohe Ausschläge (Ruckler) werden rot markiert.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    private static readonly Brush Spike = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x5E)));
    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var v = Values;
        if (v is null || v.Count < 2 || w < 4 || h < 4) return;

        var sorted = v.OrderBy(x => x).ToArray();
        double median = sorted[sorted.Length / 2];
        // Skala: doppelter Median oder höchster Wert, damit normale Frames in der unteren Hälfte liegen
        double max = Math.Max(median * 2.2, Math.Min(sorted[^1], median * 6));
        double Y(double ms) => h - 1 - Math.Min(ms, max) / max * (h - 2);
        double X(int i) => i / (double)(v.Count - 1) * w;

        var line = TryFindResource("ChartLine") as Brush ?? Brushes.Cyan;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(0, Y(v[0])), false, false);
            for (int i = 1; i < v.Count; i++) ctx.LineTo(new Point(X(i), Y(v[i])), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, new Pen(line, 1.2), geo);

        for (int i = 0; i < v.Count; i++)
            if (v[i] > 2.0 * median && v[i] > median + 12)
                dc.DrawEllipse(Spike, null, new Point(X(i), Y(v[i])), 2, 2);
    }
}
