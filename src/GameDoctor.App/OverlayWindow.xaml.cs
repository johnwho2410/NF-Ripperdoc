using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GameDoctor.Core.Performance;

namespace GameDoctor.App;

/// <summary>Ecke des Bildschirms, in der das Overlay sitzt.</summary>
public enum OverlayCorner { TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>
/// Durchsichtiges, klick-durchlässiges Fenster, das immer über dem Spiel liegt.
/// Es greift NICHT ins Spiel ein (keine Injektion) und funktioniert daher nur, wenn das Spiel
/// im Fenster oder randlos läuft – nicht im exklusiven Vollbild.
/// </summary>
public partial class OverlayWindow : Window
{
    private sealed record AlertItem(string Title, string Detail, Brush Brush, DateTime Until);

    private readonly ObservableCollection<AlertItem> _alerts = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private OverlayCorner _corner = OverlayCorner.TopLeft;

    /// <summary>So lange bleibt ein Hinweis sichtbar.</summary>
    public static readonly TimeSpan AlertDuration = TimeSpan.FromSeconds(10);

    public OverlayWindow()
    {
        InitializeComponent();
        Alerts.ItemsSource = _alerts;
        SourceInitialized += (_, _) => MakeClickThrough();
        SizeChanged += (_, _) => PlaceInCorner();
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    public OverlayCorner Corner
    {
        get => _corner;
        set { _corner = value; PlaceInCorner(); }
    }

    /// <summary>Neue Live-Werte anzeigen.</summary>
    public void ShowStats(LiveStats s)
    {
        bool hasFrames = s.GameRunning && s.FrameCount > 0;
        FpsText.Text = hasFrames && s.Fps is double fps ? fps.ToString("F0") : "–";
        LowText.Text = hasFrames && s.OnePercentLowFps is double low ? $"1 % Low {low:F0}" : "";
        BottleneckText.Text = hasFrames ? s.Bottleneck ?? "" : "";
        Spark.Values = s.RecentFrameTimes;

        var parts = new List<string>();
        if (s.GpuUtilPct is double g) parts.Add($"GPU {g:F0}%");
        if (s.VramUsedMb is double vu && s.VramTotalMb is double vt && vt > 0) parts.Add($"VRAM {vu / 1024:F1}/{vt / 1024:F0}G");
        if (s.RamTotalGb > 0) parts.Add($"RAM {s.RamUsedGb:F1}/{s.RamTotalGb:F0}G");
        if (s.GpuTempC is double t) parts.Add($"{t:F0}°C");
        DetailText.Text = hasFrames ? string.Join("  ", parts) : (s.GameRunning ? "Warte auf Frames …" : "Spiel läuft nicht");
    }

    /// <summary>Hinweise einblenden; sie verschwinden nach <see cref="AlertDuration"/> von selbst.</summary>
    public void ShowAlerts(IEnumerable<LiveAlert> alerts)
    {
        foreach (var a in alerts)
        {
            // höchstens 3 gleichzeitig, der älteste fliegt raus
            if (_alerts.Count >= 3) _alerts.RemoveAt(0);
            _alerts.Add(new AlertItem(a.Title, a.Detail, BrushFor(a.Level), DateTime.Now + AlertDuration));
        }
    }

    private static Brush BrushFor(LiveAlertLevel level) => level switch
    {
        LiveAlertLevel.Critical => SeverityBrushConverter.For(GameDoctor.Core.Severity.Critical),
        LiveAlertLevel.Warning => SeverityBrushConverter.For(GameDoctor.Core.Severity.Warning),
        _ => SeverityBrushConverter.For(GameDoctor.Core.Severity.Info),
    };

    private void Tick()
    {
        var now = DateTime.Now;
        for (int i = _alerts.Count - 1; i >= 0; i--)
            if (_alerts[i].Until <= now) _alerts.RemoveAt(i);
        // Manche Spiele schieben sich im randlosen Modus wieder nach vorn – Topmost regelmäßig erneuern.
        KeepOnTop();
    }

    private void PlaceInCorner()
    {
        var area = SystemParameters.WorkArea;
        const double m = 24;
        Left = _corner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft ? area.Left + m : area.Right - ActualWidth - m;
        Top = _corner is OverlayCorner.TopLeft or OverlayCorner.TopRight ? area.Top + m : area.Bottom - ActualHeight - m;
    }

    // ------------------------------------------------------------------ Win32

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>Mausklicks gehen durch das Overlay hindurch ins Spiel; es nimmt nie den Fokus.</summary>
    private void MakeClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    private void KeepOnTop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }
}
