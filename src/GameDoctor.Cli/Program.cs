using System.Text;
using GameDoctor.Core;
using GameDoctor.Core.Analysis;
using GameDoctor.Core.Report;

Console.OutputEncoding = Encoding.UTF8;

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
