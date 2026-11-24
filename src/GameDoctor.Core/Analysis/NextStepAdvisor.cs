using GameDoctor.Core.Community;
using GameDoctor.Core.Localization;

namespace GameDoctor.Core.Analysis;

public enum NextStepAction
{
    /// <summary>Reiner Hinweis: der Spieler muss selbst etwas tun (z. B. Windows-Einstellung ändern).</summary>
    None,
    /// <summary>„Lösung ausprobieren“ für den zugehörigen Absturz öffnen.</summary>
    TryFix,
    /// <summary>Zu den laufenden Lösungsversuchen wechseln.</summary>
    OpenFixes,
    /// <summary>Den Assistenten „Welche Mod ist es?“ öffnen.</summary>
    OpenBisect,
}

/// <summary>Ein konkreter nächster Schritt in einfacher Sprache.</summary>
public sealed record NextStep(string Headline, string Reason, NextStepAction Action, string? ButtonText = null, CrashCase? Crash = null, IReadOnlyList<string>? FocusMods = null);

/// <summary>
/// Sucht aus allen Befunden die wenigen Schritte heraus, die der Spieler jetzt tun kann – wichtigster zuerst.
/// Es werden nur vorhandene Befunde mit einer Empfehlung verwendet; hier wird nichts Neues diagnostiziert.
/// </summary>
public static class NextStepAdvisor
{
    private const int MaxSteps = 3;

    public static List<NextStep> Build(AnalysisResult r, IReadOnlyList<(FixAttempt Attempt, FixEvaluation Eval)> attempts, BisectSession? bisect = null)
    {
        var steps = new List<NextStep>();
        var latest = r.Crashes.FirstOrDefault();
        bool latestIsRecent = latest is not null && latest.Report.Time >= DateTime.Now.AddDays(-14);

        // 1) Ein laufender Test geht vor: erst sehen, ob er geholfen hat
        foreach (var (a, e) in attempts.Where(x => x.Attempt.Verdict == UserVerdict.None))
        {
            if (e.Status == FixStatus.ModStillInstalled)
            {
                steps.Add(new NextStep(L.F("„{0}“ noch ausschalten", a.TargetLabel),
                    L.T("Du hast einen Test gestartet, aber die Mod ist noch aktiv. Schalte sie in deinem Mod-Manager aus, dann läuft der Test."),
                    NextStepAction.OpenFixes, L.T("Anleitung öffnen")));
                break;
            }
            if (e.Status == FixStatus.Waiting)
            {
                steps.Add(new NextStep(L.F("Weiterspielen – der Test mit „{0}“ läuft", a.TargetLabel), e.Detail, NextStepAction.OpenFixes, L.T("Zum Test")));
                break;
            }
        }

        // 2) Eine laufende Mod-Suche
        if (bisect is { Done: false })
            steps.Add(new NextStep(L.F("Mod-Suche fortsetzen (Runde {0})", bisect.Round),
                L.T("Du bist mitten in der Suche nach der Mod, die die Abstürze verursacht."), NextStepAction.OpenBisect, L.T("Fortsetzen")));

        // 3) Die wichtigsten Empfehlungen aus den Befunden (nicht nur für Experten, mit klarer Handlung)
        var candidates = new List<(Finding F, CrashCase? Crash)>();
        foreach (var f in r.PatternFindings)
            candidates.Add((f, f.Signature is { } sig ? r.Crashes.Where(c => c.Signature == sig).OrderByDescending(c => c.Report.Time).FirstOrDefault() ?? latest : latest));
        if (latestIsRecent)
            foreach (var f in latest!.Findings) candidates.Add((f, latest));
        foreach (var f in r.EnvironmentFindings) candidates.Add((f, latest));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (f, crash) in candidates
                     .Where(x => !x.F.ExpertOnly && x.F.Severity >= Severity.Warning && !string.IsNullOrWhiteSpace(x.F.Recommendation))
                     .OrderByDescending(x => x.F.Severity)) // stabil: Muster zuerst, dann der letzte Absturz, dann Umgebung
        {
            if (steps.Count >= MaxSteps) break;
            if (!seen.Add(f.Recommendation!)) continue;
            bool canTry = crash is not null && f.Category is "Ursache" or "Speicher" or "Hardware" or "Mods" or "System";
            steps.Add(new NextStep(f.Recommendation!, f.PlainTitle ?? f.Title,
                canTry && crash is not null ? NextStepAction.TryFix : NextStepAction.None,
                canTry && crash is not null ? L.T("Lösung ausprobieren …") : null, crash, f.FocusMods));
        }

        // 4) Keine klare Spur, aber oft abgestürzt: Mods schrittweise eingrenzen
        bool hasMods = r.Inventory.Mods.Count >= 4;
        if (steps.Count < MaxSteps && hasMods && r.Crashes.Count >= 3 && bisect is null or { Done: true }
            && !steps.Any(s => s.Action == NextStepAction.OpenBisect))
            steps.Add(new NextStep(L.T("Die Mod finden, die die Abstürze auslöst"),
                L.F("Du hattest {0} Abstürze, aber keine einzelne Mod sticht klar heraus. Mit der Mod-Suche grenzt du sie Schritt für Schritt ein.", r.Crashes.Count),
                NextStepAction.OpenBisect, L.T("Mod-Suche starten")));

        // 5) Nichts zu tun
        if (steps.Count == 0)
            steps.Add(r.Crashes.Count == 0
                ? new NextStep(L.T("Du musst nichts tun"), L.T("Im gewählten Zeitraum gab es keine Abstürze."), NextStepAction.None)
                : new NextStep(L.T("Weiterspielen und beobachten"),
                    L.T("Aus den bisherigen Abstürzen ergibt sich noch kein klarer Schritt. Spiele weiter und analysiere nach dem nächsten Absturz erneut."), NextStepAction.None));

        return steps;
    }
}
