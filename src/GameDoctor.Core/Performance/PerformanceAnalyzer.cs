using GameDoctor.Core.Games.Cyberpunk;

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
        return g switch { FrameLimit.Gpu => "GPU-Limit", FrameLimit.Cpu => "CPU-Limit", _ => "FPS-Limit / VSync" };
    }

    public static PerformanceReport Analyze(PerformanceRecording rec, CyberpunkSettings? settings)
    {
        var frames = MainSwapChain(rec.Frames);
        var sensors = rec.Sensors;
        var ft = frames.Select(f => f.FrameTimeMs).ToList();

        if (frames.Count < 30)
        {
            var empty = new PerformanceReport { Recording = rec, SettingsSummary = settings?.Summary() };
            empty.Findings.Add(rec.PresentMonUsed
                ? new Finding(Severity.Hint, "Keine Frame-Daten aufgezeichnet",
                    "PresentMon hat für Cyberpunk2077.exe keine Frames gemeldet. Lief das Spiel während der Aufnahme (nicht im Hauptmenü pausiert)?",
                    "Aufnahme starten, ins Spiel wechseln und mindestens 1–2 Minuten normal spielen.")
                : new Finding(Severity.Hint, "Ohne PresentMon keine Frame-Analyse",
                    rec.PresentMonError ?? "PresentMon fehlt.", "PresentMon im Performance-Tab herunterladen."));
            AddSensorFindings(empty.Findings, sensors, settings, null, rec.ProcessName);
            return empty;
        }

        double total = ft.Sum();
        double avgFps = 1000.0 * frames.Count / total;
        double median = Percentile(ft, 50);
        double p99 = Percentile(ft, 99);
        double p999 = Percentile(ft, 99.9);

        var limits = frames.Select(Classify).ToList();
        double gpuShare = limits.Count(l => l == FrameLimit.Gpu) / (double)limits.Count;
        double cpuShare = limits.Count(l => l == FrameLimit.Cpu) / (double)limits.Count;
        double capShare = 1 - gpuShare - cpuShare;

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
        var stutters = new List<(double Sec, double Ms)>();
        const int window = 60;
        for (int i = 0; i < ft.Count; i++)
        {
            int a = Math.Max(0, i - window), b = Math.Min(ft.Count, i + window);
            var local = ft.GetRange(a, b - a);
            double localMedian = Percentile(local, 50);
            if (ft[i] > 2.0 * localMedian && ft[i] > localMedian + 12)
                stutters.Add((frames[i].TimeMs / 1000.0, ft[i]));
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
        };

        var f = report.Findings;
        bool isRtx4050 = rec.GpuName is { } gn && (gn.Contains("RTX 40") || gn.Contains("RTX 50"));

        // --- Überblick
        f.Add(new Finding(Severity.Info, $"Ø {avgFps:F0} FPS · 1 % Low {report.OnePercentLowFps:F0} FPS · 0,1 % Low {report.PointOnePercentLowFps:F0} FPS",
            $"{frames.Count:N0} Frames in {TimeSpan.FromMilliseconds(total):m\\:ss} min. Median-Frametime {median:F1} ms. " +
            "„1 % Low“ ist die Bildrate der langsamsten 1 % der Frames – sie bestimmt, wie flüssig sich das Spiel anfühlt.")
        { Category = "Überblick" });

        // --- Flaschenhals
        string pct(double x) => $"{x * 100:F0} %";
        if (gpuShare >= 0.6)
        {
            var recs = new List<string>();
            if (settings is not null)
            {
                if (settings.PathTracing) recs.Add("Pathtracing ausschalten – kostet mit Abstand am meisten GPU-Leistung");
                else if (settings.RayTracing) recs.Add($"Raytracing reduzieren (Spiegelungen: {(settings.RtReflections ? "an" : "aus")}, Beleuchtung: {settings.RtLighting})");
                if (settings.Upscaler == "DLSS" && settings.DlssMode is "Quality" or "DLAA")
                    recs.Add($"DLSS von „{settings.DlssMode}“ auf „Balanced“ stellen (mit dem Transformer-Modell kaum sichtbar)");
                if (settings.Get("/graphics/advanced/VolumetricFogResolution") is "High" or "Ultra") recs.Add("Volumetrischer Nebel: Medium");
                if (settings.Get("/graphics/advanced/ScreenSpaceReflectionsQuality") is "High" or "Ultra" or "Insane")
                    recs.Add(settings.RtReflections ? "Bildschirmraum-Spiegelungen auf „Off“ (werden bei RT-Spiegelungen ohnehin ersetzt)" : "Bildschirmraum-Spiegelungen: Medium");
                if (settings.Get("/graphics/advanced/CascadedShadowsResolution") == "High") recs.Add("Kaskadierte Schatten-Auflösung: Medium");
                if (isRtx4050 && !settings.DlssFrameGen && settings.FrameGeneration is null or "Off" && avgFps >= 40)
                    recs.Add("DLSS Frame Generation einschalten (deine GPU unterstützt es; ab ca. 40–50 Basis-FPS sinnvoll)");
            }
            f.Add(new Finding(avgFps < 55 ? Severity.Hint : Severity.Info,
                $"Die Grafikkarte begrenzt die Bildrate ({pct(gpuShare)} der Frames)",
                $"Die GPU war in {pct(gpuShare)} der Frames voll ausgelastet, die CPU hätte mehr Bilder liefern können. Das ist der Normalfall – mehr FPS gibt es hier nur über die Grafikeinstellungen.",
                recs.Count > 0 ? "Mögliche Stellschrauben, größter Effekt zuerst – jeweils neu messen." : null,
                recs.Count > 0 ? recs : null)
            { Category = "Flaschenhals" });
        }
        else if (cpuShare >= 0.35)
        {
            double maxCore = sensors.Count > 0 ? sensors.Average(s => s.CpuMaxCorePct) : 0;
            var recs = new List<string>();
            if (settings?.CrowdDensity is "High" or "Medium") recs.Add($"Crowd-Dichte von „{settings.CrowdDensity}“ auf „Low“ – größter CPU-Hebel in Cyberpunk");
            if (settings?.RayTracing == true) recs.Add("Raytracing kostet auch CPU-Zeit (BVH-Aufbau) – testweise reduzieren");
            if (isRtx4050 && settings is { DlssFrameGen: false }) recs.Add("DLSS Frame Generation hilft gerade im CPU-Limit, weil die GPU Zwischenbilder erzeugt");
            recs.Add("Skript-lastige Mods (z. B. NPC-/Verkehrs-/KI-Mods) belasten den Hauptthread");
            recs.Add("Hintergrundprogramme (Browser, Overlays, Aufnahme) beim Spielen schließen");
            f.Add(new Finding(avgFps < 55 ? Severity.Warning : Severity.Hint,
                $"Der Prozessor begrenzt die Bildrate ({pct(cpuShare)} der Frames)",
                $"In {pct(cpuShare)} der Frames hat die GPU auf die CPU gewartet. Der am stärksten ausgelastete Kern lag im Schnitt bei {maxCore:F0} %. " +
                "Spiele hängen oft an einem einzelnen Hauptthread – die Gesamtauslastung wirkt dann niedrig, obwohl die CPU limitiert.",
                "Mit dem ersten Punkt anfangen und jeweils neu messen.", recs)
            { Category = "Flaschenhals" });
        }
        else if (capShare >= 0.5)
        {
            string why = settings switch
            {
                { VSync: true } => "VSync ist im Spiel aktiv.",
                { FpsCapOn: true } => $"Im Spiel ist ein FPS-Limit von {settings.FpsCap} gesetzt.",
                _ => "Vermutlich durch ein FPS-Limit im Programm selbst, im Treiber/RTSS oder durch VSync/G-Sync im Treiber.",
            };
            f.Add(new Finding(Severity.Info, $"Bildrate ist begrenzt ({pct(capShare)} der Frames)",
                $"Weder CPU noch GPU waren voll ausgelastet – die Bildrate wird künstlich begrenzt. {why} Das ist gewollt und kein Problem.", null)
            { Category = "Flaschenhals" });
        }
        else
        {
            f.Add(new Finding(Severity.Info, "Wechselnder Flaschenhals",
                $"GPU-Limit {pct(gpuShare)}, CPU-Limit {pct(cpuShare)}, begrenzt {pct(capShare)} der Frames. Je nach Szene limitiert mal die eine, mal die andere Komponente.", null)
            { Category = "Flaschenhals" });
        }

        // --- Ruckler
        if (report.StuttersPerMinute >= 2)
        {
            var causes = new List<string>();
            int vramHits = 0, ramHits = 0;
            foreach (var (sec, _) in stutters)
            {
                var s = sensors.MinBy(x => Math.Abs(x.TimeMs / 1000 - sec));
                if (s is null) continue;
                if (s.VramUsedMb is double u && s.VramTotalMb is double t && u >= 0.95 * t) vramHits++;
                if (s.CommitUsedGb >= 0.9 * s.CommitLimitGb || s.RamUsedGb >= 0.92 * s.RamTotalGb) ramHits++;
            }
            if (stutters.Count > 0 && vramHits >= stutters.Count / 2) causes.Add($"{vramHits} von {stutters.Count} Rucklern bei fast vollem VRAM → Texturen werden nachgeladen");
            if (stutters.Count > 0 && ramHits >= stutters.Count / 3) causes.Add($"{ramHits} Ruckler bei knappem Arbeitsspeicher → Windows lagert aus");
            causes.AddRange(stutters.OrderByDescending(s => s.Ms).Take(5).Select(s => $"Spitze bei {TimeSpan.FromSeconds(s.Sec):m\\:ss}: {s.Ms:F0} ms"));

            f.Add(new Finding(report.StuttersPerMinute >= 6 ? Severity.Warning : Severity.Hint,
                $"{stutters.Count} Ruckler ({report.StuttersPerMinute:F1} pro Minute)",
                "Einzelne Frames dauerten deutlich länger als ihre Nachbarn – das spürt man als Ruckeln, auch bei hohen Durchschnitts-FPS. " +
                "Typische Ursachen in Cyberpunk: Nachladen beim schnellen Fahren, voller VRAM, Shader-Kompilierung nach Treiber-Updates, Hintergrundprozesse.",
                vramHits > 0 ? "VRAM entlasten (Texturqualität/Raytracing reduzieren)." : "Beim schnellen Fahren normal; bei Ruckeln im Stand Hintergrundprogramme und Mods prüfen.",
                causes)
            { Category = "Ruckler" });
        }
        else
        {
            f.Add(new Finding(Severity.Info, $"Kaum Ruckler ({stutters.Count} in der Aufnahme)",
                "Die Frametimes sind gleichmäßig.", null) { Category = "Ruckler" });
        }

        if (p99 > 2.2 * median && report.StuttersPerMinute < 2)
            f.Add(new Finding(Severity.Hint, "Ungleichmäßige Frametimes",
                $"Die langsamsten 1 % der Frames brauchen {p99:F1} ms, der Median {median:F1} ms. Das wirkt unruhiger, als die FPS vermuten lassen.",
                "Ein FPS-Limit knapp unter dem erreichten Minimum (z. B. per Treiber oder RTSS) glättet die Darstellung.") { Category = "Ruckler" });

        // --- Darstellung
        var mode = frames.GroupBy(x => x.PresentMode).OrderByDescending(g => g.Count()).First().Key;
        if (mode.StartsWith("Composed", StringComparison.OrdinalIgnoreCase))
            f.Add(new Finding(Severity.Hint, $"Bildausgabe über den Desktop-Compositor ({mode})",
                "Windows komponiert jedes Bild noch einmal mit dem Desktop. Das kostet Latenz und manchmal Bildrate. Ursache sind meist Overlays (Discord, Overwolf, Aufnahme-Tools) oder ein Fenstermodus.",
                "Overlays für Cyberpunk deaktivieren; ‚Randloses Fenster‘ sollte unter Windows 11 normalerweise ‚Hardware: Independent Flip‘ erreichen.") { Category = "Darstellung" });

        if (gpuShare >= 0.6 && settings?.Reflex == "Disabled" && rec.GpuName?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) == true)
            f.Add(new Finding(Severity.Hint, "NVIDIA Reflex ist aus",
                "Im GPU-Limit stauen sich Bilder in der Warteschlange – das erhöht die Eingabeverzögerung spürbar.",
                "Reflex auf „Enabled“ stellen (kostet praktisch keine FPS).") { Category = "Darstellung" });

        AddSensorFindings(f, sensors, settings, gpuShare, rec.ProcessName);
        return report;
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
                if (settings?.TextureQuality == "High") recs.Add("Texturqualität: Medium");
                if (settings?.RayTracing == true) recs.Add("Raytracing reduziert den VRAM-Bedarf deutlich, wenn es aus ist");
                recs.Add("Hochauflösende Textur-Mods (4K/8K) entfernen");
                recs.Add("Browser mit Hardwarebeschleunigung schließen – belegt ebenfalls VRAM");
                f.Add(new Finding(Severity.Warning, $"Grafikspeicher am Limit ({maxUsed / 1024:F1} von {totalMb / 1024:F1} GB)",
                    $"In {full * 100:F0} % der Messungen war der VRAM zu über 95 % belegt. Dann lagert der Treiber in den langsameren Arbeitsspeicher aus – Ruckler und Nachladen sind die Folge.",
                    "So wird VRAM frei:", recs) { Category = "Speicher" });
            }
            else
            {
                f.Add(new Finding(Severity.Info, $"VRAM ausreichend (max. {maxUsed / 1024:F1} von {totalMb / 1024:F1} GB)",
                    "Der Grafikspeicher war nicht der limitierende Faktor.", null) { Category = "Speicher" });
            }
        }

        // RAM / Auslagerung
        double maxRam = ingame.Max(s => s.RamUsedGb / s.RamTotalGb);
        double maxCommit = ingame.Max(s => s.CommitUsedGb / s.CommitLimitGb);
        if (maxCommit >= 0.9)
            f.Add(new Finding(Severity.Critical, $"Virtueller Speicher fast erschöpft ({maxCommit * 100:F0} %)",
                "Arbeitsspeicher plus Auslagerungsdatei sind fast voll. Läuft er über, stürzt das Spiel ohne klare Fehlermeldung ab.",
                "Auslagerungsdatei auf „automatisch“ stellen, Platz auf C: schaffen, Hintergrundprogramme schließen.") { Category = "Speicher" });
        else if (maxRam >= 0.9)
            f.Add(new Finding(Severity.Warning, $"Arbeitsspeicher fast voll ({maxRam * 100:F0} %)",
                $"Spitzenbelegung {ingame.Max(s => s.RamUsedGb):F1} von {ingame[0].RamTotalGb:F0} GB. Windows beginnt auszulagern – das verursacht Ruckler.",
                "Browser, Discord & Co. schließen; bei sehr vielen Mods sind 32 GB RAM sinnvoll.") { Category = "Speicher" });

        var ws = ingame.Where(s => s.GameWorkingSetGb.HasValue).Select(s => s.GameWorkingSetGb!.Value).DefaultIfEmpty().Max();
        if (ws > 0)
            f.Add(new Finding(Severity.Info, $"{appName} belegt bis zu {ws:F1} GB RAM",
                $"Gesamtbelegung des Systems bis {ingame.Max(s => s.RamUsedGb):F1} von {ingame[0].RamTotalGb:F0} GB, virtueller Speicher bis {maxCommit * 100:F0} %.", null)
            { Category = "Speicher" });

        // GPU-Temperatur & Drosselung
        var gpu = ingame.Where(s => s.GpuTempC.HasValue).ToList();
        if (gpu.Count > 0)
        {
            double maxTemp = gpu.Max(s => s.GpuTempC!.Value);
            double thermal = gpu.Count(s => (s.GpuThrottleReasons & (NvidiaGpu.ThrottleSwThermal | NvidiaGpu.ThrottleHwThermal | NvidiaGpu.ThrottleHwSlowdown | NvidiaGpu.ThrottleHwPowerBrake)) != 0) / (double)gpu.Count;
            double power = gpu.Count(s => (s.GpuThrottleReasons & NvidiaGpu.ThrottleSwPowerCap) != 0) / (double)gpu.Count;

            if (thermal >= 0.05)
                f.Add(new Finding(Severity.Warning, $"Grafikkarte drosselt wegen Temperatur ({thermal * 100:F0} % der Zeit)",
                    $"Die GPU hat ihren Takt wegen Hitze oder einer Hardware-Bremse gesenkt (max. {maxTemp:F0} °C). Das kostet FPS und erzeugt Schwankungen.",
                    "Gehäuselüftung und Staub prüfen, Lüfterkurve anpassen; ein leichtes Undervolting senkt die Temperatur oft deutlich.") { Category = "Hardware" });
            else if (maxTemp >= 85)
                f.Add(new Finding(Severity.Hint, $"GPU wird warm ({maxTemp:F0} °C)",
                    "Noch keine Drosselung, aber nahe am Limit.", "Gehäuselüftung prüfen.") { Category = "Hardware" });
            else
                f.Add(new Finding(Severity.Info, $"GPU-Temperatur in Ordnung (max. {maxTemp:F0} °C)", "Keine thermische Drosselung.", null) { Category = "Hardware" });

            if (power >= 0.3 && gpuShare >= 0.6)
                f.Add(new Finding(Severity.Info, $"GPU läuft am Powerlimit ({power * 100:F0} % der Zeit)",
                    $"Die Karte nutzt ihr Leistungsbudget ({gpu.Where(s => s.GpuPowerLimitW.HasValue).Select(s => s.GpuPowerLimitW!.Value).DefaultIfEmpty().Max():F0} W) voll aus. Unter Volllast ist das normal und kein Fehler.", null)
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
