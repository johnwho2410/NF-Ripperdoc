using GameDoctor.Core.Analysis;
using GameDoctor.Core.Games.Cyberpunk;

namespace GameDoctor.Core.Community;

public enum FixStatus
{
    /// <summary>Noch zu wenig gespielt für eine Aussage.</summary>
    Waiting,
    /// <summary>Lange genug ohne die Absturzstelle gespielt – statistisch belegt.</summary>
    LikelyHelped,
    /// <summary>Die Absturzstelle trat danach erneut auf.</summary>
    NotHelped,
    /// <summary>Die Mod ist offenbar noch installiert – der Test läuft gar nicht.</summary>
    ModStillInstalled,
}

public sealed record FixEvaluation(
    FixStatus Status,
    string Headline,
    string Detail,
    double Progress,
    TimeSpan PlayTimeSince,
    double? ExpectedCrashes,
    IReadOnlyList<DateTime> RecurredAt)
{
    /// <summary>
    /// Die bisherigen Abstürze passierten alle am selben Ort in der Spielwelt. Bleiben sie aus, kann das auch daran
    /// liegen, dass der Spieler dort nicht mehr war – ein „geholfen“ ist dann nicht belastbar.
    /// </summary>
    public bool LocationBound { get; init; }

    /// <summary>Ergebnis, das (anonym) geteilt werden darf: nur bei belastbarer Aussage.</summary>
    public string? ShareableOutcome(FixAttempt a) => Status switch
    {
        FixStatus.NotHelped => "NotHelped",
        FixStatus.LikelyHelped when LocationBound => null,
        FixStatus.LikelyHelped => "Helped",
        FixStatus.Waiting when a.Verdict == UserVerdict.NotHelped => "NotHelped",
        // Ein „Hat geholfen“ des Nutzers zählt erst nach etwas Spielzeit ohne erneuten Absturz
        FixStatus.Waiting when a.Verdict == UserVerdict.Helped && PlayTimeSince >= TimeSpan.FromHours(1) => "Helped",
        _ => null,
    };
}

/// <summary>
/// Bewertet Lösungsversuche automatisch anhand der Abstürze und der Spielzeit danach.
///
/// Idee: Trat eine Absturzstelle vorher im Schnitt alle N Stunden auf, erwartet man nach T Stunden T/N Abstürze.
/// Bleiben trotz ≥ 3 erwarteter Abstürze alle aus, ist das bei unverändertem Problem nur zu ~5 % Zufall
/// (Poisson: e^-3 ≈ 0,05) – der Versuch hat sehr wahrscheinlich geholfen.
/// </summary>
public sealed class FixEvaluator
{
    private const double ExpectedForProof = 3.0;
    private static readonly TimeSpan BaselineWindow = TimeSpan.FromDays(14);
    private static readonly TimeSpan FallbackPlayTime = TimeSpan.FromHours(6);

    private readonly IReadOnlyList<CrashCase> _crashes;
    private readonly PlaySessionLedger _ledger;
    private readonly HashSet<string>? _installedFiles;

    /// <param name="extraRoots">Aktive virtuelle Mod-Ordner (Mod Organizer 2) – dort liegen Mods statt im Spielordner.</param>
    public FixEvaluator(IReadOnlyList<CrashCase> crashes, PlaySessionLedger ledger, CyberpunkInstall? install, IReadOnlyList<string>? extraRoots = null)
    {
        _crashes = crashes;
        _ledger = ledger;
        _installedFiles = install is null ? null : IndexModFiles(install, extraRoots ?? []);
    }

    public FixEvaluation Evaluate(FixAttempt a)
    {
        var now = DateTime.Now;
        var playSince = _ledger.PlayTime(a.StartedAt, now);
        var sigs = a.Signatures.ToHashSet(StringComparer.Ordinal);

        // 1) Ist die Mod überhaupt deaktiviert?
        if (a.Action == FixAction.DisableMod && _installedFiles is not null && a.TargetFiles.Count > 0)
        {
            var still = a.TargetFiles.Where(f => _installedFiles.Contains(f)).ToList();
            if (still.Count > 0)
                return new FixEvaluation(FixStatus.ModStillInstalled,
                    $"„{a.TargetLabel}“ ist noch installiert",
                    $"Im Spielordner liegen noch Dateien dieser Mod ({string.Join(", ", still.Take(3))}{(still.Count > 3 ? " …" : "")}). " +
                    "Solange sie aktiv ist, kann der Test nichts aussagen. In Vortex deaktivieren und „Bereitstellen“ (Deploy) nicht vergessen.",
                    0, playSince, null, []);
        }

        // 2) Ist die Absturzstelle seitdem wieder aufgetreten?
        var recurred = _crashes.Where(c => c.Report.Time > a.StartedAt && sigs.Contains(c.Signature))
            .Select(c => c.Report.Time).OrderBy(t => t).ToList();
        if (recurred.Count > 0)
            return new FixEvaluation(FixStatus.NotHelped,
                "Hat nicht geholfen",
                $"Die Absturzstelle trat danach {recurred.Count}× erneut auf (zuerst am {recurred[0]:dd.MM. HH:mm}). " +
                (a.Action == FixAction.DisableMod ? $"„{a.TargetLabel}“ ist damit sehr wahrscheinlich nicht die Ursache – du kannst die Mod wieder aktivieren." : ""),
                1, playSince, null, recurred);

        // 3) Wie oft trat die Absturzstelle vorher auf – bezogen auf die Spielzeit?
        var result = EvaluateRate(a, sigs, playSince);
        if (result.Status is FixStatus.LikelyHelped or FixStatus.Waiting && IsLocationBound(a, sigs))
            result = result with
            {
                LocationBound = true,
                Detail = result.Detail + " Achtung: Die bisherigen Abstürze an dieser Stelle passierten alle am selben Ort in der Spielwelt. " +
                         "Bleiben sie aus, kann das auch daran liegen, dass du dort nicht mehr warst – am aussagekräftigsten ist der Test, wenn du den Ort wieder besuchst.",
            };
        return result;
    }

    /// <summary>Alle bisherigen Abstürze dieser Absturzstelle innerhalb von 150 m?</summary>
    private bool IsLocationBound(FixAttempt a, HashSet<string> sigs)
    {
        var positions = _crashes.Where(c => c.Report.Time < a.StartedAt && sigs.Contains(c.Signature))
            .Select(c => c.Report.Position).OfType<(double X, double Y, double Z)>().ToList();
        if (positions.Count < 2) return false;
        double cx = positions.Average(p => p.X), cy = positions.Average(p => p.Y);
        return positions.All(p => Math.Sqrt(Math.Pow(p.X - cx, 2) + Math.Pow(p.Y - cy, 2)) < 150);
    }

    private FixEvaluation EvaluateRate(FixAttempt a, HashSet<string> sigs, TimeSpan playSince)
    {
        var baseFrom = a.StartedAt - BaselineWindow;
        int before = _crashes.Count(c => c.Report.Time >= baseFrom && c.Report.Time < a.StartedAt && sigs.Contains(c.Signature));
        var playBefore = _ledger.PlayTime(baseFrom, a.StartedAt);

        if (before == 0 || playBefore < TimeSpan.FromMinutes(30))
        {
            // Keine Vergleichsbasis – einfache Faustregel
            double p = Math.Min(1, playSince / FallbackPlayTime);
            return p >= 1
                ? new FixEvaluation(FixStatus.LikelyHelped, "Wahrscheinlich geholfen",
                    $"Seitdem {Hours(playSince)} gespielt ohne diese Absturzstelle. (Zu wenig Daten von vorher für eine genaue Rechnung – Faustregel: 6 Stunden ohne Absturz.)",
                    1, playSince, null, [])
                : new FixEvaluation(FixStatus.Waiting, "Test läuft",
                    $"Seitdem {Hours(playSince)} gespielt ohne diese Absturzstelle. Noch etwa {Hours(FallbackPlayTime - playSince)} spielen, dann ist das Ergebnis aussagekräftig.",
                    p, playSince, null, []);
        }

        double ratePerHour = before / Math.Max(playBefore.TotalHours, 0.1);
        double expected = ratePerHour * playSince.TotalHours;
        var every = TimeSpan.FromHours(1 / ratePerHour);

        if (expected >= ExpectedForProof)
            return new FixEvaluation(FixStatus.LikelyHelped, "Hat sehr wahrscheinlich geholfen",
                $"Vorher trat diese Absturzstelle etwa alle {Hours(every)} Spielzeit auf ({before}× in {Hours(playBefore)}). " +
                $"Seitdem {Hours(playSince)} gespielt – erwartet wären rund {expected:F0} Abstürze gewesen, aufgetreten ist keiner. " +
                "Dass das bei unverändertem Problem Zufall ist, liegt bei unter 5 %.",
                1, playSince, expected, []);

        var needed = TimeSpan.FromHours(ExpectedForProof / ratePerHour) - playSince;
        return new FixEvaluation(FixStatus.Waiting, "Test läuft",
            $"Vorher etwa alle {Hours(every)} ein Absturz an dieser Stelle. Seitdem {Hours(playSince)} ohne. " +
            $"Noch etwa {Hours(needed)} spielen, dann ist das Ergebnis belastbar.",
            expected / ExpectedForProof, playSince, expected, []);
    }

    private static string Hours(TimeSpan t) =>
        t.TotalMinutes < 90 ? $"{Math.Max(0, t.TotalMinutes):F0} Min." : $"{t.TotalHours:F1} Std.";

    private static HashSet<string> IndexModFiles(CyberpunkInstall install, IReadOnlyList<string> extraRoots)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string dir, SearchOption opt)
        {
            if (!Directory.Exists(dir)) return;
            try { foreach (var f in Directory.EnumerateFiles(dir, "*", opt)) set.Add(Path.GetFileName(f)); }
            catch { /* Zugriff verweigert o. ä. */ }
        }
        Add(install.BinDir, SearchOption.TopDirectoryOnly);
        Add(Path.Combine(install.BinDir, "plugins"), SearchOption.AllDirectories);
        Add(Path.Combine(install.Red4ExtDir, "plugins"), SearchOption.AllDirectories);
        Add(install.ArchiveModDir, SearchOption.AllDirectories);
        Add(install.RedscriptDir, SearchOption.AllDirectories);
        Add(Path.Combine(install.Root, "r6", "tweaks"), SearchOption.AllDirectories);
        // Mod Organizer 2: aktive Mods liegen in eigenen Ordnern (deaktivierte sind dort nicht enthalten)
        foreach (var root in extraRoots) Add(root, SearchOption.AllDirectories);
        return set;
    }
}
