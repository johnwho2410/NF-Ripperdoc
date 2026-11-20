using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GameDoctor.App;

/// <summary>
/// Rahmen, dessen Form vom Skin kommt: abgerundete Ecken (<see cref="CornerRadius"/>) oder
/// abgeschrägte Ecken oben rechts und unten links (<see cref="Chamfer"/> &gt; 0).
/// Der Inhalt wird auf die Form zugeschnitten. Optional wird die obere Schräge mit
/// <see cref="EdgeAccent"/> farbig hervorgehoben.
/// </summary>
public sealed class ShapeBorder : Decorator
{
    public static readonly DependencyProperty BackgroundProperty = Reg<Brush?>(nameof(Background), null);
    public static readonly DependencyProperty BorderBrushProperty = Reg<Brush?>(nameof(BorderBrush), null);
    public static readonly DependencyProperty EdgeAccentProperty = Reg<Brush?>(nameof(EdgeAccent), null);
    public static readonly DependencyProperty BorderThicknessProperty = Reg(nameof(BorderThickness), 0.0, measure: true);
    public static readonly DependencyProperty CornerRadiusProperty = Reg(nameof(CornerRadius), 0.0);
    public static readonly DependencyProperty ChamferProperty = Reg(nameof(Chamfer), 0.0);
    public static readonly DependencyProperty PaddingProperty = Reg(nameof(Padding), default(Thickness), measure: true);

    public Brush? Background { get => (Brush?)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public Brush? BorderBrush { get => (Brush?)GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }
    public Brush? EdgeAccent { get => (Brush?)GetValue(EdgeAccentProperty); set => SetValue(EdgeAccentProperty, value); }
    public double BorderThickness { get => (double)GetValue(BorderThicknessProperty); set => SetValue(BorderThicknessProperty, value); }
    public double CornerRadius { get => (double)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public double Chamfer { get => (double)GetValue(ChamferProperty); set => SetValue(ChamferProperty, value); }
    public Thickness Padding { get => (Thickness)GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }

    private static DependencyProperty Reg<T>(string name, T def, bool measure = false) =>
        DependencyProperty.Register(name, typeof(T), typeof(ShapeBorder), new FrameworkPropertyMetadata(def,
            (measure ? FrameworkPropertyMetadataOptions.AffectsMeasure : 0) | FrameworkPropertyMetadataOptions.AffectsRender |
            FrameworkPropertyMetadataOptions.AffectsArrange));

    private Thickness Inset => new(
        Padding.Left + BorderThickness, Padding.Top + BorderThickness,
        Padding.Right + BorderThickness, Padding.Bottom + BorderThickness);

    protected override Size MeasureOverride(Size constraint)
    {
        var i = Inset;
        double w = i.Left + i.Right, h = i.Top + i.Bottom;
        if (Child is null) return new Size(w, h);
        Child.Measure(new Size(Math.Max(0, constraint.Width - w), Math.Max(0, constraint.Height - h)));
        return new Size(Child.DesiredSize.Width + w, Child.DesiredSize.Height + h);
    }

    protected override Size ArrangeOverride(Size size)
    {
        if (Child is not null)
        {
            var i = Inset;
            var rect = new Rect(i.Left, i.Top, Math.Max(0, size.Width - i.Left - i.Right), Math.Max(0, size.Height - i.Top - i.Bottom));
            Child.Arrange(rect);
            // Inhalt (z. B. farbige Leisten am Rand) auf die Form zuschneiden – in Koordinaten des Kindes.
            var clip = Shape(new Rect(size), 0);
            clip.Transform = new TranslateTransform(-rect.X, -rect.Y);
            Child.Clip = clip;
        }
        return size;
    }

    private Geometry Shape(Rect r, double inset)
    {
        r = new Rect(r.X + inset, r.Y + inset, Math.Max(0, r.Width - 2 * inset), Math.Max(0, r.Height - 2 * inset));
        double c = Math.Min(Chamfer, Math.Min(r.Width, r.Height) / 2);
        if (c <= 0)
            return new RectangleGeometry(r, CornerRadius, CornerRadius);

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(r.Left, r.Top), true, true);
            ctx.LineTo(new Point(r.Right - c, r.Top), true, true);
            ctx.LineTo(new Point(r.Right, r.Top + c), true, true);
            ctx.LineTo(new Point(r.Right, r.Bottom), true, true);
            ctx.LineTo(new Point(r.Left + c, r.Bottom), true, true);
            ctx.LineTo(new Point(r.Left, r.Bottom - c), true, true);
        }
        // Nicht einfrieren: ArrangeOverride verschiebt die Form danach noch per Transform.
        return g;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var r = new Rect(RenderSize);
        double bt = BorderThickness;
        var pen = bt > 0 && BorderBrush is not null ? new Pen(BorderBrush, bt) : null;
        dc.DrawGeometry(Background, pen, Shape(r, bt / 2));

        // Farbige Kante an der oberen Schräge (Skin-Detail)
        double c = Math.Min(Chamfer, Math.Min(r.Width, r.Height) / 2);
        if (EdgeAccent is not null && c > 0)
        {
            var accent = new Pen(EdgeAccent, 2);
            dc.DrawLine(accent, new Point(r.Right - c - 14, r.Top + 1), new Point(r.Right - c, r.Top + 1));
            dc.DrawLine(accent, new Point(r.Right - c, r.Top + 1), new Point(r.Right - 1, r.Top + c));
        }
    }
}
