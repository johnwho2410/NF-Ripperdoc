using System.Diagnostics;
using System.Globalization;

namespace GameDoctor.Core.Performance;

/// <summary>Ein Frame laut PresentMon (alle Zeiten in Millisekunden).</summary>
public readonly record struct FrameSample(
    double TimeMs,          // seit Aufnahmestart
    double FrameTimeMs,     // Abstand zum vorherigen Present
    double CpuBusyMs,
    double CpuWaitMs,
    double GpuBusyMs,
    double GpuWaitMs,
    double? DisplayLatencyMs,
    string PresentMode,
    string SwapChain);

/// <summary>Sensorwerte, einmal pro Sekunde.</summary>
public sealed record SensorSample(
    double TimeMs,
    double CpuTotalPct,
    double CpuMaxCorePct,
    double? GpuUtilPct,
    double? VramUsedMb,
    double? VramTotalMb,
    double? GpuTempC,
    double? GpuClockMhz,
    double? GpuPowerW,
    double? GpuPowerLimitW,
    ulong GpuThrottleReasons,
    double RamUsedGb,
    double RamTotalGb,
    double CommitUsedGb,
    double CommitLimitGb,
    double? GameWorkingSetGb,
    bool GameRunning);

public sealed class PerformanceRecording
{
    public DateTime StartedAt { get; init; }
    public TimeSpan Duration { get; set; }
    public string ProcessName { get; init; } = "";
    public string? GpuName { get; set; }
    public bool PresentMonUsed { get; set; }
    public string? PresentMonError { get; set; }
    public List<FrameSample> Frames { get; } = [];
    public List<SensorSample> Sensors { get; } = [];
}

/// <summary>Live-Werte für die Anzeige während der Aufnahme (und für das Ingame-Overlay).</summary>
public sealed record LiveStats(
    TimeSpan Elapsed, int FrameCount, double? Fps, double? FrameTimeMs, double? OnePercentLowFps,
    double CpuTotalPct, double CpuMaxCorePct, double? GpuUtilPct, double? VramUsedMb, double? VramTotalMb,
    double? GpuTempC, bool GameRunning, string? Bottleneck)
{
    public double RamUsedGb { get; init; }
    public double RamTotalGb { get; init; }
    public double CommitUsedGb { get; init; }
    public double CommitLimitGb { get; init; }
    /// <summary>Frametimes (ms) der letzten 10 Sekunden, für die Mini-Kurve auf höchstens 240 Punkte verdichtet.</summary>
    public IReadOnlyList<double> RecentFrameTimes { get; init; } = [];
    /// <summary>Ruckler in den letzten 10 Sekunden.</summary>
    public int StuttersLast10s { get; init; }
}

/// <summary>
/// Zeichnet Frame-Daten (PresentMon) und Systemsensoren parallel auf.
/// PresentMon wird als Kindprozess gestartet und seine CSV-Ausgabe live gelesen.
/// </summary>
public sealed class PerformanceRecorder : IDisposable
{
    private const string SessionName = "GameDoctor";

    private readonly PerformanceRecording _rec;
    private readonly string _processName;
    private readonly object _lock = new();
    private readonly Stopwatch _clock = new();
    private Process? _presentMon;
    private Dictionary<string, int>? _columns;
    private CancellationTokenSource? _cts;
    private Task? _sensorTask;
    private NvidiaGpu? _gpu;

    public event Action<LiveStats>? LiveUpdated;

    public PerformanceRecorder(string processName = "Cyberpunk2077.exe")
    {
        _processName = processName;
        _rec = new PerformanceRecording { StartedAt = DateTime.Now, ProcessName = processName };
    }

    public bool IsRunning => _cts is not null;

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _clock.Restart();

        _gpu = NvidiaGpu.TryOpen();
        _rec.GpuName = _gpu?.Name;

        StartPresentMon();
        _sensorTask = Task.Run(() => SensorLoop(_cts.Token));
    }

    public PerformanceRecording Stop()
    {
        if (_cts is null) return _rec;
        _cts.Cancel();
        try { _sensorTask?.Wait(3000); } catch { /* abgebrochen */ }
        StopPresentMon();
        _rec.Duration = _clock.Elapsed;
        _clock.Stop();
        _gpu?.Dispose();
        _gpu = null;
        _cts = null;
        return _rec;
    }

    // ------------------------------------------------------------------ PresentMon
    private void StartPresentMon()
    {
        var exe = PresentMonTool.Find();
        if (exe is null)
        {
            _rec.PresentMonError = "PresentMon ist nicht installiert – es werden nur Sensoren aufgezeichnet.";
            return;
        }

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in new[]
                 {
                     "--process_name", _processName, "--output_stdout", "--no_console_stats",
                     "--stop_existing_session", "--session_name", SessionName,
                 })
            psi.ArgumentList.Add(a);

        try
        {
            _presentMon = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _presentMon.OutputDataReceived += (_, e) => { if (e.Data is not null) OnCsvLine(e.Data); };
            _presentMon.ErrorDataReceived += (_, e) =>
            {
                // Warnungen (fehlende Adminrechte o. ä.) sind normal; echte Fehler merken
                if (e.Data is { } line && line.StartsWith("error", StringComparison.OrdinalIgnoreCase))
                    _rec.PresentMonError = line;
            };
            _presentMon.Start();
            _presentMon.BeginOutputReadLine();
            _presentMon.BeginErrorReadLine();
            _rec.PresentMonUsed = true;
        }
        catch (Exception ex)
        {
            _rec.PresentMonError = "PresentMon konnte nicht gestartet werden: " + ex.Message;
        }
    }

    private void StopPresentMon()
    {
        if (_presentMon is null) return;
        try
        {
            if (!_presentMon.HasExited) _presentMon.Kill();
            _presentMon.WaitForExit(2000);
        }
        catch { /* bereits beendet */ }

        // Die ETW-Sitzung überlebt ein Kill – sauber beenden
        try
        {
            var exe = PresentMonTool.Find();
            if (exe is not null)
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                psi.ArgumentList.Add("--terminate_existing_session");
                psi.ArgumentList.Add("--session_name");
                psi.ArgumentList.Add(SessionName);
                using var p = Process.Start(psi);
                p?.WaitForExit(3000);
            }
        }
        catch { /* nicht kritisch */ }

        _presentMon.Dispose();
        _presentMon = null;
    }

    private void OnCsvLine(string line)
    {
        var parts = line.Split(',');
        if (_columns is null)
        {
            if (!line.StartsWith("Application,", StringComparison.Ordinal)) return;
            _columns = parts.Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i);
            return;
        }

        double? Num(string col)
        {
            if (!_columns.TryGetValue(col, out var i) || i >= parts.Length) return null;
            return double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        }
        string Str(string col) => _columns.TryGetValue(col, out var i) && i < parts.Length ? parts[i] : "";

        var ft = Num("MsBetweenPresents");
        if (ft is null or <= 0) return;

        var frame = new FrameSample(
            Num("TimeInMs") ?? _clock.Elapsed.TotalMilliseconds,
            ft.Value,
            Num("MsCPUBusy") ?? 0,
            Num("MsCPUWait") ?? 0,
            Num("MsGPUBusy") ?? 0,
            Num("MsGPUWait") ?? 0,
            Num("MsUntilDisplayed"),
            Str("PresentMode"),
            Str("SwapChainAddress"));

        lock (_lock) _rec.Frames.Add(frame);
    }

    // ------------------------------------------------------------------ Sensoren
    private async Task SensorLoop(CancellationToken ct)
    {
        var cpu = new CpuSampler();
        cpu.Sample(); // Basiswert
        var procName = Path.GetFileNameWithoutExtension(_processName);

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct); } catch (TaskCanceledException) { break; }

            var cores = cpu.Sample();
            var gpu = _gpu?.Read();
            var mem = MemoryInfo.Read();

            double? ws = null;
            bool running = false;
            foreach (var p in Process.GetProcessesByName(procName))
            {
                using (p)
                {
                    running = true;
                    try { ws = p.WorkingSet64 / 1024d / 1024 / 1024; } catch { /* Zugriff verweigert */ }
                }
            }

            var s = new SensorSample(
                _clock.Elapsed.TotalMilliseconds,
                cores.Length > 0 ? cores.Average() : 0,
                cores.Length > 0 ? cores.Max() : 0,
                gpu?.UtilPct, gpu?.VramUsedMb, gpu?.VramTotalMb, gpu?.TempC, gpu?.ClockMhz,
                gpu?.PowerW, gpu?.PowerLimitW, gpu?.ThrottleReasons ?? 0,
                mem.UsedPhysGb, mem.TotalPhysGb, mem.CommitUsedGb, mem.CommitLimitGb, ws, running);

            LiveStats live;
            lock (_lock)
            {
                _rec.Sensors.Add(s);
                live = BuildLive(s);
            }
            LiveUpdated?.Invoke(live);
        }
    }

    private LiveStats BuildLive(SensorSample s)
    {
        var frames = PerformanceAnalyzer.MainSwapChain(_rec.Frames);
        double? fps = null, ft = null, low = null;
        string? bottleneck = null;
        List<double> recentFt = [];
        int stutters = 0;
        if (frames.Count > 0)
        {
            double now = frames[^1].TimeMs;
            var recent = frames.Where(f => f.TimeMs >= now - 1000).ToList();
            if (recent.Count > 0)
            {
                ft = recent.Average(f => f.FrameTimeMs);
                fps = 1000.0 / ft;
                // Sensorwerte über 5 s glätten – ein einzelner Kern-Ausreißer soll kein „CPU-Limit“ auslösen
                var lastSensors = _rec.Sensors.TakeLast(5).ToList();
                var gpuUtils = lastSensors.Where(x => x.GpuUtilPct.HasValue).Select(x => x.GpuUtilPct!.Value).ToList();
                bottleneck = PerformanceAnalyzer.ClassifyWindow(recent,
                    lastSensors.Average(x => x.CpuMaxCorePct),
                    gpuUtils.Count > 0 ? gpuUtils.Average() : null);
            }
            var last10s = frames.Where(f => f.TimeMs >= now - 10000).Select(f => f.FrameTimeMs).ToList();
            if (last10s.Count >= 20) low = 1000.0 / PerformanceAnalyzer.Percentile(last10s, 99);
            recentFt = Downsample(last10s, 240);
            if (last10s.Count >= 20)
            {
                // Gleiche Regel wie in der Auswertung: deutlich länger als der Median der Umgebung
                double median = PerformanceAnalyzer.Percentile(last10s, 50);
                stutters = last10s.Count(x => x > 2.0 * median && x > median + 12);
            }
        }
        return new LiveStats(_clock.Elapsed, frames.Count, fps, ft, low, s.CpuTotalPct, s.CpuMaxCorePct,
            s.GpuUtilPct, s.VramUsedMb, s.VramTotalMb, s.GpuTempC, s.GameRunning, bottleneck)
        {
            RamUsedGb = s.RamUsedGb,
            RamTotalGb = s.RamTotalGb,
            CommitUsedGb = s.CommitUsedGb,
            CommitLimitGb = s.CommitLimitGb,
            RecentFrameTimes = recentFt,
            StuttersLast10s = stutters,
        };
    }

    /// <summary>Verdichtet auf höchstens <paramref name="max"/> Punkte; behält je Block den Höchstwert, damit Ruckler sichtbar bleiben.</summary>
    private static List<double> Downsample(List<double> values, int max)
    {
        if (values.Count <= max) return values;
        var result = new List<double>(max);
        double step = values.Count / (double)max;
        for (int i = 0; i < max; i++)
        {
            int a = (int)(i * step), b = Math.Min(values.Count, (int)((i + 1) * step));
            double m = values[a];
            for (int j = a + 1; j < b; j++) if (values[j] > m) m = values[j];
            result.Add(m);
        }
        return result;
    }

    public void Dispose() => Stop();
}
