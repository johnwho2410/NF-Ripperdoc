using GameDoctor.Core.Localization;
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
        TimeText = r.Time.ToString("g");
        Label = c.FaultLabel;
        KindLabel = ModuleClassifier.KindLabel(c.FaultKind);
        TopSeverity = c.Findings.Count > 0 ? c.Findings.Max(f => f.Severity) : Severity.Info;
        Badge = SeverityBrushConverter.For(TopSeverity);
        Uptime = r.UptimeSeconds is int u ? TimeSpan.FromSeconds(u).ToString(@"h\:mm\:ss") : "?";
        Findings = c.Findings;
        SuspectModules = c.SuspectModules.Select(s => L.F("{0}  ·  {1}  ·  {2}× auf dem Stack", s.Name, ModuleClassifier.KindLabel(s.Kind), s.Hits)).ToList();

        Details =
        [
            new(L.T("Absturzstelle"), c.Signature),
            new(L.T("Fehler"), r.Expression ?? r.ErrorReason ?? "?"),
            new(L.T("Laufzeit bis Absturz"), Uptime),
            new(L.T("Ladezustand"), r.LoadingStage ?? "?"),
            new("VRAM", r.VramUsedMb is int used && r.VramTotalMb is int total ? $"{used:N0} / {total:N0} MB ({100.0 * used / total:F0} %)" : "?"),
            new(L.T("Grafikkarte"), L.F("{0}  ·  Treiber {1}", r.GpuName ?? "?", r.GpuDriver ?? "?")),
            new(L.T("Raytracing"), r.RayTracing switch { true => L.T("an"), false => L.T("aus"), _ => "?" }),
            new(L.T("Spielversion"), $"Patch {r.Patch ?? "?"}  ·  {r.InternalVersion ?? "?"}"),
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

/// <summary>Zeile der Mod-Tabelle (Mods &amp; System). „Verdächtig“ nur, wenn die Mod in Absturz-Stacks vorkam.</summary>
public sealed record ModRow(string Name, string Version, string NexusId, string Status, bool Suspect);

/// <summary>Zeile der Liste „Top-Problemsignaturen“ in der Übersicht.</summary>
public sealed record SignatureRow(int Rank, string Signature, string CountText);

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

    /// <summary>Nach einem Sprachwechsel die berechneten Texte neu abfragen.</summary>
    public void RefreshLanguage()
    {
        Raise(nameof(RecordButtonText)); Raise(nameof(OverlayButtonText)); Raise(nameof(OverlayHint));
    }

    public bool IsRecording { get => _isRecording; set { if (Set(ref _isRecording, value)) Raise(nameof(RecordButtonText)); } }
    public string RecordButtonText => IsRecording ? "■  " + L.T("Aufnahme beenden") : "●  " + L.T("Aufnahme starten");
    public bool PresentMonAvailable { get => _presentMonAvailable; set { if (Set(ref _presentMonAvailable, value)) Raise(nameof(PresentMonMissing)); } }
    public bool PresentMonMissing => !_presentMonAvailable;
    public bool HasReport { get => _hasReport; set => Set(ref _hasReport, value); }

    // Ingame-Overlay
    private bool _overlayVisible;
    private OverlayCorner _overlayCorner;
    public bool OverlayVisible { get => _overlayVisible; set { if (Set(ref _overlayVisible, value)) Raise(nameof(OverlayButtonText)); } }
    public string OverlayButtonText => OverlayVisible ? L.T("Overlay ausblenden") : L.T("Overlay einblenden");
    public string OverlayHint => L.F("Liegt über dem Spiel (randloses Fenster nötig) und meldet Probleme live. Ein/aus im Spiel: {0}", OverlayController.HotkeyText);
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
    public string ShareText => L.F("GPU-Limit {0:F0} %   ·   CPU-Limit {1:F0} %   ·   begrenzt (FPS-Limit/VSync) {2:F0} %", GpuShare * 100, CpuShare * 100, CapShare * 100);

    public ObservableCollection<StatTile> LiveTiles { get; } = [];
    public ObservableCollection<StatTile> ResultTiles { get; } = [];
    public ObservableCollection<Finding> Findings { get; } = [];

    public void ShowLive(GameDoctor.Core.Performance.LiveStats s)
    {
        LiveTiles.Clear();
        LiveTiles.Add(new StatTile(s.Fps?.ToString("F0") ?? "–", "FPS", s.FrameTimeMs is double ft ? $"{ft:F1} ms" : null));
        LiveTiles.Add(new StatTile(s.OnePercentLowFps?.ToString("F0") ?? "–", L.T("1 % Low (10 s)")));
        LiveTiles.Add(new StatTile($"{s.CpuMaxCorePct:F0} %", L.T("CPU stärkster Kern"), L.F("gesamt {0:F0} %", s.CpuTotalPct)));
        LiveTiles.Add(new StatTile(s.GpuUtilPct is double g ? $"{g:F0} %" : "–", L.T("GPU-Last"), s.GpuTempC is double t ? $"{t:F0} °C" : null));
        LiveTiles.Add(new StatTile(s.VramUsedMb is double u ? $"{u / 1024:F1} GB" : "–", "VRAM", s.VramTotalMb is double tot ? L.F("von {0:F1} GB", tot / 1024) : null));
        LiveTiles.Add(new StatTile(s.Bottleneck ?? "–", L.T("aktuell"), L.F("{0} · {1:N0} Frames", s.Elapsed.ToString(@"mm\:ss"), s.FrameCount)));
        Hint = s.GameRunning
            ? (s.FrameCount == 0 ? L.T("Cyberpunk läuft – warte auf die ersten Frames …") : L.T("Aufnahme läuft. Spiel ganz normal – am aussagekräftigsten sind 2–5 Minuten in der Stadt oder beim Fahren."))
            : L.T("Warte auf Cyberpunk 2077 … Starte das Spiel, die Aufnahme beginnt automatisch.");
    }

    public GameDoctor.Core.Performance.PerformanceReport? LastReport { get; private set; }

    public void ShowReport(GameDoctor.Core.Performance.PerformanceReport r)
    {
        LastReport = r;
        ResultTiles.Clear();
        ResultTiles.Add(new StatTile(r.FrameCount > 0 ? r.AvgFps.ToString("F0") : "–", L.T("Ø FPS")));
        ResultTiles.Add(new StatTile(r.FrameCount > 0 ? r.OnePercentLowFps.ToString("F0") : "–", L.T("1 % Low")));
        ResultTiles.Add(new StatTile(r.FrameCount > 0 ? r.PointOnePercentLowFps.ToString("F0") : "–", L.T("0,1 % Low")));
        ResultTiles.Add(new StatTile(r.FrameCount > 0 ? r.StuttersPerMinute.ToString("F1") : "–", L.T("Ruckler pro Minute"), L.F("{0} insgesamt", r.StutterCount)));
        GpuShare = r.GpuLimitedShare;
        CpuShare = r.CpuLimitedShare;
        CapShare = r.CappedShare;
        Series = r.FrameTimeSeries;
        Stutters = r.Stutters;
        SettingsSummary = r.SettingsSummary is null ? "" : L.F("Spieleinstellungen: {0}", r.SettingsSummary);
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
            ? L.T("✔ Mod ist deaktiviert – du kannst ganz normal spielen. GameDoctor wertet bei jeder Analyse aus.")
            : null;
        var verb = a.Action switch
        {
            GameDoctor.Core.Community.FixAction.DisableMod => L.T("deaktiviert"),
            GameDoctor.Core.Community.FixAction.UpdateMod => L.T("aktualisiert"),
            GameDoctor.Core.Community.FixAction.ChangeSetting => L.T("geändert"),
            _ => L.T("ausprobiert"),
        };
        Title = a.Action == GameDoctor.Core.Community.FixAction.ChangeSetting ? a.TargetLabel : L.F("„{0}“ {1}", a.TargetLabel, verb);
        Subtitle = L.F("seit {0:g} · Absturzstelle {1}", a.StartedAt, string.Join(", ", a.Signatures));
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
            GameDoctor.Core.Community.UserVerdict.Helped => L.F("Deine Einschätzung: hat geholfen ({0:d})", a.VerdictAt),
            GameDoctor.Core.Community.UserVerdict.NotHelped => L.F("Deine Einschätzung: hat nicht geholfen ({0:d})", a.VerdictAt),
            _ => null,
        };
        if (a.Verdict == GameDoctor.Core.Community.UserVerdict.Helped && ev.Status == GameDoctor.Core.Community.FixStatus.NotHelped)
            VerdictText += " – " + L.T("die Absturzdaten sprechen allerdings dagegen.");
        Note = a.Note;

        var outcome = ev.ShareableOutcome(a);
        bool publicTarget = a.NexusModId is not null || a.ModuleFile is not null;
        ShareText = !publicTarget
            ? L.T("Wird nicht mit der Community geteilt (keine öffentliche Mod).")
            : outcome is null
                ? L.T("Wird anonym geteilt, sobald das Ergebnis feststeht.")
                : L.F("Anonym teilbar als „{0}“.", outcome == "Helped" ? L.T("hat geholfen") : L.T("hat nicht geholfen"));
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
    private string _status = L.T("Bereit");
    private string _gameInfo = L.T("Noch keine Analyse");
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
    public ObservableCollection<SignatureRow> TopSignatures { get; } = [];
    public ObservableCollection<ModRow> Mods { get; } = [];
    private bool _hasMods;
    public bool HasMods { get => _hasMods; set => Set(ref _hasMods, value); }
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
            ? L.T("Cyberpunk 2077 nicht gefunden")
            : L.F("Cyberpunk 2077  ·  Version {0}  ·  {1}  ·  {2}", r.GameVersion ?? "?", L.T(r.Install.Store), r.Install.Root);
        FrameworkInfo = r.ModLogs is { } ml
            ? $"RED4ext {ml.Red4ExtVersion ?? "–"}  ·  CET {ml.CetVersion ?? "–"}  ·  " + L.F("{0} Archive-Mods  ·  {1} RED4ext-Plugins  ·  {2} redscript-Mods  ·  {3} CET-Mods", ml.ArchiveModCount, ml.Red4ExtPluginCount, ml.RedscriptModCount, ml.CetModCount)
            : "";

        Stats.Clear();
        var crashes = r.Crashes;
        Stats.Add(new StatTile(crashes.Count.ToString(), L.F("Abstürze ({0} Tage)", Days)));
        Stats.Add(new StatTile(crashes.Count(c => c.Report.Time >= DateTime.Now.AddDays(-7)).ToString(), L.T("letzte 7 Tage")));

        var topSig = crashes.Where(c => c.Signature != "?").GroupBy(c => c.Signature).OrderByDescending(g => g.Count()).FirstOrDefault();
        Stats.Add(topSig is not null
            ? new StatTile($"{topSig.Count()}×", L.T("häufigste Absturzstelle"), topSig.Key)
            : new StatTile("–", L.T("häufigste Absturzstelle")));

        var topMod = crashes
            .SelectMany(c => c.SuspectModules.Where(s => s.Kind is ModuleKind.Mod or ModuleKind.Overlay).Select(s => s.Name).Distinct())
            .GroupBy(n => n).OrderByDescending(g => g.Count()).FirstOrDefault();
        Stats.Add(topMod is not null
            ? new StatTile($"{topMod.Count()}×", L.T("verdächtigste Mod"), topMod.Key)
            : new StatTile("–", L.T("verdächtigste Mod"), L.T("keine Mod auffällig")));

        Mods.Clear();
        if (r.ModLogs is { } logs)
        {
            if (logs.Red4ExtVersion is { } rv) Mods.Add(new ModRow("RED4ext", rv, "", L.T("Framework"), false));
            if (logs.CetVersion is { } cv) Mods.Add(new ModRow("Cyber Engine Tweaks", cv, "", L.T("Framework"), false));
        }
        static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var suspects = crashes
            .SelectMany(c => c.SuspectModules.Where(s => s.Kind is ModuleKind.Mod or ModuleKind.Overlay).Select(s => s.Name).Distinct())
            .GroupBy(n => n).Select(g => (Key: Norm(g.Key), N: g.Count())).ToList();
        foreach (var m in r.Inventory.Mods.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var key = Norm(m.DisplayName);
            var hit = key.Length >= 4 ? suspects.Where(s => s.Key.Contains(key) || key.Contains(s.Key)).Sum(s => s.N) : 0;
            Mods.Add(new ModRow(m.DisplayName, m.Version ?? "–", $"#{m.NexusId}",
                hit > 0 ? L.F("Verdächtig · {0}× in Abstürzen", hit) : L.T("Installiert"), hit > 0));
        }
        HasMods = Mods.Count > 0;

        TopSignatures.Clear();
        int rank = 0;
        foreach (var g in crashes.Where(c => c.Signature != "?").GroupBy(c => c.Signature)
                     .Select(g => (Sig: g.Key, N: g.Count())).Where(x => x.N > 1).OrderByDescending(x => x.N).Take(5))
            TopSignatures.Add(new SignatureRow(++rank, g.Sig, $"{g.N}×"));

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
