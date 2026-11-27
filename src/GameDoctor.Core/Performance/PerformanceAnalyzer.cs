using GameDoctor.Core.Games.Cyberpunk;
using GameDoctor.Core.Localization;

namespace GameDoctor.Core.Performance;

public enum FrameLimit { Gpu, Cpu, Capped }

public sealed class PerformanceReport
{
    public required PerformanceRecording Recording { get; init; }
    public int FrameCount { get; init; }
    public double AvgFps { get; init; }
    public double OnePercentLowFps { get; init; }
    public double PointOnePercentLowFps { get; init; }
    public double MedianFrameTimeMs { get; init; }
    public double GpuLimitedShare { get; init; }
    public double CpuLimitedShare { get; init; }
    public double CappedShare { get; init; }
    public int StutterCount { get; init; }
    public double StuttersPerMinute { get; init; }
    public string? SettingsSummary { get; init; }
    public List<Finding> Findings { get; } = [];
    /// <summary>Frametime-Verlauf für Diagramme (Sekunde, ms) – ausgedünnt, Spitzen bleiben erhalten.</summary>
    public List<(double Sec, double Ms)> FrameTimeSeries { get; init; } = [];
    public List<(double Sec, double Ms)> Stutters { get; init; } = [];
    public double ExcludedSeconds { get; init; }
    public List<(double Sec, double Ms)> LoadPauses { get; init; } = [];
}

/// <summary>Wertet eine Aufnahme aus: FPS-Kennzahlen, Flaschenhals, Ruckler, Hardware-Limits, Empfehlungen.</summary>
public static class PerformanceAnalyzer
{
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToArray();
        double rank = p / 100.0 * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    public static FrameLimit Classify(FrameSample f)
    {
        if (f.GpuBusyMs >= 0.85 * f.FrameTimeMs) return FrameLimit.Gpu;
        if (f.CpuBusyMs >= 0.85 * f.FrameTimeMs) return FrameLimit.Cpu;
        return FrameLimit.Capped;
    }

    /// <summary>
    /// Nur die Bildkette (Swapchain) mit den meisten Frames – Launcher, Overlays oder Zweitfenster
    /// haben eigene Swapchains und würden die Statistik verfälschen.
    /// </summary>
    public static List<FrameSample> MainSwapChain(IReadOnlyList<FrameSample> frames)
    {
        if (frames.Count == 0) return [];
        var main = frames.GroupBy(f => f.SwapChain).MaxBy(g => g.Count())!.Key;
        return frames.Where(f => f.SwapChain == main).ToList();
    }

    /// <summary>
    /// Ein FPS-Limiter im Spiel wartet innerhalb des Frames – PresentMon zählt das als CPU-Zeit.
    /// Echtes CPU-Limit erkennt man zusätzlich daran, dass ein Kern stark ausgelastet ist.
    /// </summary>
    private static bool LooksLikeInternalLimiter(double avgMaxCorePct, double? avgGpuUtilPct) =>
        avgMaxCorePct < 50 && (avgGpuUtilPct ?? 0) < 75;

    /// <summary>Kurzbezeichnung für die Live-Anzeige.</summary>
    public static string? ClassifyWindow(IReadOnlyList<FrameSample> frames, double maxCorePct, double? gpuUtilPct)
    {
        if (frames.Count == 0) return null;
        var g = frames.GroupBy(Classify).OrderByDescending(x => x.Count()).First().Key;
        if (g == FrameLimit.Cpu && LooksLikeInternalLimiter(maxCorePct, gpuUtilPct)) g = FrameLimit.Capped;
        return g switch { FrameLimit.Gpu => L.T("GPU-Limit"), FrameLimit.Cpu => L.T("CPU-Limit"), _ => L.T("FPS-Limit / VSync") };
    }

    public sealed record GameplaySelection(List<FrameSample> Frames, double ExcludedSeconds, List<(double Sec, double Ms)> LoadPauses)
    {
        /// <summary>Frames der Nachladephase direkt nach einem Spielstand-Ladevorgang (separat bewertet).</summary>
        public List<FrameSample> WarmupFrames { get; init; } = [];
    }

    private const double LongLoadMs = 5000;
    // Mit vielen Mods dauert das Nachstreamen nach dem Laden spürbar länger (gemessen: ~50 s bei 300+ Archive-Mods)
    private const double WarmupMs = 60000;

    /// <summary>
    /// Trennt echtes Spielen von Hauptmenü, Ladebildschirmen und dem Fensterwechsel am Ende.
    /// - Frames ≥ 1 s sind Ladepausen (kein „Ruckler“).
    /// - Sekunden mit extrem hoher Bildrate (Menüs/Ladebildschirme rendern oft mit mehreren hundert FPS) fliegen raus.
    /// - Die letzten 3 s werden verworfen: dort wechselt der Spieler meist zurück zu GameDoctor.
    /// </summary>
    public static GameplaySelection SelectGameplay(List<FrameSample> frames)
    {
        var loadPauses = frames.Where(f => f.FrameTimeMs >= 1000).Select(f => (f.TimeMs / 1000, f.FrameTimeMs)).ToList();
        if (frames.Count == 0) return new([], 0, loadPauses);

        double endMs = frames[^1].TimeMs - 3000;
        var candidates = frames.Where(f => f.FrameTimeMs < 1000 && f.TimeMs <= endMs).ToList();

        // Langer Ladevorgang (Spielstand laden): alles davor ist Menü, die 60 s danach lädt das Spiel die Umgebung nach
        var warmup = new List<FrameSample>();
        var longLoads = frames.Where(f => f.FrameTimeMs >= LongLoadMs).Select(f => f.TimeMs).ToList();
        if (longLoads.Count > 0)
        {
            double firstLoadEnd = longLoads[0];
            candidates = candidates.Where(f => f.TimeMs > firstLoadEnd).ToList();
            bool InWarmup(FrameSample f) => longLoads.Any(end => f.TimeMs > end && f.TimeMs <= end + WarmupMs);
            warmup = candidates.Where(InWarmup).ToList();
            candidates = candidates.Where(f => !InWarmup(f)).ToList();
        }

        var buckets = candidates.GroupBy(f => (long)(f.TimeMs / 1000))
            .Select(g => (Sec: g.Key, Fps: 1000.0 * g.Count() / Math.Max(g.Sum(x => x.FrameTimeMs), 1), Frames: g.ToList()))
            .ToList();
        // Typische Spiel-Bildrate: Median über die Sekunden (zeitgewichtet), ohne offensichtliche Menü-Sekunden
        var plausible = buckets.Where(b => b.Fps <= 240).Select(b => b.Fps).ToList();
        double typical = plausible.Count > 0 ? Percentile(plausible, 50) : (buckets.Count > 0 ? Percentile(buckets.Select(b => b.Fps).ToList(), 50) : 0);
        double menuThreshold = Math.Max(150, typical * 2.5);

        var kept = new List<FrameSample>();
        foreach (var b in buckets)
        {
            if (b.Fps > menuThreshold) continue;
            kept.AddRange(b.Frames);
        }

        double recordedSec = (frames[^1].TimeMs - frames[0].TimeMs) / 1000;
        double keptSec = kept.Sum(f => f.FrameTimeMs) / 1000;
        double warmupSec = warmup.Sum(f => f.FrameTimeMs) / 1000;
        double excluded = Math.Max(0, recordedSec - keptSec - warmupSec - loadPauses.Sum(p => p.Item2) / 1000);
        return new(kept, excluded, loadPauses) { WarmupFrames = warmup };
    }

    public static PerformanceReport Analyze(PerformanceRecording rec, CyberpunkSettings? settings)
    {
        var allFrames = MainSwapChain(rec.Frames);
        var sensors = rec.Sensors;
        var gameplay = SelectGameplay(allFrames);
        var frames = gameplay.Frames;
        var ft = frames.Select(f => f.FrameTimeMs).ToList();

        if (frames.Count < 30)
        {
            var empty = new PerformanceReport { Recording = rec, SettingsSummary = settings?.Summary() };
            empty.Findings.Add(rec.PresentMonUsed
                ? new Finding(Severity.Hint, L.T("Keine Frame-Daten aufgezeichnet"),
                    L.T("PresentMon hat für Cyberpunk2077.exe keine Frames gemeldet. Lief das Spiel während der Aufnahme (nicht im Hauptmenü pausiert)?"),
                    L.T("Aufnahme starten, ins Spiel wechseln und mindestens 1–2 Minuten normal spielen."))
                : new Finding(Severity.Hint, L.T("Ohne PresentMon keine Frame-Analyse"),
                    rec.PresentMonError ?? L.T("PresentMon fehlt."), L.T("PresentMon im Performance-Tab herunterladen.")));
            AddSensorFindings(empty.Findings, sensors, settings, null, rec.ProcessName);
            return empty;
        }

        double total = ft.Sum();
        double avgFps = 1000.0 * frames.Count / total;
        double median = Percentile(ft, 50);
        double p99 = Percentile(ft, 99);
        double p999 = Percentile(ft, 99.9);

        // Anteile nach Spielzeit gewichten (nicht nach Frame-Anzahl) – sonst dominieren kurze Frames
        double gpuShare = frames.Where(x => Classify(x) == FrameLimit.Gpu).Sum(x => x.FrameTimeMs) / total;
        double cpuShare = frames.Where(x => Classify(x) == FrameLimit.Cpu).Sum(x => x.FrameTimeMs) / total;
        double capShare = Math.Max(0, 1 - gpuShare - cpuShare);

        // Scheinbares CPU-Limit, das in Wahrheit ein FPS-Limiter ist?
        var activeSensors = sensors.Where(s => s.GameRunning).ToList();
        if (activeSensors.Count == 0) activeSensors = sensors;
        double avgMaxCore = activeSensors.Count > 0 ? activeSensors.Average(s => s.CpuMaxCorePct) : 100;
        var gpuUtils = activeSensors.Where(s => s.GpuUtilPct.HasValue).Select(s => s.GpuUtilPct!.Value).ToList();
        double? avgGpuUtil = gpuUtils.Count > 0 ? gpuUtils.Average() : null;
        bool capFromSettings = settings is { FpsCapOn: true } && double.TryParse(settings.FpsCap, out var cap) && Math.Abs(avgFps - cap) / cap < 0.05;
        if (cpuShare >= 0.35 && (capFromSettings || LooksLikeInternalLimiter(avgMaxCore, avgGpuUtil)))
        {
            capShare += cpuShare;
            cpuShare = 0;
        }

        // Ruckler: deutlich länger als der gleitende Median der Umgebung
        // (bei Frame Generation auf Basis der echten Bilder, sonst gälte jedes zweite als Ruckler)
        var real = FrameRhythm.RealFrames(frames);
        var realFt = real.Select(x => x.FrameTimeMs).ToList();
        double realMedian = Percentile(realFt, 50);
        var stutters = new List<(double Sec, double Ms)>();
        const int window = 60;
        for (int i = 0; i < realFt.Count; i++)
        {
            int a = Math.Max(0, i - window), b = Math.Min(realFt.Count, i + window);
            var local = realFt.GetRange(a, b - a);
            double localMedian = Percentile(local, 50);
            if (realFt[i] > 2.0 * localMedian && realFt[i] > localMedian + 12)
                stutters.Add((real[i].TimeMs / 1000.0, realFt[i]));
        }
        double minutes = Math.Max(total / 60000.0, 1 / 60.0);

        var report = new PerformanceReport
        {
            Recording = rec,
            FrameCount = frames.Count,
            AvgFps = avgFps,
            OnePercentLowFps = 1000.0 / p99,
            PointOnePercentLowFps = 1000.0 / p999,
            MedianFrameTimeMs = median,
            GpuLimitedShare = gpuShare,
            CpuLimitedShare = cpuShare,
            CappedShare = capShare,
            StutterCount = stutters.Count,
            StuttersPerMinute = stutters.Count / minutes,
            SettingsSummary = settings?.Summary(),
            FrameTimeSeries = Downsample(frames, 1500),
            Stutters = stutters,
            ExcludedSeconds = gameplay.ExcludedSeconds,
            LoadPauses = gameplay.LoadPauses,
        };

        var f = report.Findings;
        bool isRtx4050 = rec.GpuName is { } gn && (gn.Contains("RTX 40") || gn.Contains("RTX 50"));

        // --- Überblick
        var excludedText = gameplay.ExcludedSeconds >= 1 || gameplay.LoadPauses.Count > 0
            ? " " + (gameplay.LoadPauses.Count > 0
                ? L.F("Nicht mitgezählt: {0:F0} s Menü/Ladebildschirm/Fensterwechsel und {1} Ladepause(n) (längste {2:F1} s).",
                    gameplay.ExcludedSeconds, gameplay.LoadPauses.Count, gameplay.LoadPauses.Max(p => p.Ms) / 1000)
                : L.F("Nicht mitgezählt: {0:F0} s Menü/Ladebildschirm/Fensterwechsel.", gameplay.ExcludedSeconds))
            : "";
        f.Add(new Finding(Severity.Info, L.F("Ø {0:F0} FPS · 1 % Low {1:F0} FPS · 0,1 % Low {2:F0} FPS", avgFps, report.OnePercentLowFps, report.PointOnePercentLowFps),
            L.F("{0:N0} Frames in {1} min Spielzeit. Median-Frametime {2:F1} ms.", frames.Count, TimeSpan.FromMilliseconds(total).ToString(@"m\:ss"), median) + excludedText + " " +
            L.T("„1 % Low“ ist die Bildrate der langsamsten 1 % der Frames – sie bestimmt, wie flüssig sich das Spiel anfühlt."))
        { Category = "Überblick" });

        // --- Nachladephase nach dem Laden eines Spielstands
        var warm = gameplay.WarmupFrames;
        if (warm.Count >= 30)
        {
            var wft = FrameRhythm.RealFrames(warm).Select(x => x.FrameTimeMs).ToList();
            int warmHitches = wft.Count(x => x > 2 * realMedian && x > realMedian + 12);
            double warmSec = wft.Sum() / 1000;
            double warmLow = 1000 / Percentile(wft, 99);
            bool bad = warmHitches / Math.Max(warmSec / 60, 0.1) > 3 * Math.Max(report.StuttersPerMinute, 1);
            f.Add(new Finding(bad ? Severity.Hint : Severity.Info,
                L.F("Nachladephase nach dem Laden: {0} Ruckler in {1:F0} s", warmHitches, warmSec),
                L.F("Direkt nach dem Laden eines Spielstands streamt Cyberpunk Texturen, Modelle und Skripte der Umgebung nach (1 % Low hier {0:F0} FPS). Diese Phase ist getrennt ausgewertet und fließt nicht in die Werte oben ein.", warmLow) +
                (bad ? " " + L.T("Sie ist bei dir deutlich unruhiger als das normale Spielen – typisch für sehr viele Mods (jede Archive-Mod muss beim Laden mit eingelesen werden) und knappen Arbeitsspeicher.") : ""),
                bad ? L.T("Nach dem Laden etwa eine Minute ruhig spielen (nicht sofort losfahren), bis die Umgebung fertig geladen ist. Dauerhaft helfen weniger Archive-Mods und mehr RAM.") : null)
            { Category = "Ruckler" });
        }

        // --- Flaschenhals
        static string pct(double x) => $"{x * 100:F0} %";
        if (gpuShare >= 0.6)
        {
            var recs = new List<string>();
            if (settings is not null)
            {
                if (settings.PathTracing) recs.Add(L.T("Pathtracing ausschalten – kostet mit Abstand am meisten GPU-Leistung"));
                else if (settings.RayTracing) recs.Add(L.F("Raytracing reduzieren (Spiegelungen: {0}, Beleuchtung: {1})", settings.RtReflections ? L.T("an") : L.T("aus"), settings.RtLighting));
                if (settings.Upscaler == "DLSS" && settings.DlssMode is "Quality" or "DLAA")
                    recs.Add(L.F("DLSS von „{0}“ auf „Balanced“ stellen (mit dem Transformer-Modell kaum sichtbar)", settings.DlssMode));
                if (settings.Get("/graphics/advanced/VolumetricFogResolution") is "High" or "Ultra") recs.Add(L.T("Volumetrischer Nebel: Medium"));
                if (settings.Get("/graphics/advanced/ScreenSpaceReflectionsQuality") is "High" or "Ultra" or "Insane")
                    recs.Add(settings.RtReflections ? L.T("Bildschirmraum-Spiegelungen auf „Off“ (werden bei RT-Spiegelungen ohnehin ersetzt)") : L.T("Bildschirmraum-Spiegelungen: Medium"));
                if (settings.Get("/graphics/advanced/CascadedShadowsResolution") == "High") recs.Add(L.T("Kaskadierte Schatten-Auflösung: Medium"));
                if (isRtx4050 && !settings.DlssFrameGen && settings.FrameGeneration is null or "Off" && avgFps >= 40)
                    recs.Add(L.T("DLSS Frame Generation einschalten (deine GPU unterstützt es; ab ca. 40–50 Basis-FPS sinnvoll)"));
            }
            f.Add(new Finding(avgFps < 55 ? Severity.Hint : Severity.Info,
                L.F("Die Grafikkarte begrenzt die Bildrate ({0} der Spielzeit)", pct(gpuShare)),
                L.F("Die GPU war {0} der Zeit voll ausgelastet, die CPU hätte mehr Bilder liefern können. Das ist der Normalfall – mehr FPS gibt es hier nur über die Grafikeinstellungen.", pct(gpuShare)),
                recs.Count > 0 ? L.T("Mögliche Stellschrauben, größter Effekt zuerst – jeweils neu messen.") : null,
                recs.Count > 0 ? recs : null)
            { Category = "Flaschenhals" });
        }
        else if (cpuShare >= 0.35)
        {
            double maxCore = sensors.Count > 0 ? sensors.Average(s => s.CpuMaxCorePct) : 0;
            var recs = new List<string>();
            if (settings?.CrowdDensity is "High" or "Medium") recs.Add(L.F("Crowd-Dichte von „{0}“ auf „Low“ – größter CPU-Hebel in Cyberpunk", settings.CrowdDensity));
            if (settings?.RayTracing == true) recs.Add(L.T("Raytracing kostet auch CPU-Zeit (BVH-Aufbau) – testweise reduzieren"));
            if (isRtx4050 && settings is { DlssFrameGen: false }) recs.Add(L.T("DLSS Frame Generation hilft gerade im CPU-Limit, weil die GPU Zwischenbilder erzeugt"));
            recs.Add(L.T("Skript-lastige Mods (z. B. NPC-/Verkehrs-/KI-Mods) belasten den Hauptthread"));
            recs.Add(L.T("Hintergrundprogramme (Browser, Overlays, Aufnahme) beim Spielen schließen"));
            f.Add(new Finding(avgFps < 55 ? Severity.Warning : Severity.Hint,
                L.F("Der Prozessor begrenzt die Bildrate ({0} der Spielzeit)", pct(cpuShare)),
                L.F("{0} der Zeit hat die GPU auf die CPU gewartet. Der am stärksten ausgelastete Kern lag im Schnitt bei {1:F0} %. Spiele hängen oft an einem einzelnen Hauptthread – die Gesamtauslastung wirkt dann niedrig, obwohl die CPU limitiert.", pct(cpuShare), maxCore),
                L.T("Mit dem ersten Punkt anfangen und jeweils neu messen."), recs)
            { Category = "Flaschenhals" });
        }
        else if (capShare >= 0.5)
        {
            string why = settings switch
            {
                { VSync: true } => L.T("VSync ist im Spiel aktiv."),
                { FpsCapOn: true } => L.F("Im Spiel ist ein FPS-Limit von {0} gesetzt.", settings.FpsCap),
                _ => L.T("Vermutlich durch ein FPS-Limit im Programm selbst, im Treiber/RTSS oder durch VSync/G-Sync im Treiber."),
            };
            f.Add(new Finding(Severity.Info, L.F("Bildrate ist begrenzt ({0} der Spielzeit)", pct(capShare)),
                L.F("Weder CPU noch GPU waren voll ausgelastet – die Bildrate wird künstlich begrenzt. {0} Das ist gewollt und kein Problem.", why), null)
            { Category = "Flaschenhals" });
        }
        else
        {
            f.Add(new Finding(Severity.Info, L.T("Wechselnder Flaschenhals"),
                L.F("GPU-Limit {0}, CPU-Limit {1}, begrenzt {2} der Spielzeit. Je nach Szene limitiert mal die eine, mal die andere Komponente.", pct(gpuShare), pct(cpuShare), pct(capShare)), null)
            { Category = "Flaschenhals" });
        }

        // --- Stottern in festem Takt (z. B. etwa einmal pro Sekunde)
        var rhythm = FrameRhythm.FindPeriodicHitches(real, realMedian);
        if (rhythm.Count > 0) f.Add(RhythmFinding(rhythm, rec.BackgroundPrograms));

        // --- Ruckler
        if (report.StuttersPerMinute >= 2)
        {
            var causes = new List<string>();
            static bool VramFull(SensorSample s) => s.VramUsedMb is double u && s.VramTotalMb is double t && u >= 0.95 * t;
            static bool RamTight(SensorSample s) => s.CommitUsedGb >= 0.9 * s.CommitLimitGb || s.RamUsedGb >= 0.92 * s.RamTotalGb;

            int vramHits = 0, ramHits = 0;
            foreach (var (sec, _) in stutters)
            {
                var s = sensors.MinBy(x => Math.Abs(x.TimeMs / 1000 - sec));
                if (s is null) continue;
                if (VramFull(s)) vramHits++;
                if (RamTight(s)) ramHits++;
            }
            // Nur als Ursache werten, wenn Ruckler deutlich häufiger in diese Phasen fallen als es dem Zeitanteil entspricht
            // (ist der Speicher die ganze Zeit knapp, sagt die Übereinstimmung nichts aus)
            double vramBase = sensors.Count > 0 ? sensors.Count(VramFull) / (double)sensors.Count : 0;
            double ramBase = sensors.Count > 0 ? sensors.Count(RamTight) / (double)sensors.Count : 0;
            bool vramCause = stutters.Count > 0 && vramHits >= stutters.Count / 2 && vramHits / (double)stutters.Count >= vramBase + 0.25;
            bool ramCause = stutters.Count > 0 && ramHits >= stutters.Count / 3 && ramHits / (double)stutters.Count >= ramBase + 0.25;
            if (vramCause) causes.Add(L.F("{0} von {1} Rucklern bei fast vollem VRAM (sonst nur {2:F0} % der Zeit) → Texturen werden nachgeladen", vramHits, stutters.Count, vramBase * 100));
            if (ramCause) causes.Add(L.F("{0} von {1} Rucklern bei knappem Arbeitsspeicher (sonst nur {2:F0} % der Zeit) → Windows lagert aus", ramHits, stutters.Count, ramBase * 100));
            if (!vramCause && !ramCause && ramBase >= 0.8) causes.Add(L.T("Der Arbeitsspeicher war fast die ganze Zeit knapp – das kann Ruckler allgemein begünstigen"));
            causes.AddRange(stutters.OrderByDescending(s => s.Ms).Take(5).Select(s => L.F("Spitze bei {0}: {1:F0} ms", TimeSpan.FromSeconds(s.Sec).ToString(@"m\:ss"), s.Ms)));

            f.Add(new Finding(report.StuttersPerMinute >= 6 ? Severity.Warning : Severity.Hint,
                L.F("{0} Ruckler ({1:F1} pro Minute)", stutters.Count, report.StuttersPerMinute),
                L.T("Einzelne Frames dauerten deutlich länger als ihre Nachbarn – das spürt man als Ruckeln, auch bei hohen Durchschnitts-FPS. Typische Ursachen in Cyberpunk: Nachladen beim schnellen Fahren, voller VRAM, Shader-Kompilierung nach Treiber-Updates, Hintergrundprozesse."),
                rhythm.Count > 0
                    ? L.T("Zuerst um das regelmäßige Stottern kümmern (eigener Hinweis).")
                    : ramCause || (!vramCause && ramBase >= 0.8)
                    ?L.T("Zuerst den Arbeitsspeicher entlasten – siehe Hinweis zum virtuellen Speicher/zur Auslagerungsdatei.")
                    : vramCause
                        ? L.T("VRAM entlasten (Texturqualität/Raytracing reduzieren).")
                        : L.T("Beim schnellen Fahren normal; bei Ruckeln im Stand Hintergrundprogramme und Mods prüfen."),
                causes)
            { Category = "Ruckler" });
        }
        else
        {
            f.Add(new Finding(Severity.Info, L.F("Kaum Ruckler ({0} in der Aufnahme)", stutters.Count),
                L.T("Die Frametimes sind gleichmäßig."), null) { Category = "Ruckler" });
        }

        double realP99 = Percentile(realFt, 99);
        if (realP99 > 2.2 * realMedian && report.StuttersPerMinute < 2)
            f.Add(new Finding(Severity.Hint, L.T("Ungleichmäßige Frametimes"),
                L.F("Die langsamsten 1 % der Frames brauchen {0:F1} ms, der Median {1:F1} ms. Das wirkt unruhiger, als die FPS vermuten lassen.", realP99, realMedian),
                L.T("Ein FPS-Limit knapp unter dem erreichten Minimum (z. B. per Treiber oder RTSS) glättet die Darstellung.")) { Category = "Ruckler" });

        // --- Darstellung
        var mode = frames.GroupBy(x => x.PresentMode).OrderByDescending(g => g.Count()).First().Key;
        if (mode.StartsWith("Composed", StringComparison.OrdinalIgnoreCase))
            f.Add(new Finding(Severity.Hint, L.F("Bildausgabe über den Desktop-Compositor ({0})", mode),
                L.T("Windows komponiert jedes Bild noch einmal mit dem Desktop. Das kostet Latenz und manchmal Bildrate. Ursache sind meist Overlays (Discord, Overwolf, Aufnahme-Tools) oder ein Fenstermodus."),
                L.T("Overlays für Cyberpunk deaktivieren; ‚Randloses Fenster‘ sollte unter Windows 11 normalerweise ‚Hardware: Independent Flip‘ erreichen.")) { Category = "Darstellung" });

        if (gpuShare >= 0.6 && settings?.Reflex == "Disabled" && rec.GpuName?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) == true)
            f.Add(new Finding(Severity.Hint, L.T("NVIDIA Reflex ist aus"),
                L.T("Im GPU-Limit stauen sich Bilder in der Warteschlange – das erhöht die Eingabeverzögerung spürbar."),
                L.T("Reflex auf „Enabled“ stellen (kostet praktisch keine FPS).")) { Category = "Darstellung" });

        AddSensorFindings(f, sensors, settings, gpuShare, rec.ProcessName);
        return report;
    }

    private static Finding RhythmFinding(List<PeriodicRun> runs, List<string>? recordedPrograms)
    {
        // Ältere Aufnahmen haben die Liste nicht – dann zählt, was gerade läuft (und das wird so benannt)
        var programs = recordedPrograms ?? BackgroundTools.Detect();
        var sensorTools = programs.Where(BackgroundTools.ReadsSensors).ToList();
        var otherTools = programs.Where(p => !BackgroundTools.ReadsSensors(p)).ToList();
        var evidence = new List<string>();
        if (sensorTools.Count > 0)
            evidence.Add(L.F("Zuerst testen – lesen laufend Hardware-Werte aus: {0}", string.Join(", ", sensorTools)));
        if (otherTools.Count > 0)
            evidence.Add(L.F("Danach testen – weitere Hintergrundprogramme: {0}", string.Join(", ", otherTools)));
        if (programs.Count > 0 && recordedPrograms is null)
            evidence.Add(L.T("(Diese Programme laufen jetzt gerade – bei dieser älteren Aufnahme wurden sie nicht mitgespeichert.)"));

        int count = runs.Sum(r => r.Count);
        double period = runs.Sum(r => r.PeriodSec * r.Count) / count;
        double ms = runs.Sum(r => r.AvgMs * r.Count) / count;
        bool cpu = runs.Sum(r => r.CpuBoundShare * r.Count) / count >= 0.7;
        static string At(double sec) => TimeSpan.FromSeconds(sec).ToString(@"m\:ss");

        return new Finding(Severity.Warning,
            L.F("Regelmäßiges Stottern: etwa alle {0:F1} Sekunden", period),
            L.F("Das Spiel blieb in festem Takt kurz stehen – {0} Mal, jeweils etwa {1:F0} ms.", count, ms) + " " +
            (cpu
                ? L.T("Der Prozessor war dabei beschäftigt, die Grafikkarte musste warten. Grafikeinstellungen ändern daran in der Regel nichts. So ein Takt entsteht durch etwas, das in festen Abständen läuft – ein Programm im Hintergrund (zum Beispiel eines, das Hardware-Werte ausliest) oder eine Mod mit Skripten.")
                : L.T("Der Prozessor war dabei nicht durchgehend beschäftigt. So ein Takt entsteht durch etwas, das in festen Abständen läuft; welche Seite bremst, lässt sich hier nicht klar sagen.")),
            L.T("Passiert es auch in anderen Spielen oder ohne Mods? Dann ist sehr wahrscheinlich ein Programm im Hintergrund schuld: Beende, während es stottert, nacheinander ein Programm (die unten genannten zuerst). Hört es sofort auf, hast du den Auslöser. Passiert es nur in diesem Spiel, merke dir, wobei es anfängt, und schalte die dazu passenden Mods aus – sonst jeweils die Hälfte der Skript-Mods."),
            evidence.Concat(runs.OrderByDescending(r => r.Count).Take(3).OrderBy(r => r.StartSec)
                .Select(r => L.F("Von {0} bis {1}: {2} Hänger, alle {3:F1} s, je etwa {4:F0} ms", At(r.StartSec), At(r.EndSec), r.Count, r.PeriodSec, r.AvgMs))).ToList())
        { Category = "Ruckler" };
    }

    private static void AddSensorFindings(List<Finding> f, List<SensorSample> sensors, CyberpunkSettings? settings, double? gpuShare, string processName)
    {
        var appName = processName.Equals("Cyberpunk2077.exe", StringComparison.OrdinalIgnoreCase) ? "Cyberpunk" : processName;
        var ingame = sensors.Where(s => s.GameRunning).ToList();
        if (ingame.Count == 0) ingame = sensors;
        if (ingame.Count == 0) return;

        // VRAM
        var vram = ingame.Where(s => s.VramUsedMb.HasValue && s.VramTotalMb > 0).ToList();
        if (vram.Count > 0)
        {
            double maxUsed = vram.Max(s => s.VramUsedMb!.Value);
            double totalMb = vram[0].VramTotalMb!.Value;
            double full = vram.Count(s => s.VramUsedMb >= 0.95 * s.VramTotalMb) / (double)vram.Count;
            if (full >= 0.1)
            {
                var recs = new List<string>();
                if (settings?.TextureQuality == "High") recs.Add(L.T("Texturqualität: Medium"));
                if (settings?.RayTracing == true) recs.Add(L.T("Raytracing reduziert den VRAM-Bedarf deutlich, wenn es aus ist"));
                recs.Add(L.T("Hochauflösende Textur-Mods (4K/8K) entfernen"));
                recs.Add(L.T("Browser mit Hardwarebeschleunigung schließen – belegt ebenfalls VRAM"));
                f.Add(new Finding(Severity.Warning, L.F("Grafikspeicher am Limit ({0:F1} von {1:F1} GB)", maxUsed / 1024, totalMb / 1024),
                    L.F("In {0:F0} % der Messungen war der VRAM zu über 95 % belegt. Dann lagert der Treiber in den langsameren Arbeitsspeicher aus – Ruckler und Nachladen sind die Folge.", full * 100),
                    L.T("So wird VRAM frei:"), recs) { Category = "Speicher" });
            }
            else
            {
                f.Add(new Finding(Severity.Info, L.F("VRAM ausreichend (max. {0:F1} von {1:F1} GB)", maxUsed / 1024, totalMb / 1024),
                    L.T("Der Grafikspeicher war nicht der limitierende Faktor."), null) { Category = "Speicher" });
            }
        }

        // RAM / Auslagerung
        double maxRam = ingame.Max(s => s.RamUsedGb / s.RamTotalGb);
        double maxCommit = ingame.Max(s => s.CommitUsedGb / s.CommitLimitGb);
        if (maxCommit >= 0.9)
        {
            var peak = ingame.MaxBy(s => s.CommitUsedGb / s.CommitLimitGb)!;
            var pf = Platform.PageFileInfo.Read();
            // Ziel: Commit-Limit mit 40 % Reserve über der gemessenen Spitze, auf 8 GB gerundet
            double targetLimitGb = peak.CommitUsedGb * 1.4;
            int suggestedMb = (int)Math.Ceiling(Math.Max(targetLimitGb - peak.RamTotalGb, 16) / 8) * 8 * 1024;
            var evidence = new List<string>
            {
                L.F("Spitze: {0:F1} von {1:F1} GB belegt (RAM {2:F0} GB + Auslagerungsdatei)", peak.CommitUsedGb, peak.CommitLimitGb, peak.RamTotalGb),
                L.F("Aktuelle Einstellung: {0}", pf.Describe()),
            };
            string rec;
            var fixedFile = pf.Files.FirstOrDefault(x => !x.SystemManaged);
            if (fixedFile is not null && fixedFile.InitialMb < suggestedMb)
            {
                rec = L.F("Anfangsgröße der Auslagerungsdatei auf {0} von {1:N0} MB auf {2:N0} MB erhöhen (Maximum mindestens gleich groß). Windows vergrößert sie sonst erst im Notfall – das verursacht Ruckler und kann Abstürze auslösen. Einstellung: Windows-Suche „Erweiterte Systemeinstellungen“ → Leistung → Einstellungen → Erweitert → Virtueller Arbeitsspeicher → Ändern. Danach neu starten.",
                    fixedFile.Drive, fixedFile.InitialMb, suggestedMb);
                if (fixedFile.DriveFreeGb is double free)
                    evidence.Add(L.F("Freier Platz auf Laufwerk {0} {1:F0} GB", fixedFile.Drive, free));
            }
            else
            {
                rec = L.T("Auslagerungsdatei auf einer SSD mit genug freiem Platz automatisch verwalten lassen, Hintergrundprogramme schließen. Bei sehr vielen Mods sind 32 GB RAM die eigentliche Lösung.");
            }
            f.Add(new Finding(Severity.Critical, L.F("Virtueller Speicher fast erschöpft ({0:F0} %)", maxCommit * 100),
                L.T("Arbeitsspeicher plus Auslagerungsdatei waren fast vollständig belegt. Läuft der virtuelle Speicher über, stürzt das Spiel ohne klare Fehlermeldung ab – das ist eine häufige Ursache für Abstürze „in der Engine“ bei stark gemoddeten Spielen."),
                rec, evidence) { Category = "Speicher" });
        }
        else if (maxRam >= 0.9)
            f.Add(new Finding(Severity.Warning, L.F("Arbeitsspeicher fast voll ({0:F0} %)", maxRam * 100),
                L.F("Spitzenbelegung {0:F1} von {1:F0} GB. Windows beginnt auszulagern – das verursacht Ruckler.", ingame.Max(s => s.RamUsedGb), ingame[0].RamTotalGb),
                L.T("Browser, Discord & Co. schließen; bei sehr vielen Mods sind 32 GB RAM sinnvoll.")) { Category = "Speicher" });

        var ws = ingame.Where(s => s.GameWorkingSetGb.HasValue).Select(s => s.GameWorkingSetGb!.Value).DefaultIfEmpty().Max();
        if (ws > 0)
            f.Add(new Finding(Severity.Info, L.F("{0} belegt bis zu {1:F1} GB RAM", appName, ws),
                L.F("Gesamtbelegung des Systems bis {0:F1} von {1:F0} GB, virtueller Speicher bis {2:F0} %.", ingame.Max(s => s.RamUsedGb), ingame[0].RamTotalGb, maxCommit * 100), null)
            { Category = "Speicher" });

        // GPU-Temperatur & Drosselung
        var gpu = ingame.Where(s => s.GpuTempC.HasValue).ToList();
        if (gpu.Count > 0)
        {
            double maxTemp = gpu.Max(s => s.GpuTempC!.Value);
            double thermal = gpu.Count(s => (s.GpuThrottleReasons & (NvidiaGpu.ThrottleSwThermal | NvidiaGpu.ThrottleHwThermal | NvidiaGpu.ThrottleHwSlowdown | NvidiaGpu.ThrottleHwPowerBrake)) != 0) / (double)gpu.Count;
            double power = gpu.Count(s => (s.GpuThrottleReasons & NvidiaGpu.ThrottleSwPowerCap) != 0) / (double)gpu.Count;

            if (thermal >= 0.05)
                f.Add(new Finding(Severity.Warning, L.F("Grafikkarte drosselt wegen Temperatur ({0:F0} % der Zeit)", thermal * 100),
                    L.F("Die GPU hat ihren Takt wegen Hitze oder einer Hardware-Bremse gesenkt (max. {0:F0} °C). Das kostet FPS und erzeugt Schwankungen.", maxTemp),
                    L.T("Gehäuselüftung und Staub prüfen, Lüfterkurve anpassen; ein leichtes Undervolting senkt die Temperatur oft deutlich.")) { Category = "Hardware" });
            else if (maxTemp >= 85)
                f.Add(new Finding(Severity.Hint, L.F("GPU wird warm ({0:F0} °C)", maxTemp),
                    L.T("Noch keine Drosselung, aber nahe am Limit."), L.T("Gehäuselüftung prüfen.")) { Category = "Hardware" });
            else
                f.Add(new Finding(Severity.Info, L.F("GPU-Temperatur in Ordnung (max. {0:F0} °C)", maxTemp), L.T("Keine thermische Drosselung."), null) { Category = "Hardware" });

            if (power >= 0.3 && gpuShare >= 0.6)
                f.Add(new Finding(Severity.Info, L.F("GPU läuft am Powerlimit ({0:F0} % der Zeit)", power * 100),
                    L.F("Die Karte nutzt ihr Leistungsbudget ({0:F0} W) voll aus. Unter Volllast ist das normal und kein Fehler.",
                        gpu.Where(s => s.GpuPowerLimitW.HasValue).Select(s => s.GpuPowerLimitW!.Value).DefaultIfEmpty().Max()), null)
                { Category = "Hardware" });
        }
    }

    private static List<(double, double)> Downsample(List<FrameSample> frames, int maxPoints)
    {
        if (frames.Count <= maxPoints) return frames.Select(f => (f.TimeMs / 1000, f.FrameTimeMs)).ToList();
        int bucket = (int)Math.Ceiling(frames.Count / (double)maxPoints);
        var result = new List<(double, double)>(maxPoints);
        for (int i = 0; i < frames.Count; i += bucket)
        {
            var slice = frames.Skip(i).Take(bucket).ToList();
            var worst = slice.MaxBy(f => f.FrameTimeMs);
            result.Add((worst.TimeMs / 1000, worst.FrameTimeMs));
        }
        return result;
    }
}
