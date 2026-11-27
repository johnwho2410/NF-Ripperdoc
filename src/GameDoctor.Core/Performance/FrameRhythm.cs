namespace GameDoctor.Core.Performance;

/// <summary>Ein vom Spiel selbst berechnetes Bild (bei Frame Generation inklusive der dazugehörigen Zwischenbilder).</summary>
public readonly record struct RealFrame(double TimeMs, double FrameTimeMs, double CpuBusyMs);

/// <summary>Eine Serie von Hängern in festem Takt.</summary>
public sealed record PeriodicRun(double StartSec, double EndSec, int Count, double PeriodSec, double AvgMs, double CpuBoundShare);

/// <summary>Aufbereitung der Frametimes für die Ruckler-Erkennung.</summary>
public static class FrameRhythm
{
    // Zwischenbilder der Frame Generation: praktisch ohne eigene Dauer und ohne CPU-/GPU-Arbeit
    private static bool LooksGenerated(FrameSample f) => f.FrameTimeMs < 3 && f.GpuBusyMs < 0.5 && f.CpuBusyMs < 2;

    /// <summary>
    /// Mit Frame Generation meldet PresentMon abwechselnd echte Bilder und Zwischenbilder mit fast 0 ms.
    /// Ohne Zusammenfassen gälte jedes zweite Bild als „Ruckler“. Sind solche Zwischenbilder häufig, werden sie
    /// dem vorangehenden echten Bild zugeschlagen; sonst bleiben die Frames unverändert.
    /// </summary>
    public static List<RealFrame> RealFrames(IReadOnlyList<FrameSample> frames)
    {
        bool frameGeneration = frames.Count > 0 && frames.Count(LooksGenerated) >= 0.25 * frames.Count;
        var result = new List<RealFrame>(frames.Count);
        foreach (var f in frames)
        {
            if (frameGeneration && LooksGenerated(f))
            {
                if (result.Count > 0)
                    result[^1] = result[^1] with { FrameTimeMs = result[^1].FrameTimeMs + f.FrameTimeMs };
                continue;
            }
            result.Add(new RealFrame(f.TimeMs, f.FrameTimeMs, f.CpuBusyMs));
        }
        return result;
    }

    private const double MinHitchMs = 80;
    private const double SameHitchSec = 0.5;     // Nachwackler direkt nach einem Hänger zählen nicht eigens
    private const double MinPeriodSec = 0.6, MaxPeriodSec = 3.0;
    private const int MinRunLength = 10;

    /// <summary>
    /// Sucht deutliche Hänger, die in gleichmäßigem Abstand wiederkehren. Zufällige Ruckler (Nachladen beim Fahren)
    /// erfüllen das praktisch nie über viele Wiederholungen – ein fester Takt deutet auf einen wiederkehrenden Vorgang.
    /// </summary>
    public static List<PeriodicRun> FindPeriodicHitches(IReadOnlyList<RealFrame> frames, double medianMs)
    {
        double threshold = Math.Max(MinHitchMs, 3 * medianMs);
        var hitches = new List<RealFrame>();
        foreach (var f in frames.Where(f => f.FrameTimeMs >= threshold))
            if (hitches.Count == 0 || (f.TimeMs - hitches[^1].TimeMs) / 1000 >= SameHitchSec)
                hitches.Add(f);

        var runs = new List<PeriodicRun>();
        var run = new List<RealFrame>();
        var gaps = new List<double>();

        void Close()
        {
            if (run.Count >= MinRunLength)
            {
                double mean = gaps.Average();
                double deviation = Math.Sqrt(gaps.Average(g => (g - mean) * (g - mean)));
                if (deviation <= 0.18 * mean)
                    runs.Add(new PeriodicRun(run[0].TimeMs / 1000, run[^1].TimeMs / 1000, run.Count,
                        PerformanceAnalyzer.Percentile(gaps, 50), run.Average(h => h.FrameTimeMs),
                        run.Count(h => h.CpuBusyMs >= 0.8 * h.FrameTimeMs) / (double)run.Count));
            }
            run.Clear();
            gaps.Clear();
        }

        foreach (var h in hitches)
        {
            if (run.Count == 0) { run.Add(h); continue; }
            double gap = (h.TimeMs - run[^1].TimeMs) / 1000;
            double expected = gaps.Count > 0 ? PerformanceAnalyzer.Percentile(gaps, 50) : gap;
            bool fits = gap is >= MinPeriodSec and <= MaxPeriodSec && Math.Abs(gap - expected) <= 0.3 * expected;
            if (!fits) { Close(); run.Add(h); continue; }
            run.Add(h);
            gaps.Add(gap);
        }
        Close();
        return runs;
    }
}
