using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using GameDoctor.Core;
using GameDoctor.Core.Analysis;

namespace GameDoctor.App;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record StatTile(string Value, string Label, string? Sub = null);

public sealed record DetailRow(string Key, string Value);

public sealed class CrashViewModel
{
    public CrashViewModel(CrashCase c)
    {
        Case = c;
        var r = c.Report;
        TimeText = r.Time.ToString("dd.MM.yyyy  HH:mm");
        Label = c.FaultLabel;
        KindLabel = ModuleClassifier.KindLabel(c.FaultKind);
        TopSeverity = c.Findings.Count > 0 ? c.Findings.Max(f => f.Severity) : Severity.Info;
        Badge = SeverityBrushConverter.For(TopSeverity);
        Uptime = r.UptimeSeconds is int u ? TimeSpan.FromSeconds(u).ToString(@"h\:mm\:ss") : "?";
        Findings = c.Findings;
        SuspectModules = c.SuspectModules.Select(s => $"{s.Name}  ·  {ModuleClassifier.KindLabel(s.Kind)}  ·  {s.Hits}× auf dem Stack").ToList();

        Details =
        [
            new("Absturzstelle", c.Signature),
            new("Fehler", r.Expression ?? r.ErrorReason ?? "?"),
            new("Laufzeit bis Absturz", Uptime),
            new("Ladezustand", r.LoadingStage ?? "?"),
            new("VRAM", r.VramUsedMb is int used && r.VramTotalMb is int total ? $"{used:N0} / {total:N0} MB ({100.0 * used / total:F0} %)" : "?"),
            new("Grafikkarte", $"{r.GpuName ?? "?"}  ·  Treiber {r.GpuDriver ?? "?"}"),
            new("Raytracing", r.RayTracing switch { true => "an", false => "aus", _ => "?" }),
            new("Spielversion", $"Patch {r.Patch ?? "?"}  ·  {r.InternalVersion ?? "?"}"),
        ];
    }

    public CrashCase Case { get; }
    public string TimeText { get; }
    public string Label { get; }
    public string KindLabel { get; }
    public Severity TopSeverity { get; }
    public Brush Badge { get; }
    public string Uptime { get; }
    public IReadOnlyList<DetailRow> Details { get; }
    public IReadOnlyList<Finding> Findings { get; }
    public IReadOnlyList<string> SuspectModules { get; }
    public string Folder => Case.Report.Folder;
}

/// <summary>Zustand des Performance-Tabs.</summary>
public sealed class PerformanceViewModel : ObservableObject
{
    private bool _isRecording;
    private bool _presentMonAvailable;
    private bool _hasReport;
    private string _hint = "";
    private string _settingsSummary = "";
    private IReadOnlyList<(double Sec, double Ms)>? _series;
    private IReadOnlyList<(double Sec, double Ms)>? _stutters;
    private double _gpuShare, _cpuShare, _capShare;

    public bool IsRecording { get => _isRecording; set { if (Set(ref _isRecording, value)) Raise(nameof(RecordButtonText)); } }
    public string RecordButtonText => IsRecording ? "■  Aufnahme beenden" : "●  Aufnahme starten";
    public bool PresentMonAvailable { get => _presentMonAvailable; set { if (Set(ref _presentMonAvailable, value)) Raise(nameof(PresentMonMissing)); } }
    public bool PresentMonMissing => !_presentMonAvailable;
    public bool HasReport { get => _hasReport; set => Set(ref _hasReport, value); }

    // Ingame-Overlay
    private bool _overlayVisible;
    private OverlayCorner _overlayCorner;
    public bool OverlayVisible { get => _overlayVisible; set { if (Set(ref _overlayVisible, value)) Raise(nameof(OverlayButtonText)); } }
    public string OverlayButtonText => OverlayVisible ? "Overlay ausblenden" : "Overlay einblenden";
    public string OverlayHint => $"Liegt über dem Spiel (randloses Fenster nötig) und meldet Probleme live. Ein/aus im Spiel: {OverlayController.HotkeyText}";
    public OverlayCorner OverlayCorner
    {
        get => _overlayCorner;
        set
        {
            if (!Set(ref _overlayCorner, value)) return;
            Raise(nameof(CornerTopLeft)); Raise(nameof(CornerTopRight)); Raise(nameof(CornerBottomLeft)); Raise(nameof(CornerBottomRight));
        }
    }
    public bool CornerTopLeft => OverlayCorner == OverlayCorner.TopLeft;
    public bool CornerTopRight => OverlayCorner == OverlayCorner.TopRight;
    public bool CornerBottomLeft => OverlayCorner == OverlayCorner.BottomLeft;
    public bool CornerBottomRight => OverlayCorner == OverlayCorner.BottomRight;
    public string Hint { get => _hint; set => Set(ref _hint, value); }
    public string SettingsSummary { get => _settingsSummary; set => Set(ref _settingsSummary, value); }
    public IReadOnlyList<(double Sec, double Ms)>? Series { get => _series; set => Set(ref _series, value); }
    public IReadOnlyList<(double Sec, double Ms)>? Stutters { get => _stutters; set => Set(ref _stutters, value); }

    // Anteile für den Flaschenhals-Balken (als Sternbreiten)
    public double GpuShare { get => _gpuShare; set { Set(ref _gpuShare, value); Raise(nameof(ShareText)); } }
    public double CpuShare { get => _cpuShare; set { Set(ref _cpuShare, value); Raise(nameof(ShareText)); } }
    public double CapShare { get => _capShare; set { Set(ref _capShare, value); Raise(nameof(ShareText)); } }
    public string ShareText => $"GPU-Limit {GpuShare * 100:F0} %   ·   CPU-Limit {CpuShare * 100:F0} %   ·   begrenzt (FPS-Limit/VSync) {CapShare * 100:F0} %";

    public ObservableCollection<StatTile> LiveTiles { get; } = [];
    public ObservableCollection<StatTile> ResultTiles { get; } = [];
    public ObservableCollection<Finding> Findings { get; } = [];

    public void ShowLive(GameDoctor.Core.Performance.LiveStats s)
    {
        LiveTiles.Clear();
        LiveTiles.Add(new StatTile(s.Fps?.ToString("F0") ?? "–", "FPS", s.FrameTimeMs is double ft ? $"{ft:F1} ms" : null));
        LiveTiles.Add(new StatTile(s.OnePercentLowFps?.ToString("F0") ?? "–", "1 % Low (10 s)"));
        LiveTiles.Add(new StatTile($"{s.CpuMaxCorePct:F0} %", "CPU stärkster Kern", $"gesamt {s.CpuTotalPct:F0} %"));
        LiveTiles.Add(new StatTile(s.GpuUtilPct is double g ? $"{g:F0} %" : "–", "GPU-Last", s.GpuTempC is double t ? $"{t:F0} °C" : null));
        LiveTiles.Add(new StatTile(s.VramUsedMb is double u ? $"{u / 1024:F1} GB" : "–", "VRAM", s.VramTotalMb is double tot ? $"von {tot / 1024:F1} GB" : null));
        LiveTiles.Add(new StatTile(s.Bottleneck ?? "–", "aktuell", $"{s.Elapsed:mm\\:ss} · {s.FrameCount:N0} Frames"));
        Hint = s.GameRunning
            ? (s.FrameCount == 0 ? "Cyberpunk läuft – warte auf die ersten Frames …" : "Aufnahme läuft. Spiel ganz normal – am aussagekräftigsten sind 2–5 Minuten in der Stadt oder beim Fahren.")
            : "Warte auf Cyberpunk 2077 … Starte das Spiel, die Aufnahme beginnt automatisch.";
    }

    public GameDoctor.Core.Performance.PerformanceReport? LastReport { get; private set; }

    public void ShowReport(GameDoctor.Core.Performance.PerformanceReport r)
    {
        LastReport = r;
        ResultTiles.Clear();
        ResultTiles.Add(new StatTile(r.FrameCount > 0 ? r.AvgFps.ToString("F0") : "–", "Ø FPS"));
        ResultTiles.Add(new StatTile(r.FrameCount > 0 ? r.OnePercentLowFps.ToString("F0") : "–", "1 % Low"));
        ResultTiles.Add(new StatTile(r.FrameCount > 0 ? r.PointOnePercentLowFps.ToString("F0") : "–", "0,1 % Low"));
        ResultTiles.Add(new StatTile(r.FrameCount > 0 ? r.StuttersPerMinute.ToString("F1") : "–", "Ruckler pro Minute", $"{r.StutterCount} insgesamt"));
        GpuShare = r.GpuLimitedShare;
        CpuShare = r.CpuLimitedShare;
        CapShare = r.CappedShare;
        Series = r.FrameTimeSeries;
        Stutters = r.Stutters;
        SettingsSummary = r.SettingsSummary is null ? "" : "Spieleinstellungen: " + r.SettingsSummary;
        Findings.Clear();
        foreach (var f in r.Findings.OrderByDescending(f => f.Severity)) Findings.Add(f);
        HasReport = true;
    }
}

/// <summary>Eine Zeile der Ansicht „Datenquellen“.</summary>
public sealed class DataSourceViewModel(GameDoctor.Core.Platform.DataSource d)
{
    public string Name => d.Name;
    public string Detail => d.Detail;
    public string? Hint => d.Hint;
    public string Icon => d.Status switch
    {
        GameDoctor.Core.Platform.DataSourceStatus.Ok => "✔",
        GameDoctor.Core.Platform.DataSourceStatus.Partial => "◐",
        GameDoctor.Core.Platform.DataSourceStatus.Missing => "✘",
        _ => "ℹ",
    };
    public Brush Brush => d.Status switch
    {
        GameDoctor.Core.Platform.DataSourceStatus.Ok => SeverityBrushConverter.Good,
        GameDoctor.Core.Platform.DataSourceStatus.Partial => SeverityBrushConverter.For(Severity.Warning),
        GameDoctor.Core.Platform.DataSourceStatus.Missing => SeverityBrushConverter.For(Severity.Critical),
        _ => SeverityBrushConverter.For(Severity.Info),
    };
}

/// <summary>Ein Lösungsversuch samt automatischer Bewertung – für den Tab „Lösungen“.</summary>
public sealed class FixAttemptViewModel
{
    public FixAttemptViewModel(GameDoctor.Core.Community.FixAttempt a, GameDoctor.Core.Community.FixEvaluation ev,
        GameDoctor.Core.Community.ModInventory? inventory = null, GameDoctor.Core.Games.Cyberpunk.CyberpunkInstall? install = null)
    {
        Attempt = a;
        Evaluation = ev;

        // Solange die Mod noch aktiv ist: Anleitung direkt in der Karte anzeigen
        if (ev.Status == GameDoctor.Core.Community.FixStatus.ModStillInstalled && inventory is not null)
            Instructions = FixAttemptWindow.Numbered(GameDoctor.Core.Community.FixInstructions.For(
                a.Action, a.TargetLabel, a.NexusModId, a.TargetFiles, inventory, install));
        ConfirmText = ev.ModConfirmedDisabled && ev.Status == GameDoctor.Core.Community.FixStatus.Waiting
            ? "✔ Mod ist deaktiviert – du kannst ganz normal spielen. GameDoctor wertet bei jeder Analyse aus."
            : null;
        var verb = a.Action switch
        {
            GameDoctor.Core.Community.FixAction.DisableMod => "deaktiviert",
            GameDoctor.Core.Community.FixAction.UpdateMod => "aktualisiert",
            GameDoctor.Core.Community.FixAction.ChangeSetting => "geändert",
            _ => "ausprobiert",
        };
        Title = a.Action == GameDoctor.Core.Community.FixAction.ChangeSetting ? a.TargetLabel : $"„{a.TargetLabel}“ {verb}";
        Subtitle = $"seit {a.StartedAt:dd.MM.yyyy HH:mm} · Absturzstelle {string.Join(", ", a.Signatures)}";
        Headline = ev.Headline;
        Detail = ev.Detail;
        Progress = Math.Clamp(ev.Progress, 0, 1) * 100;
        ShowProgress = ev.Status == GameDoctor.Core.Community.FixStatus.Waiting;
        StatusBrush = ev.Status switch
        {
            GameDoctor.Core.Community.FixStatus.LikelyHelped => SeverityBrushConverter.Good,
            GameDoctor.Core.Community.FixStatus.NotHelped => SeverityBrushConverter.For(Severity.Critical),
            GameDoctor.Core.Community.FixStatus.ModStillInstalled => SeverityBrushConverter.For(Severity.Warning),
            _ => SeverityBrushConverter.For(Severity.Info),
        };
        VerdictText = a.Verdict switch
        {
            GameDoctor.Core.Community.UserVerdict.Helped => $"Deine Einschätzung: hat geholfen ({a.VerdictAt:dd.MM.})",
            GameDoctor.Core.Community.UserVerdict.NotHelped => $"Deine Einschätzung: hat nicht geholfen ({a.VerdictAt:dd.MM.})",
            _ => null,
        };
        if (a.Verdict == GameDoctor.Core.Community.UserVerdict.Helped && ev.Status == GameDoctor.Core.Community.FixStatus.NotHelped)
            VerdictText += " – die Absturzdaten sprechen allerdings dagegen.";
        Note = a.Note;

        var outcome = ev.ShareableOutcome(a);
        bool publicTarget = a.NexusModId is not null || a.ModuleFile is not null;
        ShareText = !publicTarget
            ? "Wird nicht mit der Community geteilt (keine öffentliche Mod)."
            : outcome is null
                ? "Wird anonym geteilt, sobald das Ergebnis feststeht."
                : $"Anonym teilbar als „{(outcome == "Helped" ? "hat geholfen" : "hat nicht geholfen")}“.";
    }

    public GameDoctor.Core.Community.FixAttempt Attempt { get; }
    public GameDoctor.Core.Community.FixEvaluation Evaluation { get; }
    public Guid Id => Attempt.Id;
    public string Title { get; }
    public string Subtitle { get; }
    public string Headline { get; }
    public string Detail { get; }
    public double Progress { get; }
    public bool ShowProgress { get; }
    public Brush StatusBrush { get; }
    public string? VerdictText { get; }
    public string? Note { get; }
    public string ShareText { get; }
    public IReadOnlyList<string>? Instructions { get; }
    public string? ConfirmText { get; }
}

public sealed class MainViewModel : ObservableObject
{
    private bool _hasFixes;

    public ObservableCollection<FixAttemptViewModel> Fixes { get; } = [];
    public bool HasFixes { get => _hasFixes; set { if (Set(ref _hasFixes, value)) Raise(nameof(NoFixes)); } }
    public bool NoFixes => !_hasFixes;

    public void SetFixes(IEnumerable<FixAttemptViewModel> fixes)
    {
        Fixes.Clear();
        foreach (var f in fixes) Fixes.Add(f);
        HasFixes = Fixes.Count > 0;
    }

    public PerformanceViewModel Perf { get; } = new();

    private bool _isBusy;
    private string _status = "Bereit";
    private string _gameInfo = "Noch keine Analyse";
    private string _frameworkInfo = "";
    private int _days = 30;
    private CrashViewModel? _selectedCrash;
    private bool _hasResult;

    public AnalysisResult? Result { get; private set; }

    public bool IsBusy { get => _isBusy; set { if (Set(ref _isBusy, value)) Raise(nameof(IsIdle)); } }
    public bool IsIdle => !_isBusy;
    public string Status { get => _status; set => Set(ref _status, value); }
    public string GameInfo { get => _gameInfo; set => Set(ref _gameInfo, value); }
    public string FrameworkInfo { get => _frameworkInfo; set => Set(ref _frameworkInfo, value); }
    public int Days { get => _days; set => Set(ref _days, value); }
    public bool HasResult { get => _hasResult; set => Set(ref _hasResult, value); }
    public CrashViewModel? SelectedCrash { get => _selectedCrash; set => Set(ref _selectedCrash, value); }

    public ObservableCollection<StatTile> Stats { get; } = [];
    public ObservableCollection<Finding> TopFindings { get; } = [];
    public ObservableCollection<Finding> PatternFindings { get; } = [];
    public ObservableCollection<Finding> EnvironmentFindings { get; } = [];
    public ObservableCollection<CrashViewModel> Crashes { get; } = [];
    public ObservableCollection<DataSourceViewModel> DataSources { get; } = [];

    private bool _gameMissing;
    public bool GameMissing { get => _gameMissing; set => Set(ref _gameMissing, value); }

    public void Load(AnalysisResult r)
    {
        Result = r;
        GameMissing = r.Install is null;
        DataSources.Clear();
        foreach (var d in r.DataSources) DataSources.Add(new DataSourceViewModel(d));
        GameInfo = r.Install is null
            ? "Cyberpunk 2077 nicht gefunden"
            : $"Cyberpunk 2077  ·  Version {r.GameVersion ?? "?"}  ·  {r.Install.Store}  ·  {r.Install.Root}";
        FrameworkInfo = r.ModLogs is { } ml
            ? $"RED4ext {ml.Red4ExtVersion ?? "–"}  ·  CET {ml.CetVersion ?? "–"}  ·  {ml.ArchiveModCount} Archive-Mods  ·  {ml.Red4ExtPluginCount} RED4ext-Plugins  ·  {ml.RedscriptModCount} redscript-Mods  ·  {ml.CetModCount} CET-Mods"
            : "";

        Stats.Clear();
        var crashes = r.Crashes;
        Stats.Add(new StatTile(crashes.Count.ToString(), $"Abstürze ({Days} Tage)"));
        Stats.Add(new StatTile(crashes.Count(c => c.Report.Time >= DateTime.Now.AddDays(-7)).ToString(), "letzte 7 Tage"));

        var topSig = crashes.Where(c => c.Signature != "?").GroupBy(c => c.Signature).OrderByDescending(g => g.Count()).FirstOrDefault();
        Stats.Add(topSig is not null
            ? new StatTile($"{topSig.Count()}×", "häufigste Absturzstelle", topSig.Key)
            : new StatTile("–", "häufigste Absturzstelle"));

        var topMod = crashes
            .SelectMany(c => c.SuspectModules.Where(s => s.Kind is ModuleKind.Mod or ModuleKind.Overlay).Select(s => s.Name).Distinct())
            .GroupBy(n => n).OrderByDescending(g => g.Count()).FirstOrDefault();
        Stats.Add(topMod is not null
            ? new StatTile($"{topMod.Count()}×", "verdächtigste Mod", topMod.Key)
            : new StatTile("–", "verdächtigste Mod", "keine Mod auffällig"));

        TopFindings.Clear();
        foreach (var f in crashes.Take(1).SelectMany(c => c.Findings)
                     .Concat(r.PatternFindings).Concat(r.EnvironmentFindings)
                     .Where(f => f.Severity >= Severity.Warning)
                     .OrderByDescending(f => f.Severity).Take(6))
            TopFindings.Add(f);

        PatternFindings.Clear();
        foreach (var f in r.PatternFindings) PatternFindings.Add(f);

        EnvironmentFindings.Clear();
        foreach (var f in r.EnvironmentFindings.OrderByDescending(f => f.Severity)) EnvironmentFindings.Add(f);

        Crashes.Clear();
        foreach (var c in crashes) Crashes.Add(new CrashViewModel(c));
        SelectedCrash = Crashes.FirstOrDefault();

        HasResult = true;
    }
}
