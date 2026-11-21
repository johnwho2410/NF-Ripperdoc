using System.Text;
using GameDoctor.Core;
using GameDoctor.Core.Analysis;
using GameDoctor.Core.Report;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length > 0 && args[0] == "perf")
{
    await RunPerf(args.Skip(1).ToArray());
    return;
}

// KI-Prompt: gamedoctor prompt [--days 30] [--out prompt.txt]
// Community-Fingerabdrücke: gamedoctor fingerprint [--days 30] [--save]
// Zeigt exakt, was später anonym geteilt werden könnte – und was der Datenschutz-Wächter blockiert.
if (args.Length > 0 && args[0] == "fingerprint")
{
    int fDays = 30;
    bool save = args.Contains("--save");
    for (int i = 1; i < args.Length; i++)
        if (args[i] == "--days") fDays = int.Parse(args[++i]);

    string? fOut = null;
    for (int i = 1; i < args.Length; i++)
        if (args[i] == "--out") fOut = args[++i];

    var res = CrashAnalyzer.Run(new CrashAnalyzerOptions { Days = fDays });
    var secret = GameDoctor.Core.Community.FingerprintStore.GetOrCreateSecret();
    var inventory = res.Install is null ? new GameDoctor.Core.Community.ModInventory() : GameDoctor.Core.Community.ModInventory.Load(res.Install.Root);
    var builder = new GameDoctor.Core.Community.FingerprintBuilder(new ModuleClassifier(res.Install?.Root), secret, inventory);
    var pkg = GameDoctor.Core.Community.CommunityPackage.Build(res, builder, inventory, secret);

    var json = System.Text.Json.JsonSerializer.Serialize(
        new { pkg.Fingerprints, pkg.Pairs, pkg.Census }, GameDoctor.Core.Community.FingerprintBuilder.Json);
    if (fOut is not null) File.WriteAllText(fOut, json, Encoding.UTF8);

    Console.WriteLine("Stufe 1 – was anonym geteilt würde:");
    Console.WriteLine($"  Mod-Inventar: {inventory.Mods.Count} öffentliche Mods (Nexus-ID){(inventory.FromVortex ? "" : " – kein Vortex-Manifest gefunden")}, {inventory.PackagesWithoutId} ohne ID ausgelassen");
    Console.WriteLine($"  {res.Crashes.Count} Abstürze → {pkg.Fingerprints.Count} Fingerabdrücke (je Absturzstelle einer), {pkg.UnusableCrashes} nicht auswertbar");
    Console.WriteLine($"  {pkg.Pairs.Count:N0} Paar-Meldungen „Absturzstelle × installierte Mod“, {pkg.Census.Count} Verbreitungs-Meldungen");
    Console.WriteLine($"  Vom Datenschutz-Wächter blockiert: {pkg.BlockedByGuard}");
    foreach (var v in pkg.Violations.Take(10)) Console.WriteLine($"    BLOCKIERT: {v.Rule} „{v.Match}“");
    Console.WriteLine($"  Größe: {Encoding.UTF8.GetByteCount(json) / 1024.0:F0} KB{(fOut is not null ? $" → {fOut}" : " (mit --out <datei> vollständig speichern)")}");
    Console.WriteLine();
    Console.WriteLine("Beispiele (je eine Meldung, so wie sie gesendet würde):");
    var opts = new System.Text.Json.JsonSerializerOptions();
    if (pkg.Fingerprints.FirstOrDefault() is { } f1) Console.WriteLine("  " + System.Text.Json.JsonSerializer.Serialize(f1, opts));
    if (pkg.Pairs.FirstOrDefault() is { } p1) Console.WriteLine("  " + System.Text.Json.JsonSerializer.Serialize(p1, opts));
    if (pkg.Census.FirstOrDefault() is { } c1) Console.WriteLine("  " + System.Text.Json.JsonSerializer.Serialize(c1, opts));

    if (save) Console.WriteLine($"Fingerabdrücke lokal gespeichert: {GameDoctor.Core.Community.FingerprintStore.Merge(pkg.Fingerprints)} neu in {GameDoctor.Core.Community.FingerprintStore.Dir}");
    return;
}

if (args.Length > 0 && args[0] == "prompt")
{
    int pDays = 30;
    string? pOut = null;
    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--days") pDays = int.Parse(args[++i]);
        else if (args[i] == "--out") pOut = args[++i];
    }
    var res = CrashAnalyzer.Run(new CrashAnalyzerOptions { Days = pDays });
    GameDoctor.Core.Performance.PerformanceReport? perfReport = null;
    if (GameDoctor.Core.Performance.RecordingStore.Latest() is { } rf)
        perfReport = GameDoctor.Core.Performance.PerformanceAnalyzer.Analyze(
            GameDoctor.Core.Performance.RecordingStore.Load(rf), GameDoctor.Core.Games.Cyberpunk.CyberpunkSettings.TryLoad());
    var prompt = AiPromptBuilder.Build(res, new AiPromptOptions { Performance = perfReport });
    if (pOut is not null) { File.WriteAllText(pOut, prompt, Encoding.UTF8); Console.WriteLine($"Gespeichert: {pOut} ({prompt.Length:N0} Zeichen)"); }
    else Console.WriteLine(prompt);
    return;
}

string? gamePath = null;
string? outFile = null;
int days = 30;
int details = 5;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--game": gamePath = args[++i]; break;
        case "--out": outFile = args[++i]; break;
        case "--days": days = int.Parse(args[++i]); break;
        case "--details": details = int.Parse(args[++i]); break;
        case "--dump":
            DumpDetails(args[++i], gamePath);
            return;
        case "-h" or "--help":
            Console.WriteLine("""
                GameDoctor – Absturzanalyse für Cyberpunk 2077

                  gamedoctor [--game <Pfad>] [--days 30] [--details 5] [--out bericht.md]

                  --game     Spielordner (sonst automatisch über Steam/GOG/Epic)
                  --days     Abstürze der letzten N Tage auswerten
                  --details  So viele der neuesten Abstürze ausführlich beschreiben
                  --out      Bericht als Markdown-Datei speichern
                """);
            return;
    }
}

var result = CrashAnalyzer.Run(new CrashAnalyzerOptions { GamePath = gamePath, Days = days },
    msg => { Console.Write($"\r{msg,-70}"); });
Console.Write($"\r{new string(' ', 70)}\r");

// ---------- Kurzausgabe in der Konsole ----------
Console.WriteLine($"Cyberpunk 2077 {result.GameVersion ?? "?"}  ({result.Install?.Root ?? "nicht gefunden"})");
if (result.ModLogs is { } ml)
    Console.WriteLine($"RED4ext {ml.Red4ExtVersion ?? "–"} · CET {ml.CetVersion ?? "–"} · {ml.ArchiveModCount} Archive-Mods · {ml.Red4ExtPluginCount} RED4ext-Plugins");
Console.WriteLine($"{result.Crashes.Count} Abstürze in den letzten {days} Tagen");
Console.WriteLine();

if (result.Crashes.FirstOrDefault() is { } latest)
{
    Write(ConsoleColor.Cyan, $"Neuester Absturz: {latest.Report.Time:dd.MM.yyyy HH:mm}  →  {latest.FaultLabel}  [{latest.Signature}]");
    foreach (var f in latest.Findings) PrintFinding(f);
    Console.WriteLine();
}

if (result.PatternFindings.Count > 0)
{
    Write(ConsoleColor.Cyan, "Muster über alle Abstürze");
    foreach (var f in result.PatternFindings) PrintFinding(f);
    Console.WriteLine();
}

if (result.EnvironmentFindings.Count > 0)
{
    Write(ConsoleColor.Cyan, "Mod-Umgebung und System");
    foreach (var f in result.EnvironmentFindings.OrderByDescending(f => f.Severity)) PrintFinding(f);
}

// ---------- Bericht speichern ----------
outFile ??= Path.Combine(AppContext.BaseDirectory, "reports", $"crash-report-{DateTime.Now:yyyyMMdd-HHmmss}.md");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
File.WriteAllText(outFile, MarkdownReport.Build(result, details), Encoding.UTF8);
Console.WriteLine();
Console.WriteLine($"Ausführlicher Bericht: {Path.GetFullPath(outFile)}");

// Performance-Aufnahme: gamedoctor perf [--seconds 60]
static async Task RunPerf(string[] args)
{
    int seconds = 0;
    string process = "Cyberpunk2077.exe";
    string? load = null;
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--seconds") seconds = int.Parse(args[++i]);
        else if (args[i] == "--process") process = args[++i];
        else if (args[i] == "--load") load = args[++i];
    }

    // Gespeicherte Aufnahme neu auswerten: --load <Datei> oder --load latest
    if (load is not null)
    {
        var file = load == "latest" ? GameDoctor.Core.Performance.RecordingStore.Latest() : load;
        if (file is null) { Console.WriteLine("Keine gespeicherte Aufnahme gefunden."); return; }
        var loaded = GameDoctor.Core.Performance.RecordingStore.Load(file);
        Console.WriteLine($"Aufnahme {Path.GetFileName(file)}: {loaded.Frames.Count:N0} Frames, {loaded.Sensors.Count} Sensorwerte, {loaded.Duration:mm\\:ss} min");
        PrintPerfReport(GameDoctor.Core.Performance.PerformanceAnalyzer.Analyze(loaded, GameDoctor.Core.Games.Cyberpunk.CyberpunkSettings.TryLoad()));
        return;
    }

    if (GameDoctor.Core.Performance.PresentMonTool.Find() is null)
    {
        Console.WriteLine("PresentMon fehlt. Herunterladen (ca. 1 MB, Intel, Open Source)? [j/N]");
        if (Console.ReadLine()?.Trim().ToLowerInvariant() is "j" or "ja" or "y")
            Console.WriteLine("Gespeichert: " + await GameDoctor.Core.Performance.PresentMonTool.DownloadAsync());
    }

    using var rec = new GameDoctor.Core.Performance.PerformanceRecorder(process);
    rec.LiveUpdated += s => Console.Write(
        $"\r{s.Elapsed:mm\\:ss}  {(s.GameRunning ? "" : "[Spiel läuft nicht] ")}FPS {s.Fps?.ToString("F0") ?? "–",4}  1%Low {s.OnePercentLowFps?.ToString("F0") ?? "–",4}  " +
        $"CPU {s.CpuTotalPct,3:F0}% (Kern max {s.CpuMaxCorePct,3:F0}%)  GPU {s.GpuUtilPct?.ToString("F0") ?? "–",3}%  " +
        $"VRAM {(s.VramUsedMb ?? 0) / 1024:F1}/{(s.VramTotalMb ?? 0) / 1024:F1} GB  {s.GpuTempC?.ToString("F0") ?? "–"}°C  {s.Bottleneck ?? ""}      ");
    rec.Start();
    Console.WriteLine(seconds > 0 ? $"Aufnahme läuft {seconds} s …" : "Aufnahme läuft – Enter zum Beenden.");

    if (seconds > 0) await Task.Delay(TimeSpan.FromSeconds(seconds));
    else await Task.Run(() => Console.ReadLine());

    var recording = rec.Stop();
    Console.WriteLine();
    Console.WriteLine();
    if (recording.Frames.Count > 0) Console.WriteLine("Gespeichert: " + GameDoctor.Core.Performance.RecordingStore.Save(recording));
    PrintPerfReport(GameDoctor.Core.Performance.PerformanceAnalyzer.Analyze(recording, GameDoctor.Core.Games.Cyberpunk.CyberpunkSettings.TryLoad()));
}

static void PrintPerfReport(GameDoctor.Core.Performance.PerformanceReport report)
{
    if (report.SettingsSummary is not null) Console.WriteLine("Einstellungen: " + report.SettingsSummary);
    Console.WriteLine($"Flaschenhals (Spielzeit): GPU {report.GpuLimitedShare * 100:F0} % · CPU {report.CpuLimitedShare * 100:F0} % · begrenzt {report.CappedShare * 100:F0} %");
    Console.WriteLine();
    foreach (var f in report.Findings.OrderByDescending(f => f.Severity)) PrintFinding(f);
}

// Technische Detailansicht eines einzelnen Dumps (für Entwicklung/Fehlersuche)
static void DumpDetails(string path, string? gamePath)
{
    var d = GameDoctor.Core.Dump.MinidumpReader.Analyze(path);
    if (d.Error is not null) { Console.WriteLine(d.Error); return; }
    var install = gamePath is not null ? GameDoctor.Core.Games.Cyberpunk.CyberpunkInstall.FromPath(gamePath) : GameDoctor.Core.Games.Cyberpunk.CyberpunkInstall.Locate();
    var cls = new ModuleClassifier(install?.Root);
    Console.WriteLine($"Exception 0x{d.ExceptionCode:X8} @ 0x{d.ExceptionAddress:X}  Thread {d.CrashingThreadId}");
    Console.WriteLine($"Parameter: {string.Join(", ", d.ExceptionParameters.Select(p => $"0x{p:X}"))}");
    Console.WriteLine($"Modul: {d.FaultingModule?.Path ?? "-"} +0x{d.FaultingOffset:X}");
    Console.WriteLine($"{d.Modules.Count} Module, {d.StackHits.Count} Stack-Treffer:");
    foreach (var h in d.StackHits)
        Console.WriteLine($"  {cls.Classify(h.Module),-15} {h.Module.Name}+0x{h.Offset:X}");
}

static void PrintFinding(Finding f)
{
    var color = f.Severity switch
    {
        Severity.Critical => ConsoleColor.Red,
        Severity.Warning => ConsoleColor.Yellow,
        Severity.Hint => ConsoleColor.DarkYellow,
        _ => ConsoleColor.Gray,
    };
    Write(color, $"  {MarkdownReport.Icon(f.Severity)} {f.Title}");
    Console.WriteLine($"     {f.Detail}");
    if (f.Evidence is { Count: > 0 })
        foreach (var e in f.Evidence.Take(8)) Console.WriteLine($"       • {e}");
    if (f.Recommendation is not null)
        Write(ConsoleColor.Green, $"     ➜ {f.Recommendation}");
}

static void Write(ConsoleColor c, string text)
{
    var old = Console.ForegroundColor;
    Console.ForegroundColor = c;
    Console.WriteLine(text);
    Console.ForegroundColor = old;
}
