using System.Text;
using GameDoctor.Core.Analysis;

namespace GameDoctor.Core.Report;

/// <summary>Erzeugt einen lesbaren Bericht (Markdown) aus dem Analyseergebnis.</summary>
public static class MarkdownReport
{
    public static string Build(AnalysisResult r, int detailedCrashes = 5)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# GameDoctor – Absturzanalyse Cyberpunk 2077");
        sb.AppendLine();
        sb.AppendLine($"Erstellt: {DateTime.Now:dd.MM.yyyy HH:mm}  ");
        sb.AppendLine($"Spiel: {r.Install?.Root ?? "nicht gefunden"} ({r.Install?.Store ?? "?"}), Version {r.GameVersion ?? "?"}  ");
        if (r.ModLogs is { } ml)
            sb.AppendLine($"RED4ext {ml.Red4ExtVersion ?? "–"} · CET {ml.CetVersion ?? "–"}  ");
        sb.AppendLine($"Ausgewertete Abstürze: {r.Crashes.Count}");
        sb.AppendLine();

        // Kurzfassung: die wichtigsten Erkenntnisse zuerst
        var top = r.Crashes.Take(1).SelectMany(c => c.Findings)
            .Concat(r.PatternFindings).Concat(r.EnvironmentFindings)
            .Where(f => f.Severity >= Severity.Warning)
            .OrderByDescending(f => f.Severity).Take(6).ToList();
        sb.AppendLine("## Kurzfassung");
        sb.AppendLine();
        if (top.Count == 0) sb.AppendLine("Keine kritischen Auffälligkeiten gefunden.");
        foreach (var f in top) sb.AppendLine($"- {Icon(f.Severity)} **{f.Title}**");
        sb.AppendLine();

        if (r.PatternFindings.Count > 0)
        {
            sb.AppendLine("## Muster über alle Abstürze");
            sb.AppendLine();
            foreach (var f in r.PatternFindings) WriteFinding(sb, f);
        }

        sb.AppendLine($"## Die letzten {Math.Min(detailedCrashes, r.Crashes.Count)} Abstürze im Detail");
        sb.AppendLine();
        foreach (var c in r.Crashes.Take(detailedCrashes))
        {
            var rep = c.Report;
            sb.AppendLine($"### {rep.Time:dd.MM.yyyy HH:mm:ss} – {c.FaultLabel}");
            sb.AppendLine();
            sb.AppendLine("| | |");
            sb.AppendLine("|---|---|");
            sb.AppendLine($"| Signatur | `{c.Signature}` |");
            sb.AppendLine($"| Fehler | {rep.Expression ?? rep.ErrorReason ?? "?"} |");
            sb.AppendLine($"| Laufzeit bis Absturz | {(rep.UptimeSeconds is int u ? TimeSpan.FromSeconds(u).ToString(@"h\:mm\:ss") : "?")} |");
            sb.AppendLine($"| Ladezustand | {rep.LoadingStage ?? "?"} |");
            sb.AppendLine($"| VRAM | {rep.VramUsedMb?.ToString() ?? "?"} / {rep.VramTotalMb?.ToString() ?? "?"} MB |");
            sb.AppendLine($"| GPU / Treiber | {rep.GpuName ?? "?"} / {rep.GpuDriver ?? "?"} |");
            sb.AppendLine($"| Patch | {rep.Patch ?? "?"} ({rep.InternalVersion ?? "?"}) |");
            sb.AppendLine($"| Ordner | `{rep.Folder}` |");
            sb.AppendLine();
            foreach (var f in c.Findings) WriteFinding(sb, f);
            if (c.SuspectModules.Count > 0)
            {
                sb.AppendLine("<details><summary>Nicht-Vanilla-Module auf dem Stack</summary>");
                sb.AppendLine();
                foreach (var s in c.SuspectModules) sb.AppendLine($"- {s.Name} – {ModuleClassifier.KindLabel(s.Kind)} ({s.Hits}×)");
                sb.AppendLine();
                sb.AppendLine("</details>");
                sb.AppendLine();
            }
        }

        if (r.EnvironmentFindings.Count > 0)
        {
            sb.AppendLine("## Mod-Umgebung und System");
            sb.AppendLine();
            foreach (var f in r.EnvironmentFindings.OrderByDescending(f => f.Severity)) WriteFinding(sb, f);
        }

        sb.AppendLine("## Alle Abstürze");
        sb.AppendLine();
        sb.AppendLine("| Zeit | Ort | Signatur | Laufzeit |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var c in r.Crashes)
            sb.AppendLine($"| {c.Report.Time:dd.MM. HH:mm} | {ModuleClassifier.KindLabel(c.FaultKind)}: {c.FaultLabel} | `{c.Signature}` | {(c.Report.UptimeSeconds is int u ? TimeSpan.FromSeconds(u).ToString(@"h\:mm\:ss") : "?")} |");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("*Hinweis: „Mods auf dem Stack“ ist eine Heuristik. Sie liefert starke Indizien, aber keinen Beweis.*");
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
