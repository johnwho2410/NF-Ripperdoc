using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using GameDoctor.Core.Performance;

namespace GameDoctor.App;

/// <summary>
/// Steuert das Ingame-Overlay: ein-/ausblenden (auch per Tastenkürzel Strg+Umschalt+O, das im Spiel funktioniert),
/// Live-Werte weiterreichen und die Hinweise des <see cref="LiveAdvisor"/> anzeigen.
/// </summary>
public sealed class OverlayController : IDisposable
{
    public const string HotkeyText = "Strg+Umschalt+O";

    private readonly Window _owner;
    private OverlayWindow? _overlay;
    private LiveAdvisor _advisor = new();
    private HwndSource? _source;
    private bool _hotkeyRegistered;

    /// <summary>Wird ausgelöst, wenn das Overlay ein- oder ausgeblendet wurde (z. B. per Tastenkürzel).</summary>
    public event Action<bool>? VisibilityChanged;

    /// <summary>Wird vor dem Einblenden aufgerufen; soll die Live-Messung starten, falls sie nicht läuft.</summary>
    public Func<bool>? EnsureLiveData { get; set; }

    public OverlayController(Window owner)
    {
        _owner = owner;
        if (owner.IsLoaded) RegisterHotkey();
        else owner.SourceInitialized += (_, _) => RegisterHotkey();
    }

    public bool IsVisible => _overlay?.IsVisible == true;
    public bool HotkeyAvailable => _hotkeyRegistered;

    public OverlayCorner Corner
    {
        get => AppSettings.Load().OverlayCorner;
        set
        {
            AppSettings.Update(s => s.OverlayCorner = value);
            if (_overlay is not null) _overlay.Corner = value;
        }
    }

    public void Toggle()
    {
        if (IsVisible) Hide();
        else Show();
    }

    public void Show()
    {
        if (EnsureLiveData is not null && !EnsureLiveData()) return;
        _overlay ??= new OverlayWindow();
        _overlay.Corner = Corner;
        _advisor = new LiveAdvisor(); // frische Zählung, kein alter Cooldown
        _overlay.Show();
        VisibilityChanged?.Invoke(true);
    }

    public void Hide()
    {
        if (_overlay is null) return;
        _overlay.Hide();
        VisibilityChanged?.Invoke(false);
    }

    /// <summary>Jede Sekunde vom Recorder (bereits auf dem UI-Thread).</summary>
    public void OnLive(LiveStats s)
    {
        if (!IsVisible) return;
        _overlay!.ShowStats(s);
        var alerts = _advisor.Update(s, DateTime.Now);
        if (alerts.Count > 0) _overlay.ShowAlerts(alerts);
    }

    /// <summary>Prüft anhand des Fenstermodus, ob das Overlay über dem Spiel überhaupt sichtbar sein kann.</summary>
    public static string? WindowModeProblem(string? windowMode) =>
        windowMode is "Fullscreen"
            ? "Cyberpunk läuft im exklusiven Vollbild. Dort kann kein Programm etwas über das Spiel legen, ohne in das Spiel einzugreifen. " +
              "Stell im Spiel unter Grafik → Fenstermodus auf „Randloses Fenster“ – das kostet praktisch keine Leistung."
            : null;

    // ------------------------------------------------------------------ globales Tastenkürzel

    private const int HotkeyId = 0x4744; // „GD“
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
    private const uint VK_O = 0x4F;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private void RegisterHotkey()
    {
        var hwnd = new WindowInteropHelper(_owner).Handle;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
        // Schlägt fehl, wenn ein anderes Programm das Kürzel schon belegt – dann bleibt der Knopf in GameDoctor.
        _hotkeyRegistered = RegisterHotKey(hwnd, HotkeyId, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_O);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Toggle();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_hotkeyRegistered && _source is not null) UnregisterHotKey(_source.Handle, HotkeyId);
        _source?.RemoveHook(WndProc);
        _overlay?.Close();
        _overlay = null;
    }
}
