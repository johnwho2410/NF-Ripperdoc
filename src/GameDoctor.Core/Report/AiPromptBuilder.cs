using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameDoctor.Core.Analysis;
using GameDoctor.Core.Performance;
using GameDoctor.Core.Platform;

namespace GameDoctor.Core.Report;

public sealed class AiPromptOptions
{
    /// <summary>Dieser Absturz wird ausführlich beschrieben (sonst der neueste).</summary>
    public CrashCase? FocusCrash { get; init; }
    public int OtherCrashes { get; init; } = 10;
    public bool IncludePatterns { get; init; } = true;
    public bool IncludeEnvironment { get; init; } = true;
    public bool IncludeModList { get; init; } = true;
    public bool IncludeLogErrors { get; init; } = true;
    public PerformanceReport? Performance { get; init; }
    public bool Anonymize { get; init; } = true;
}

/// <summary>
/// Baut einen Prompt, mit dem man die gesammelten Daten einer KI (ChatGPT, Claude, Gemini …) zur Auswertung geben kann.
/// Enthält Aufgabenstellung, System, Mod-Umgebung, Abstürze, Muster und Log-Fehler – anonymisiert.
/// </summary>
public static class AiPromptBuilder
{
    public static string Build(AnalysisResult r, AiPromptOptions o)
    {
        var sb = new StringBuilder();

        // ---------------- Aufgabe
        sb.AppendLine("Du bist Experte für Cyberpunk 2077, das Modding-Ökosystem (RED4ext, Cyber Engine Tweaks, redscript, ArchiveXL, TweakXL, Codeware) und die Analyse von Windows-Abstürzen.");
        sb.AppendLine("Unten stehen Daten, die das Diagnosetool „GameDoctor“ auf meinem PC gesammelt hat: Crashreports der REDEngine, ausgewertete Minidumps, Mod-Logs, Windows-Ereignisse und Systeminfos.");
        sb.AppendLine();
        sb.AppendLine("Bitte:");
        sb.AppendLine("1. Nenne die wahrscheinlichsten Absturzursachen, sortiert nach Wahrscheinlichkeit.");
        sb.AppendLine("2. Trenne klar zwischen dem, was die Daten belegen, und dem, was Vermutung ist.");
        sb.AppendLine("3. Gib mir konkrete, nummerierte Schritte zum Eingrenzen und Beheben (welche Mod zuerst deaktivieren, was aktualisieren, welche Einstellung ändern).");
        sb.AppendLine("4. Sag mir, welche Informationen dir noch fehlen, um sicherer zu sein.");
        sb.AppendLine("Hinweise zu den Daten: CD Projekt veröffentlicht keine Debug-Symbole, Adressen in Cyberpunk2077.exe sind daher nur als Modul+Offset bekannt. " +
                      "„Mods auf dem Stack“ sind Rücksprungadressen im Stack des abstürzenden Threads (Heuristik: starkes Indiz, kein Beweis). Antworte auf Deutsch.");
        sb.AppendLine();

        // ---------------- System
        sb.AppendLine("## System und Spiel");
        sb.AppendLine($"- Spiel: Cyberpunk 2077, Version {r.GameVersion ?? "?"} ({r.Install?.Store ?? "?"})");
        var latest = r.Crashes.FirstOrDefault();
        if (latest is not null)
        {
            sb.AppendLine($"- Patch laut Crashreport: {latest.Report.Patch ?? "?"} · {latest.Report.InternalVersion ?? "?"}");
            sb.AppendLine($"- GPU: {latest.Report.GpuName ?? "?"}, Treiber {latest.Report.GpuDriver ?? "?"}, {latest.Report.VramTotalMb?.ToString() ?? "?"} MB VRAM");
        }
        if (latest?.Report.SysInfo is { } sys) AppendSysInfo(sb, sys);
        sb.AppendLine($"- Auslagerungsdatei: {PageFileInfo.Read().Describe()}");
        if (r.ModLogs is { } ml)
        {
            sb.AppendLine($"- Frameworks: RED4ext {ml.Red4ExtVersion ?? "–"}, Cyber Engine Tweaks {ml.CetVersion ?? "–"}");
            sb.AppendLine($"- Mods: {ml.ArchiveModCount} Archive-Mods, {ml.Red4ExtPluginCount} RED4ext-Plugins, {ml.RedscriptModCount} redscript-Mods, {ml.CetModCount} CET-Mods");
        }
        sb.AppendLine();

        // ---------------- Datenquellen – damit die KI weiß, was fehlt, statt aus fehlenden Daten falsche Schlüsse zu ziehen
        var gaps = r.DataSources.Where(d => d.Status is DataSourceStatus.Missing or DataSourceStatus.Partial).ToList();
        sb.AppendLine("## Datenquellen");
        sb.AppendLine($"- Mod-Manager: {r.Inventory.Manager ?? "keiner erkannt"}{(r.Inventory.ExtraRoots.Count > 0 ? " (Mods liegen in virtuellen Ordnern)" : "")}");
        if (gaps.Count == 0) sb.AppendLine("- Alle Datenquellen vorhanden.");
        foreach (var d in gaps)
            sb.AppendLine($"- FEHLT/UNVOLLSTÄNDIG: {d.Name} – {d.Detail}");
        sb.AppendLine();

        // ---------------- Fokus-Absturz
        var focus = o.FocusCrash ?? latest;
        if (focus is not null)
        {
            sb.AppendLine($"## Absturz im Detail ({focus.Report.Time:dd.MM.yyyy HH:mm})");
            AppendCrash(sb, focus, detailed: true);
            sb.AppendLine();
        }

        // ---------------- Weitere Abstürze
        var others = r.Crashes.Where(c => c != focus).Take(o.OtherCrashes).ToList();
        if (others.Count > 0)
        {
            sb.AppendLine($"## Weitere Abstürze (insgesamt {r.Crashes.Count} im Zeitraum, hier die {others.Count} neuesten)");
            sb.AppendLine("Zeit | Ort im Code | Fehler | Laufzeit | Position [x, y, z] | Nicht-Vanilla-Module auf dem Stack");
            foreach (var c in others)
            {
                var pos = c.Report.Position is { } p ? $"[{p.X:F0}, {p.Y:F0}, {p.Z:F0}]" : "–";
                var mods = c.SuspectModules.Count > 0 ? string.Join(", ", c.SuspectModules.Select(s => s.Name)) : "–";
                sb.AppendLine($"{c.Report.Time:dd.MM. HH:mm} | {c.Signature} | {ShortException(c)} | {Uptime(c)} | {pos} | {mods}");
            }
            sb.AppendLine();
        }

        // ---------------- Muster
        if (o.IncludePatterns && r.PatternFindings.Count > 0)
        {
            sb.AppendLine("## Muster, die GameDoctor über alle Abstürze erkannt hat");
            foreach (var f in r.PatternFindings) AppendFinding(sb, f);
            sb.AppendLine();
        }

        // ---------------- Umgebung
        if (o.IncludeEnvironment && r.EnvironmentFindings.Count > 0)
        {
            sb.AppendLine("## Mod-Umgebung, Hintergrundfehler und System (von GameDoctor bewertet)");
            foreach (var f in r.EnvironmentFindings.Where(f => f.Severity >= Severity.Hint)) AppendFinding(sb, f);
            sb.AppendLine();
        }

        if (o.IncludeLogErrors && r.ModLogs is { } logs)
        {
            var errors = logs.Issues.Where(i => i.Severity >= Severity.Warning).OrderByDescending(i => i.Count).Take(25).ToList();
            if (errors.Count > 0)
            {
                sb.AppendLine("## Fehler aus den Mod-Logs (aktuellste Sitzung, gruppiert)");
                foreach (var i in errors)
                    sb.AppendLine($"- [{i.Source}{(i.Mod is not null ? " / " + i.Mod : "")}] {i.Message}{(i.Count > 1 ? $" ({i.Count}×)" : "")}");
                sb.AppendLine();
            }
        }

        // ---------------- Mod-Liste
        if (o.IncludeModList && r.Install is { } inst)
        {
            sb.AppendLine("## Installierte Mods (Ordnernamen)");
            AppendDirList(sb, "RED4ext-Plugins", Path.Combine(inst.Red4ExtDir, "plugins"));
            AppendDirList(sb, "CET-Mods", Path.Combine(inst.CetDir, "mods"));
            AppendDirList(sb, "redscript-Mods", inst.RedscriptDir);
            sb.AppendLine($"- Archive-Mods: {r.ModLogs?.ArchiveModCount ?? 0} Dateien (nicht einzeln aufgeführt)");
            sb.AppendLine();
        }

        // ---------------- Performance
        if (o.Performance is { FrameCount: > 0 } perf)
        {
            sb.AppendLine("## Letzte Performance-Aufnahme");
            sb.AppendLine($"- Ø {perf.AvgFps:F0} FPS, 1 % Low {perf.OnePercentLowFps:F0}, 0,1 % Low {perf.PointOnePercentLowFps:F0}, {perf.StuttersPerMinute:F1} Ruckler/min");
            sb.AppendLine($"- Flaschenhals nach Spielzeit: GPU {perf.GpuLimitedShare * 100:F0} %, CPU {perf.CpuLimitedShare * 100:F0} %, begrenzt {perf.CappedShare * 100:F0} %");
            if (perf.SettingsSummary is not null) sb.AppendLine($"- Grafikeinstellungen: {perf.SettingsSummary}");
            foreach (var f in perf.Findings.Where(f => f.Severity >= Severity.Hint)) AppendFinding(sb, f);
            sb.AppendLine();
        }

        var text = sb.ToString();
        return o.Anonymize ? Anonymize(text) : text;
    }

    private static void AppendCrash(StringBuilder sb, CrashCase c, bool detailed)
    {
        var rep = c.Report;
        sb.AppendLine($"- Abstürzendes Modul: {c.FaultLabel} ({ModuleClassifier.KindLabel(c.FaultKind)}), Stelle {c.Signature}");
        sb.AppendLine($"- Fehler: {ShortException(c)}{(rep.Message is not null ? " – " + rep.Message : "")}");
        sb.AppendLine($"- Laufzeit bis zum Absturz: {Uptime(c)}, Ladezustand: {rep.LoadingStage ?? "?"}");
        sb.AppendLine($"- VRAM: {rep.VramUsedMb?.ToString() ?? "?"} / {rep.VramTotalMb?.ToString() ?? "?"} MB, Out-of-Memory laut Engine: {(rep.OutOfMemory ? "ja" : "nein")}, Raytracing: {rep.RayTracing?.ToString() ?? "?"}");
        if (rep.Position is { } p) sb.AppendLine($"- Position im Spiel: [{p.X:F0}, {p.Y:F0}, {p.Z:F0}]");
        if (c.SuspectModules.Count > 0)
            sb.AppendLine($"- Nicht-Vanilla-Module auf dem Stack: {string.Join(", ", c.SuspectModules.Select(s => $"{s.Name} ({s.Hits}×)"))}");
        if (detailed && c.Dump is { Error: null } d)
        {
            var frames = d.StackHits.Take(25).Select(h => $"{h.Module.Name}+0x{h.Offset:X}");
            sb.AppendLine($"- Rücksprungadressen auf dem Stack (oben = zuletzt): {string.Join(", ", frames)}");
        }
        if (c.NearbyEvents.Count > 0)
            sb.AppendLine($"- Windows-Ereignisse ±3 min: {string.Join("; ", c.NearbyEvents.Select(e => e.Summary))}");
        foreach (var f in c.Findings) AppendFinding(sb, f, "  ");
    }

    private static void AppendFinding(StringBuilder sb, Finding f, string indent = "")
    {
        sb.AppendLine($"{indent}- [{f.Severity}] {f.Title}: {f.Detail}");
        if (f.Evidence is { Count: > 0 })
            foreach (var e in f.Evidence.Take(12)) sb.AppendLine($"{indent}    · {e}");
    }

    private static void AppendSysInfo(StringBuilder sb, JsonElement sys)
    {
        try
        {
            if (sys.TryGetProperty("Processors", out var cpus) && cpus.GetArrayLength() > 0)
                sb.AppendLine($"- CPU: {cpus[0].GetProperty("Name").GetString()?.Trim()}");
            if (sys.TryGetProperty("TotalPhysicalMemory", out var tm))
            {
                var banks = sys.TryGetProperty("MemoryBanks", out var b) ? b.EnumerateArray().ToList() : [];
                var speed = banks.Select(x => x.TryGetProperty("Speed", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0).DefaultIfEmpty().Max();
                sb.AppendLine($"- RAM: {tm.GetInt64() / 1024d / 1024 / 1024:F0} GB ({banks.Count} Module, {speed} MT/s)");
            }
            if (sys.TryGetProperty("OperatingSystem", out var os))
                sb.AppendLine($"- Windows: {os.GetProperty("Caption").GetString()} {os.GetProperty("Version").GetString()}");
        }
        catch { /* unerwartetes Format */ }
    }

    private static void AppendDirList(StringBuilder sb, string label, string dir)
    {
        if (!Directory.Exists(dir)) return;
        var names = Directory.EnumerateDirectories(dir).Select(Path.GetFileName).Where(n => n is not null && !n.StartsWith("__")).OrderBy(n => n).ToList();
        sb.AppendLine($"- {label} ({names.Count}): {string.Join(", ", names)}");
    }

    private static string ShortException(CrashCase c)
    {
        if (c.Dump is not { Error: null } d) return c.Report.Expression ?? "?";
        var code = $"0x{d.ExceptionCode:X8}";
        if (d.ExceptionCode == 0xC0000005 && d.ExceptionParameters.Length >= 2)
        {
            var op = d.ExceptionParameters[0] switch { 0 => "Lesen", 1 => "Schreiben", 8 => "Ausführen", _ => "Zugriff" };
            return $"Access Violation ({code}), {op} an 0x{d.ExceptionParameters[1]:X}";
        }
        return code;
    }

    private static string Uptime(CrashCase c) =>
        c.Report.UptimeSeconds is int u ? TimeSpan.FromSeconds(u).ToString(@"h\:mm\:ss") : "?";

    /// <summary>Entfernt Benutzername, PC-Name und persönliche Pfade.</summary>
    public static string Anonymize(string text)
    {
        var user = Environment.UserName;
        var machine = Environment.MachineName;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile)) text = text.Replace(profile, @"C:\Users\<Benutzer>", StringComparison.OrdinalIgnoreCase);
        if (user.Length >= 3) text = Regex.Replace(text, Regex.Escape(user), "<Benutzer>", RegexOptions.IgnoreCase);
        if (machine.Length >= 3) text = Regex.Replace(text, Regex.Escape(machine), "<PC>", RegexOptions.IgnoreCase);
        // Telemetrie-/Spieldurchlauf-IDs (GUIDs) entfernen
        text = Regex.Replace(text, @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", "<ID>");
        return text;
    }
}
