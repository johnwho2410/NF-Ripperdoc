using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace GameDoctor.App;

/// <summary>Zeichnet den Frametime-Verlauf (ms über Sekunden) inkl. Ruckler-Markierungen und 60/30-FPS-Linien.</summary>
public sealed class FrameTimeChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IReadOnlyList<(double Sec, double Ms)>), typeof(FrameTimeChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StuttersProperty = DependencyProperty.Register(
        nameof(Stutters), typeof(IReadOnlyList<(double Sec, double Ms)>), typeof(FrameTimeChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<(double Sec, double Ms)>? Series
    {
        get => (IReadOnlyList<(double Sec, double Ms)>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public IReadOnlyList<(double Sec, double Ms)>? Stutters
    {
        get => (IReadOnlyList<(double Sec, double Ms)>?)GetValue(StuttersProperty);
        set => SetValue(StuttersProperty, value);
    }

    private static readonly Brush StutterBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x5E)));

    // Linie, Raster, Beschriftung und Schrift kommen aus dem aktiven Skin.
    private Brush LineBrush = Brushes.Cyan, GridBrush = Brushes.DimGray, LabelBrush = Brushes.Gray;
    private Typeface Font = new("Segoe UI");

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    public FrameTimeChart()
    {
        // Selbst gezeichnet → bei Skin-Wechsel neu zeichnen. Abmelden beim Entladen, damit nichts hängen bleibt.
        Loaded += (_, _) => ThemeManager.Changed += InvalidateVisual;
        Unloaded += (_, _) => ThemeManager.Changed -= InvalidateVisual;
    }

    private void LoadSkin()
    {
        LineBrush = TryFindResource("ChartLine") as Brush ?? LineBrush;
        GridBrush = TryFindResource("Line") as Brush ?? GridBrush;
        LabelBrush = TryFindResource("MutedBrush") as Brush ?? LabelBrush;
        if (TryFindResource("BodyFont") is FontFamily ff) Font = new Typeface(ff, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    }

    protected override void OnRender(DrawingContext dc)
    {
        LoadSkin();
        double w = ActualWidth, h = ActualHeight;
        const double left = 44, bottom = 22, top = 8, right = 8;
        double pw = w - left - right, ph = h - top - bottom;
        if (pw <= 10 || ph <= 10) return;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var series = Series;
        if (series is null || series.Count < 2)
        {
            Text(dc, "Noch keine Daten", left + pw / 2 - 50, top + ph / 2 - 8, LabelBrush);
            return;
        }

        double maxSec = series[^1].Sec, minSec = series[0].Sec;
        double spanSec = Math.Max(maxSec - minSec, 1);
        // Y-Skala: bis zum 99,5-%-Wert (plus Luft), damit einzelne Riesenspitzen den Rest nicht plattdrücken
        var sorted = series.Select(p => p.Ms).OrderBy(x => x).ToArray();
        double yMax = Math.Max(20, sorted[(int)(sorted.Length * 0.995)] * 1.4);
        yMax = Math.Ceiling(yMax / 10) * 10;

        double X(double sec) => left + (sec - minSec) / spanSec * pw;
        double Y(double ms) => top + ph - Math.Min(ms, yMax) / yMax * ph;

        var gridPen = new Pen(GridBrush, 1);
        // Hilfslinien: 60 FPS (16,7 ms) und 30 FPS (33,3 ms)
        foreach (var (ms, label) in new[] { (16.7, "60 FPS"), (33.3, "30 FPS") })
        {
            if (ms > yMax) continue;
            double y = Y(ms);
            dc.DrawLine(new Pen(GridBrush, 1) { DashStyle = DashStyles.Dash }, new Point(left, y), new Point(left + pw, y));
            Text(dc, label, left + pw - 44, y - 16, LabelBrush);
        }
        // Achsen
        dc.DrawLine(gridPen, new Point(left, top + ph), new Point(left + pw, top + ph));
        for (int i = 0; i <= 4; i++)
        {
            double ms = yMax * i / 4;
            Text(dc, $"{ms:F0} ms", 2, Y(ms) - 8, LabelBrush);
        }
        for (int i = 0; i <= 5; i++)
        {
            double sec = minSec + spanSec * i / 5;
            Text(dc, TimeSpan.FromSeconds(sec).ToString(@"m\:ss"), X(sec) - 12, top + ph + 4, LabelBrush);
        }

        // Verlauf
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(X(series[0].Sec), Y(series[0].Ms)), false, false);
            for (int i = 1; i < series.Count; i++)
                ctx.LineTo(new Point(X(series[i].Sec), Y(series[i].Ms)), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, new Pen(LineBrush, 1.2), geo);

        // Ruckler
        if (Stutters is { } st)
            foreach (var (sec, ms) in st)
                dc.DrawEllipse(StutterBrush, null, new Point(X(sec), Y(ms)), 3, 3);
    }

    private void Text(DrawingContext dc, string text, double x, double y, Brush brush)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, 11, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(x, y));
    }
}
