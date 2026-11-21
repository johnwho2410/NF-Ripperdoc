using GameDoctor.Core.Localization;

namespace GameDoctor.Core.Performance;

public enum LiveAlertLevel { Info, Warning, Critical }

/// <summary>Ein Hinweis für das Ingame-Overlay.</summary>
public sealed record LiveAlert(string Id, LiveAlertLevel Level, string Title, string Detail);

/// <summary>
/// Bewertet die Live-Werte jede Sekunde und meldet Probleme fürs Overlay.
/// Damit das Overlay nicht nervt:
/// – eine Bedingung muss einige Sekunden am Stück gelten, bevor sie gemeldet wird,
/// – derselbe Hinweis kommt frühestens nach <see cref="Cooldown"/> wieder,
/// – in den ersten Sekunden nach Spielstart (Laden, Shader) wird nichts gemeldet.
/// </summary>
public sealed class LiveAdvisor
{
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan WarmUp { get; init; } = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, int> _streak = [];
    private readonly Dictionary<string, DateTime> _lastShown = [];
    private string? _lastBottleneck;
    private int _bottleneckStreak;
    private DateTime? _gameSeenAt;

    /// <summary>Neue Live-Werte verarbeiten; liefert die Hinweise, die jetzt angezeigt werden sollen.</summary>
    public List<LiveAlert> Update(LiveStats s, DateTime now)
    {
        var result = new List<LiveAlert>();
        if (!s.GameRunning || s.FrameCount == 0)
        {
            _gameSeenAt = null;
            _streak.Clear();
            _fired.Clear();
            _stableBottleneck = null;
            _lastBottleneck = null;
            _bottleneckStreak = 0;
            return result;
        }
        _gameSeenAt ??= now;
        bool warm = now - _gameSeenAt.Value >= WarmUp;

        // ---- Grafikspeicher
        if (s.VramUsedMb is double vu && s.VramTotalMb is double vt && vt > 0)
            Check("vram", vu / vt >= 0.95, 5, LiveAlertLevel.Warning, L.T("Grafikspeicher voll"),
                L.F("{0:F1} von {1:F1} GB belegt – Texturen werden nachgeladen. Texturqualität oder Raytracing senken.", vu / 1024, vt / 1024));
        else Reset("vram");

        // ---- Arbeitsspeicher und virtueller Speicher
        if (s.RamTotalGb > 0)
            Check("ram", s.RamUsedGb / s.RamTotalGb >= 0.92, 5, LiveAlertLevel.Warning, L.T("Arbeitsspeicher fast voll"),
                L.F("{0:F1} von {1:F0} GB – Windows lagert aus. Browser, Discord & Co. schließen.", s.RamUsedGb, s.RamTotalGb));
        if (s.CommitLimitGb > 0)
            Check("commit", s.CommitUsedGb / s.CommitLimitGb >= 0.90, 3, LiveAlertLevel.Critical, L.T("Virtueller Speicher knapp"),
                L.F("{0:F0} von {1:F0} GB zugesagt – Absturzgefahr. Auslagerungsdatei vergrößern oder Programme schließen.", s.CommitUsedGb, s.CommitLimitGb));

        // ---- Ruckler und Bildrate
        Check("stutter", s.StuttersLast10s >= 5, 2, LiveAlertLevel.Warning, L.T("Ruckler-Serie"),
            L.F("{0} Ruckler in 10 Sekunden.", s.StuttersLast10s));
        if (s.Fps is double fps)
            Check("lowfps", fps < 30, 10, LiveAlertLevel.Info, L.T("Bildrate unter 30"),
                L.F("Seit über 10 Sekunden nur {0:F0} FPS", fps) + (s.Bottleneck is { } b ? $" ({b})." : "."));

        // ---- Temperatur
        if (s.GpuTempC is double t)
            Check("gputemp", t >= 83, 10, LiveAlertLevel.Warning, L.T("Grafikkarte sehr warm"),
                L.F("{0:F0} °C – ab etwa 83 °C taktet die Karte herunter. Lüfter und Gehäusebelüftung prüfen.", t));

        // ---- Flaschenhals: nur melden, wenn er von einem stabilen Zustand in einen anderen wechselt.
        //      Ein dauerhaftes GPU-Limit ist der Normalfall und wird nicht gemeldet.
        if (s.Bottleneck is { } bn)
        {
            if (bn == _lastBottleneck) _bottleneckStreak++;
            else { _lastBottleneck = bn; _bottleneckStreak = 1; }
            if (_bottleneckStreak >= 15 && bn != _stableBottleneck)
            {
                // Der erste stabile Zustand (und alles in der Aufwärmphase) wird nur gemerkt, nicht gemeldet.
                // Gegen Hin-und-her-Springen schützt der Cooldown pro Zielzustand.
                if (_stableBottleneck is not null)
                    Emit("bottleneck:" + bn, LiveAlertLevel.Info, L.F("Flaschenhals jetzt: {0}", bn),
                        L.F("Vorher {0}, seit 15 Sekunden durchgehend {1}.", _stableBottleneck, bn));
                _stableBottleneck = bn;
            }
        }

        return result;

        // Bedingung muss `seconds` Sekunden am Stück gelten. Gemeldet wird einmal pro Problemphase –
        // war die Meldung gerade gesperrt (Aufwärmphase, Cooldown), wird es in den Folgesekunden erneut versucht.
        void Check(string id, bool condition, int seconds, LiveAlertLevel level, string title, string detail)
        {
            if (!condition) { Reset(id); return; }
            int n = _streak[id] = _streak.GetValueOrDefault(id) + 1;
            if (n >= seconds && !_fired.Contains(id) && Emit(id, level, title, detail)) _fired.Add(id);
        }

        bool Emit(string id, LiveAlertLevel level, string title, string detail)
        {
            if (!warm) return false;
            if (_lastShown.TryGetValue(id, out var last) && now - last < Cooldown) return false;
            _lastShown[id] = now;
            result.Add(new LiveAlert(id, level, title, detail));
            return true;
        }
    }

    private readonly HashSet<string> _fired = [];
    private string? _stableBottleneck;

    private void Reset(string id)
    {
        _streak.Remove(id);
        _fired.Remove(id);
    }
}
