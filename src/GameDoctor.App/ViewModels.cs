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

public sealed class MainViewModel : ObservableObject
{
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

    public void Load(AnalysisResult r)
    {
        Result = r;
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
