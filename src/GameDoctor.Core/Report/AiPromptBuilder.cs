using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameDoctor.Core.Analysis;
using GameDoctor.Core.Localization;
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
/// Enthält Aufgabenstellung, System, Mod-Umgebung, Abstürze, Muster und Log-Fehler – anonymisiert und in der
/// eingestellten Sprache (die KI wird gebeten, in derselben Sprache zu antworten).
/// </summary>
public static class AiPromptBuilder
{
    public static string Build(AnalysisResult r, AiPromptOptions o)
    {
        var sb = new StringBuilder();

        // ---------------- Aufgabe
        sb.AppendLine(L.T("Du bist Experte für Cyberpunk 2077, das Modding-Ökosystem (RED4ext, Cyber Engine Tweaks, redscript, ArchiveXL, TweakXL, Codeware) und die Analyse von Windows-Abstürzen."));
        sb.AppendLine(L.T("Unten stehen Daten, die das Diagnosetool „GameDoctor“ auf meinem PC gesammelt hat: Crashreports der REDEngine, ausgewertete Minidumps, Mod-Logs, Windows-Ereignisse und Systeminfos."));
        sb.AppendLine();
        sb.AppendLine(L.T("Bitte:"));
        sb.AppendLine(L.T("1. Nenne die wahrscheinlichsten Absturzursachen, sortiert nach Wahrscheinlichkeit."));
        sb.AppendLine(L.T("2. Trenne klar zwischen dem, was die Daten belegen, und dem, was Vermutung ist."));
        sb.AppendLine(L.T("3. Gib mir konkrete, nummerierte Schritte zum Eingrenzen und Beheben (welche Mod zuerst deaktivieren, was aktualisieren, welche Einstellung ändern)."));
        sb.AppendLine(L.T("4. Sag mir, welche Informationen dir noch fehlen, um sicherer zu sein."));
        sb.AppendLine(L.T("Hinweise zu den Daten: CD Projekt veröffentlicht keine Debug-Symbole, Adressen in Cyberpunk2077.exe sind daher nur als Modul+Offset bekannt. „Mods auf dem Stack“ sind Rücksprungadressen im Stack des abstürzenden Threads (Heuristik: starkes Indiz, kein Beweis). Antworte auf Deutsch."));
        sb.AppendLine();

        // ---------------- System
        sb.AppendLine(L.T("## System und Spiel"));
        sb.AppendLine(L.F("- Spiel: Cyberpunk 2077, Version {0} ({1})", r.GameVersion ?? "?", r.Install is null ? "?" : L.T(r.Install.Store)));
        var latest = r.Crashes.FirstOrDefault();
        if (latest is not null)
        {
            sb.AppendLine(L.F("- Patch laut Crashreport: {0} · {1}", latest.Report.Patch ?? "?", latest.Report.InternalVersion ?? "?"));
            sb.AppendLine(L.F("- GPU: {0}, Treiber {1}, {2} MB VRAM", latest.Report.GpuName ?? "?", latest.Report.GpuDriver ?? "?", latest.Report.VramTotalMb?.ToString() ?? "?"));
        }
        if (latest?.Report.SysInfo is { } sys) AppendSysInfo(sb, sys);
        sb.AppendLine(L.F("- Auslagerungsdatei: {0}", PageFileInfo.Read().Describe()));
        if (r.ModLogs is { } ml)
        {
            sb.AppendLine($"- Frameworks: RED4ext {ml.Red4ExtVersion ?? "–"}, Cyber Engine Tweaks {ml.CetVersion ?? "–"}");
            sb.AppendLine("- Mods: " + L.F("{0} Archive-Mods, {1} RED4ext-Plugins, {2} redscript-Mods, {3} CET-Mods.", ml.ArchiveModCount, ml.Red4ExtPluginCount, ml.RedscriptModCount, ml.CetModCount));
        }
        sb.AppendLine();

        // ---------------- Datenquellen – damit die KI weiß, was fehlt, statt aus fehlenden Daten falsche Schlüsse zu ziehen
        var gaps = r.DataSources.Where(d => d.Status is DataSourceStatus.Missing or DataSourceStatus.Partial).ToList();
        sb.AppendLine(L.T("## Datenquellen"));
        sb.AppendLine(L.F("- Mod-Manager: {0}", r.Inventory.Manager ?? L.T("keiner erkannt")) + (r.Inventory.ExtraRoots.Count > 0 ? " " + L.T("(Mods liegen in virtuellen Ordnern)") : ""));
        if (gaps.Count == 0) sb.AppendLine(L.T("- Alle Datenquellen vorhanden."));
        foreach (var d in gaps)
            sb.AppendLine(L.F("- FEHLT/UNVOLLSTÄNDIG: {0} – {1}", d.Name, d.Detail));
        sb.AppendLine();

        // ---------------- Fokus-Absturz
        var focus = o.FocusCrash ?? latest;
        if (focus is not null)
        {
            sb.AppendLine(L.F("## Absturz im Detail ({0:g})", focus.Report.Time));
            AppendCrash(sb, focus, detailed: true);
            sb.AppendLine();
        }

        // ---------------- Weitere Abstürze
        var others = r.Crashes.Where(c => c != focus).Take(o.OtherCrashes).ToList();
        if (others.Count > 0)
        {
            sb.AppendLine(L.F("## Weitere Abstürze (insgesamt {0} im Zeitraum, hier die {1} neuesten)", r.Crashes.Count, others.Count));
            sb.AppendLine(L.T("Zeit | Ort im Code | Fehler | Laufzeit | Position [x, y, z] | Nicht-Vanilla-Module auf dem Stack"));
            foreach (var c in others)
            {
                var pos = c.Report.Position is { } p ? $"[{p.X:F0}, {p.Y:F0}, {p.Z:F0}]" : "–";
                var mods = c.SuspectModules.Count > 0 ? string.Join(", ", c.SuspectModules.Select(s => s.Name)) : "–";
                sb.AppendLine(L.F("{0:g}", c.Report.Time) + $" | {c.Signature} | {ShortException(c)} | {Uptime(c)} | {pos} | {mods}");
            }
            sb.AppendLine();
        }

        // ---------------- Muster
        if (o.IncludePatterns && r.PatternFindings.Count > 0)
        {
            sb.AppendLine(L.T("## Muster, die GameDoctor über alle Abstürze erkannt hat"));
            foreach (var f in r.PatternFindings) AppendFinding(sb, f);
            sb.AppendLine();
        }

        // ---------------- Umgebung
        if (o.IncludeEnvironment && r.EnvironmentFindings.Count > 0)
        {
            sb.AppendLine(L.T("## Mod-Umgebung, Hintergrundfehler und System (von GameDoctor bewertet)"));
            foreach (var f in r.EnvironmentFindings.Where(f => f.Severity >= Severity.Hint)) AppendFinding(sb, f);
            sb.AppendLine();
        }

        if (o.IncludeLogErrors && r.ModLogs is { } logs)
        {
            var errors = logs.Issues.Where(i => i.Severity >= Severity.Warning).OrderByDescending(i => i.Count).Take(25).ToList();
            if (errors.Count > 0)
            {
                sb.AppendLine(L.T("## Fehler aus den Mod-Logs (aktuellste Sitzung, gruppiert)"));
                foreach (var i in errors)
                    sb.AppendLine($"- [{i.Source}{(i.Mod is not null ? " / " + i.Mod : "")}] {i.Message}{(i.Count > 1 ? $" ({i.Count}×)" : "")}");
                sb.AppendLine();
            }
        }

        // ---------------- Mod-Liste
        if (o.IncludeModList && r.Install is { } inst)
        {
            sb.AppendLine(L.T("## Installierte Mods (Ordnernamen)"));
            AppendDirList(sb, "RED4ext-Plugins", Path.Combine(inst.Red4ExtDir, "plugins"));
            AppendDirList(sb, "CET-Mods", Path.Combine(inst.CetDir, "mods"));
            AppendDirList(sb, "redscript-Mods", inst.RedscriptDir);
            sb.AppendLine(L.F("- Archive-Mods: {0} Dateien (nicht einzeln aufgeführt)", r.ModLogs?.ArchiveModCount ?? 0));
            sb.AppendLine();
        }

        // ---------------- Performance
        if (o.Performance is { FrameCount: > 0 } perf)
        {
            sb.AppendLine(L.T("## Letzte Performance-Aufnahme"));
            sb.AppendLine(L.F("- Ø {0:F0} FPS, 1 % Low {1:F0}, 0,1 % Low {2:F0}, {3:F1} Ruckler/min", perf.AvgFps, perf.OnePercentLowFps, perf.PointOnePercentLowFps, perf.StuttersPerMinute));
            sb.AppendLine(L.F("- Flaschenhals nach Spielzeit: GPU {0:F0} %, CPU {1:F0} %, begrenzt {2:F0} %", perf.GpuLimitedShare * 100, perf.CpuLimitedShare * 100, perf.CappedShare * 100));
            if (perf.SettingsSummary is not null) sb.AppendLine(L.F("- Grafikeinstellungen: {0}", perf.SettingsSummary));
            foreach (var f in perf.Findings.Where(f => f.Severity >= Severity.Hint)) AppendFinding(sb, f);
            sb.AppendLine();
        }

        var text = sb.ToString();
        return o.Anonymize ? Anonymize(text) : text;
    }

    private static void AppendCrash(StringBuilder sb, CrashCase c, bool detailed)
    {
        var rep = c.Report;
        sb.AppendLine(L.F("- Abstürzendes Modul: {0} ({1}), Stelle {2}", c.FaultLabel, ModuleClassifier.KindLabel(c.FaultKind), c.Signature));
        sb.AppendLine(L.F("- Fehler: {0}", ShortException(c)) + (rep.Message is not null ? " – " + rep.Message : ""));
        sb.AppendLine(L.F("- Laufzeit bis zum Absturz: {0}, Ladezustand: {1}", Uptime(c), rep.LoadingStage ?? "?"));
        sb.AppendLine(L.F("- VRAM: {0} / {1} MB, Out-of-Memory laut Engine: {2}, Raytracing: {3}",
            rep.VramUsedMb?.ToString() ?? "?", rep.VramTotalMb?.ToString() ?? "?", rep.OutOfMemory ? L.T("ja") : L.T("nein"), rep.RayTracing?.ToString() ?? "?"));
        if (rep.Position is { } p) sb.AppendLine(L.F("- Position im Spiel: [{0:F0}, {1:F0}, {2:F0}]", p.X, p.Y, p.Z));
        if (c.SuspectModules.Count > 0)
            sb.AppendLine(L.F("- Nicht-Vanilla-Module auf dem Stack: {0}", string.Join(", ", c.SuspectModules.Select(s => $"{s.Name} ({s.Hits}×)"))));
        if (detailed && c.Dump is { Error: null } d)
        {
            var frames = d.StackHits.Take(25).Select(h => $"{h.Module.Name}+0x{h.Offset:X}");
            sb.AppendLine(L.F("- Rücksprungadressen auf dem Stack (oben = zuletzt): {0}", string.Join(", ", frames)));
        }
        if (c.NearbyEvents.Count > 0)
            sb.AppendLine(L.F("- Windows-Ereignisse ±3 min: {0}", string.Join("; ", c.NearbyEvents.Select(e => e.Summary))));
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
                sb.AppendLine(L.F("- RAM: {0:F0} GB ({1} Module, {2} MT/s)", tm.GetInt64() / 1024d / 1024 / 1024, banks.Count, speed));
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
            var op = d.ExceptionParameters[0] switch { 0 => L.T("Lesen"), 1 => L.T("Schreiben"), 8 => L.T("Ausführen"), _ => L.T("Zugriff") };
            return L.F("Access Violation ({0}), {1} an 0x{2:X}", code, op, d.ExceptionParameters[1]);
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
        var userTag = L.T("<Benutzer>");
        if (!string.IsNullOrEmpty(profile)) text = text.Replace(profile, @"C:\Users\" + userTag, StringComparison.OrdinalIgnoreCase);
        if (user.Length >= 3) text = Regex.Replace(text, Regex.Escape(user), userTag, RegexOptions.IgnoreCase);
        if (machine.Length >= 3) text = Regex.Replace(text, Regex.Escape(machine), "<PC>", RegexOptions.IgnoreCase);
        // Telemetrie-/Spieldurchlauf-IDs (GUIDs) entfernen
        text = Regex.Replace(text, @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", "<ID>");
        return text;
    }
}
