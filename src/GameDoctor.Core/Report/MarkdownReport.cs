using System.Text;
using GameDoctor.Core.Analysis;
using GameDoctor.Core.Localization;

namespace GameDoctor.Core.Report;

/// <summary>Erzeugt einen lesbaren Bericht (Markdown) aus dem Analyseergebnis – in der eingestellten Sprache.</summary>
public static class MarkdownReport
{
    public static string Build(AnalysisResult r, int detailedCrashes = 5)
    {
        var sb = new StringBuilder();
        sb.AppendLine(L.T("# GameDoctor – Absturzanalyse Cyberpunk 2077"));
        sb.AppendLine();
        sb.AppendLine(L.F("Erstellt: {0:g}", DateTime.Now) + "  ");
        sb.AppendLine(L.F("Spiel: {0} ({1}), Version {2}", r.Install?.Root ?? L.T("nicht gefunden"), r.Install is null ? "?" : L.T(r.Install.Store), r.GameVersion ?? "?") + "  ");
        if (r.ModLogs is { } ml)
            sb.AppendLine($"RED4ext {ml.Red4ExtVersion ?? "–"} · CET {ml.CetVersion ?? "–"}  ");
        sb.AppendLine(L.F("Ausgewertete Abstürze: {0}", r.Crashes.Count));
        sb.AppendLine();

        // Kurzfassung: die wichtigsten Erkenntnisse zuerst
        var top = r.Crashes.Take(1).SelectMany(c => c.Findings)
            .Concat(r.PatternFindings).Concat(r.EnvironmentFindings)
            .Where(f => f.Severity >= Severity.Warning)
            .OrderByDescending(f => f.Severity).Take(6).ToList();
        sb.AppendLine(L.T("## Kurzfassung"));
        sb.AppendLine();
        if (top.Count == 0) sb.AppendLine(L.T("Keine kritischen Auffälligkeiten gefunden."));
        foreach (var f in top) sb.AppendLine($"- {Icon(f.Severity)} **{f.Title}**");
        sb.AppendLine();

        if (r.PatternFindings.Count > 0)
        {
            sb.AppendLine(L.T("## Muster über alle Abstürze"));
            sb.AppendLine();
            foreach (var f in r.PatternFindings) WriteFinding(sb, f);
        }

        sb.AppendLine(L.F("## Die letzten {0} Abstürze im Detail", Math.Min(detailedCrashes, r.Crashes.Count)));
        sb.AppendLine();
        foreach (var c in r.Crashes.Take(detailedCrashes))
        {
            var rep = c.Report;
            sb.AppendLine(L.F("### {0:G} – {1}", rep.Time, c.FaultLabel));
            sb.AppendLine();
            sb.AppendLine("| | |");
            sb.AppendLine("|---|---|");
            sb.AppendLine($"| {L.T("Signatur")} | `{c.Signature}` |");
            sb.AppendLine($"| {L.T("Fehler")} | {rep.Expression ?? rep.ErrorReason ?? "?"} |");
            sb.AppendLine($"| {L.T("Laufzeit bis Absturz")} | {(rep.UptimeSeconds is int u ? TimeSpan.FromSeconds(u).ToString(@"h\:mm\:ss") : "?")} |");
            sb.AppendLine($"| {L.T("Ladezustand")} | {rep.LoadingStage ?? "?"} |");
            sb.AppendLine($"| VRAM | {rep.VramUsedMb?.ToString() ?? "?"} / {rep.VramTotalMb?.ToString() ?? "?"} MB |");
            sb.AppendLine($"| {L.T("GPU / Treiber")} | {rep.GpuName ?? "?"} / {rep.GpuDriver ?? "?"} |");
            sb.AppendLine($"| Patch | {rep.Patch ?? "?"} ({rep.InternalVersion ?? "?"}) |");
            if (c.Symbols is { } sy)
            {
                if (sy.Site is not null) sb.AppendLine($"| {L.T("Funktion")} | `{Symbols.SymbolFormat.Short(sy.Site, 140).Replace("|", "\\|")}` |");
                if (sy.Subsystem is not null) sb.AppendLine($"| {L.T("Teilsystem")} | {sy.Subsystem.Label} ({L.T("aus Community-Namen abgeleitet, kann falsch sein")}) |");
            }
            if (c.Dump is { Error: null } dmp)
            {
                if (dmp.BaseRegister is not null && dmp.AddressClass != FaultAddressClass.Unknown)
                    sb.AppendLine($"| {L.T("Zeiger-Register")} | `{dmp.BaseRegister} = 0x{dmp.BaseRegisterValue:X}` |");
                if (dmp.ObjectClasses.Count > 0)
                    sb.AppendLine($"| {L.T("Objekt-Klassen auf dem Stack")} | {string.Join(", ", dmp.ObjectClasses.Take(4).Select(o => $"`+0x{o.ClassOffset:X}` ×{o.Count}"))} |");
            }
            sb.AppendLine($"| {L.T("Ordner")} | `{rep.Folder}` |");
            sb.AppendLine();
            foreach (var f in c.Findings) WriteFinding(sb, f);
            if (c.Symbols is { } cs && Symbols.SymbolFormat.CallPath(cs, 12, 120).ToList() is { Count: > 0 } named)
            {
                sb.AppendLine($"<details><summary>{L.T("Benannter Aufrufpfad (Community-Namen, oben = zuletzt)")}</summary>");
                sb.AppendLine();
                foreach (var line in named) sb.AppendLine($"- `{line.Replace("`", "'")}`");
                sb.AppendLine();
                sb.AppendLine("</details>");
                sb.AppendLine();
            }
            if (c.SuspectModules.Count > 0)
            {
                sb.AppendLine($"<details><summary>{L.T("Nicht-Vanilla-Module auf dem Stack")}</summary>");
                sb.AppendLine();
                foreach (var s in c.SuspectModules) sb.AppendLine($"- {s.Name} – {ModuleClassifier.KindLabel(s.Kind)} ({s.Hits}×)");
                sb.AppendLine();
                sb.AppendLine("</details>");
                sb.AppendLine();
            }
        }

        if (r.EnvironmentFindings.Count > 0)
        {
            sb.AppendLine(L.T("## Mod-Umgebung und System"));
            sb.AppendLine();
            foreach (var f in r.EnvironmentFindings.OrderByDescending(f => f.Severity)) WriteFinding(sb, f);
        }

        sb.AppendLine(L.T("## Alle Abstürze"));
        sb.AppendLine();
        sb.AppendLine(L.T("| Zeit | Ort | Signatur | Laufzeit |"));
        sb.AppendLine("|---|---|---|---|");
        foreach (var c in r.Crashes)
            sb.AppendLine($"| {L.F("{0:g}", c.Report.Time)} | {ModuleClassifier.KindLabel(c.FaultKind)}: {c.FaultLabel} | `{c.Signature}` | {(c.Report.UptimeSeconds is int u ? TimeSpan.FromSeconds(u).ToString(@"h\:mm\:ss") : "?")} |");
        sb.AppendLine();

        sb.AppendLine(L.T("## Datenquellen"));
        sb.AppendLine();
        sb.AppendLine(L.T("| | Quelle | Stand |"));
        sb.AppendLine("|---|---|---|");
        foreach (var d in r.DataSources)
        {
            var mark = d.Status switch
            {
                Platform.DataSourceStatus.Ok => "✔",
                Platform.DataSourceStatus.Partial => "◐",
                Platform.DataSourceStatus.Missing => "✘",
                _ => "ℹ",
            };
            sb.AppendLine($"| {mark} | {d.Name} | {d.Detail.Replace("|", "\\|")}{(d.Hint is not null ? $" – *{d.Hint}*" : "")} |");
        }
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine(L.T("*Hinweis: „Mods auf dem Stack“ ist eine Heuristik. Sie liefert starke Indizien, aber keinen Beweis.*"));
        return sb.ToString();
    }

    private static void WriteFinding(StringBuilder sb, Finding f)
    {
        sb.AppendLine($"**{Icon(f.Severity)} {f.Title}**  ");
        sb.AppendLine(f.Detail);
        if (f.Evidence is { Count: > 0 })
        {
            sb.AppendLine();
            foreach (var e in f.Evidence) sb.AppendLine($"- {e.Replace("|", "\\|")}");
        }
        if (f.Recommendation is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"➜ *{f.Recommendation}*");
        }
        sb.AppendLine();
    }

    public static string Icon(Severity s) => s switch
    {
        Severity.Critical => "🔴",
        Severity.Warning => "🟠",
        Severity.Hint => "🟡",
        _ => "ℹ️",
    };
}
