using System.Text.Json;
using System.Text.Json.Serialization;
using GameDoctor.Core.Analysis;
using GameDoctor.Core.Games.Cyberpunk;
using GameDoctor.Core.Localization;

namespace GameDoctor.Core.Community;

/// <summary>Eine Mod, die bei der Mod-Suche getestet werden kann. Dateinamen dienen nur zur Prüfung, ob die Mod wirklich ein-/ausgeschaltet ist.</summary>
public sealed record BisectMod(string Name, int? NexusId, List<string> Files, bool Suspect);

public enum BisectOutcome
{
    /// <summary>Mit den ausgeschalteten Mods stürzte das Spiel weiter ab: Die Ursache liegt bei den anderen.</summary>
    Crashed,
    /// <summary>Mit den ausgeschalteten Mods lief das Spiel stabil: Die Ursache liegt bei ihnen.</summary>
    Stable,
}

public sealed record BisectRound(int Number, List<string> Disabled, DateTime StartedAt, BisectOutcome Outcome, TimeSpan PlayTime);

/// <summary>Die laufende Runde: welche Mods aus sein sollen und ob der Spieler sie schon umgestellt hat.</summary>
public sealed record BisectCurrent
{
    public List<string> Disabled { get; init; } = [];
    /// <summary>Mods, die gegenüber der Runde davor wieder eingeschaltet werden müssen.</summary>
    public List<string> ReEnable { get; init; } = [];
    /// <summary>Letzte Runde: nur noch die eine verdächtige Mod ausschalten, um das Ergebnis zu bestätigen.</summary>
    public bool Confirming { get; init; }
    /// <summary>Wann der Spieler „Runde starten“ geklickt hat (null = noch nicht begonnen).</summary>
    public DateTime? StartedAt { get; init; }
}

/// <summary>
/// Die Suche nach der Mod, die Abstürze auslöst, durch schrittweises Halbieren: In jeder Runde wird die Hälfte der noch verdächtigen Mods
/// ausgeschaltet. Stürzt das Spiel dann nicht mehr ab, steckt die Ursache in dieser Hälfte, sonst in der anderen.
/// GameDoctor ändert dabei nie selbst etwas an den Mods – der Spieler schaltet sie im Mod-Manager um.
/// </summary>
public sealed class BisectSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public List<BisectMod> Pool { get; set; } = [];
    /// <summary>Noch verdächtige Mods (Namen aus <see cref="Pool"/>).</summary>
    public List<string> Suspects { get; set; } = [];
    public List<BisectRound> History { get; set; } = [];
    public BisectCurrent? Current { get; set; }
    public bool Done { get; set; }
    /// <summary>Die gefundene Mod (null = keine einzelne Mod gefunden, siehe <see cref="Conclusion"/>).</summary>
    public string? Culprit { get; set; }
    public string? Conclusion { get; set; }

    [JsonIgnore] public int Round => History.Count + 1;

    /// <summary>Wie viele Runden es voraussichtlich braucht (Halbieren + eine Bestätigung).</summary>
    public static int EstimateRounds(int suspects) => suspects <= 1 ? 1 : (int)Math.Ceiling(Math.Log2(suspects)) + 1;
}

public static class BisectPlanner
{
    // Mods, die andere Mods voraussetzen: Ausschalten würde Folgefehler auslösen und das Ergebnis verfälschen.
    private static readonly string[] BaseMods =
    [
        "red4ext", "cyber engine tweaks", "codeware", "archivexl", "tweakxl", "redscript", "mod settings", "native settings",
        "input loader", "audioware", "audioxl", "radioext", "equipmentex", "equipment-ex", "redfilesystem",
    ];

    public static bool IsBaseMod(string name) => BaseMods.Any(b => name.Contains(b, StringComparison.OrdinalIgnoreCase));

    private static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Wie oft taucht welche installierte Mod in Abstürzen auf (anhand der Module auf dem Stack)? Der Namensabgleich ist ungefähr.</summary>
    public static Dictionary<string, int> SuspectCounts(IReadOnlyList<CrashCase> crashes, ModInventory inventory)
    {
        var suspects = crashes
            .SelectMany(c => c.SuspectModules.Where(s => s.Kind is ModuleKind.Mod or ModuleKind.Overlay && s.Installed).Select(s => s.Name).Distinct())
            .GroupBy(n => n).Select(g => (Key: Norm(g.Key), N: g.Count())).ToList();
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in inventory.Mods)
        {
            var key = Norm(m.DisplayName);
            if (key.Length < 4) continue;
            int hit = suspects.Where(s => s.Key.Contains(key) || key.Contains(s.Key)).Sum(s => s.N);
            if (hit > 0) result[m.DisplayName] = hit;
        }
        return result;
    }

    /// <summary>Die Mods, die in der Suche getestet werden können (ohne Frameworks und Bibliotheken).</summary>
    public static List<BisectMod> Candidates(ModInventory inventory, IReadOnlyDictionary<string, int> suspectCounts) =>
        inventory.Mods
            .Where(m => !IsBaseMod(m.DisplayName))
            .GroupBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(m => new BisectMod(m.DisplayName, m.NexusId, inventory.FilesOf(m.NexusId).ToList(), suspectCounts.ContainsKey(m.DisplayName)))
            .ToList();

    public static BisectSession Start(List<BisectMod> pool)
    {
        var s = new BisectSession { Pool = pool, Suspects = pool.Select(m => m.Name).ToList() };
        s.Current = NextRound(s, previous: null, firstRound: true);
        return s;
    }

    /// <summary>Beginnt die aktuelle Runde (der Spieler hat die Mods umgestellt).</summary>
    public static void BeginRound(BisectSession s, DateTime? now = null) =>
        s.Current = s.Current! with { StartedAt = now ?? DateTime.Now };

    /// <summary>Übernimmt das Ergebnis der Runde und bereitet die nächste vor (oder schließt die Suche ab).</summary>
    public static void Finish(BisectSession s, BisectOutcome outcome, TimeSpan playTime)
    {
        var cur = s.Current!;
        s.History.Add(new BisectRound(s.Round, [.. cur.Disabled], cur.StartedAt ?? DateTime.Now, outcome, playTime));

        if (cur.Confirming)
        {
            s.Done = true;
            if (outcome == BisectOutcome.Stable)
            {
                s.Culprit = s.Suspects[0];
                s.Conclusion = L.F("Mit „{0}“ ausgeschaltet stürzte das Spiel nicht mehr ab – diese Mod ist sehr wahrscheinlich die Ursache.", s.Culprit);
            }
            else
                s.Conclusion = L.T("Das Spiel stürzte auch ohne die zuletzt verdächtige Mod ab. Die Ursache ist wohl nicht diese eine Mod – vielleicht ein Zusammenspiel mehrerer Mods oder etwas anderes (Treiber, Hardware).");
            s.Current = null;
            return;
        }

        var disabled = new HashSet<string>(cur.Disabled, StringComparer.OrdinalIgnoreCase);
        s.Suspects = outcome == BisectOutcome.Stable
            ? s.Suspects.Where(disabled.Contains).ToList()          // Ursache steckt in den ausgeschalteten
            : s.Suspects.Where(n => !disabled.Contains(n)).ToList(); // Ursache steckt in den anderen

        if (s.Suspects.Count == 0)
        {
            s.Done = true;
            s.Conclusion = L.T("Die Abstürze blieben, auch wenn die getesteten Mods aus waren. Die Ursache liegt wohl nicht bei diesen Mods.");
            s.Current = null;
            return;
        }
        s.Current = NextRound(s, cur, firstRound: false);
    }

    private static BisectCurrent NextRound(BisectSession s, BisectCurrent? previous, bool firstRound)
    {
        List<string> group;
        bool confirming = false;
        if (s.Suspects.Count == 1) { group = [.. s.Suspects]; confirming = true; }
        else
        {
            // Erste Runde: zuerst die Mods ausschalten, die in Abstürzen aufgefallen sind – falls das eine überschaubare Gruppe ist
            var flagged = firstRound ? s.Pool.Where(m => m.Suspect && s.Suspects.Contains(m.Name)).Select(m => m.Name).ToList() : [];
            group = flagged.Count is >= 1 and <= 12 && flagged.Count < s.Suspects.Count
                ? flagged
                : s.Suspects.Take((s.Suspects.Count + 1) / 2).ToList();
        }
        var reEnable = previous is null ? [] : previous.Disabled.Where(n => !group.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        return new BisectCurrent { Disabled = group, ReEnable = reEnable, Confirming = confirming };
    }

    /// <summary>Anleitung für die aktuelle Runde, passend zum Mod-Manager.</summary>
    public static List<string> Instructions(BisectSession s, ModInventory inventory)
    {
        var cur = s.Current!;
        var steps = new List<string> { L.T("Cyberpunk beenden.") };
        string manager = inventory.Manager ?? "";
        steps.Add(manager switch
        {
            "Vortex" => L.T("Vortex öffnen und links auf „Mods“ gehen. Mehrere Mods lassen sich mit gedrückter Strg-Taste gemeinsam markieren und umschalten."),
            "Mod Organizer 2" => L.T("Mod Organizer 2 öffnen. Mehrere Mods lassen sich markieren und gemeinsam umschalten (Rechtsklick)."),
            _ => L.T("Die Dateien der Mods aus dem Spielordner in einen Sicherungsordner verschieben (nicht löschen)."),
        });
        steps.Add(L.F("Diese {0} Mods AUSSCHALTEN:", cur.Disabled.Count));
        steps.AddRange(cur.Disabled.Take(40).Select(n => "   • " + n));
        if (cur.Disabled.Count > 40) steps.Add("   • " + L.F("… und {0} weitere (siehe Liste im Fenster)", cur.Disabled.Count - 40));
        if (cur.ReEnable.Count > 0)
        {
            steps.Add(L.F("Diese {0} Mods wieder EINSCHALTEN (sie wurden in der Runde davor ausgeschaltet):", cur.ReEnable.Count));
            steps.AddRange(cur.ReEnable.Take(40).Select(n => "   • " + n));
            if (cur.ReEnable.Count > 40) steps.Add("   • " + L.F("… und {0} weitere", cur.ReEnable.Count - 40));
        }
        steps.Add(manager == "Vortex"
            ? L.T("Oben auf „Bereitstellen“ (Deploy) klicken – erst dann ändern sich die Dateien im Spielordner.")
            : manager == "Mod Organizer 2"
                ? L.T("Das Spiel künftig über Mod Organizer 2 starten – nur dann gilt die Änderung.")
                : L.T("Alle anderen Mods bleiben, wie sie sind."));
        steps.Add(L.T("Zurück zu GameDoctor und „Runde starten“ klicken. GameDoctor prüft, ob die Mods wirklich umgestellt sind."));
        return steps;
    }

    /// <summary>Prüft anhand der Dateien im Spielordner, ob die Umstellung stimmt. Ohne Dateiliste lässt sich nichts prüfen (Ergebnis: leer).</summary>
    public static (List<string> StillActive, List<string> StillOff) CheckApplied(BisectSession s, HashSet<string>? installedFiles)
    {
        if (installedFiles is null || s.Current is null) return ([], []);
        bool Active(string name)
        {
            var mod = s.Pool.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return mod is not null && mod.Files.Count > 0 ? mod.Files.Any(installedFiles.Contains) : false;
        }
        bool Checkable(string name) => s.Pool.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Files.Count: > 0 };

        var stillActive = s.Current.Disabled.Where(n => Checkable(n) && Active(n)).ToList();
        var stillOff = s.Current.ReEnable.Where(n => Checkable(n) && !Active(n)).ToList();
        return (stillActive, stillOff);
    }
}

public enum BisectStatus { NotStarted, Waiting, Crashed, Stable }

public sealed record BisectEvaluation(BisectStatus Status, string Text, double Progress, TimeSpan PlayTime, int CrashesSince);

/// <summary>Bewertet die laufende Runde: Gab es seit dem Start einen Absturz – oder lange genug stabiles Spielen?</summary>
public static class BisectEvaluator
{
    private const double ExpectedForProof = 3.0;
    private static readonly TimeSpan BaselineWindow = TimeSpan.FromDays(14);
    private static readonly TimeSpan MinPlay = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan MaxPlay = TimeSpan.FromHours(6);
    private static readonly TimeSpan FallbackPlay = TimeSpan.FromHours(2);

    /// <summary>Wie lange muss man ohne Absturz spielen, damit „stabil“ aussagekräftig ist? Richtet sich danach, wie oft das Spiel vorher abstürzte.</summary>
    public static TimeSpan RequiredPlayTime(DateTime start, IReadOnlyList<CrashCase> crashes, PlaySessionLedger ledger)
    {
        var from = start - BaselineWindow;
        int before = crashes.Count(c => c.Report.Time >= from && c.Report.Time < start);
        var playBefore = ledger.PlayTime(from, start);
        if (before == 0 || playBefore < TimeSpan.FromMinutes(30)) return FallbackPlay;
        var needed = TimeSpan.FromHours(ExpectedForProof * playBefore.TotalHours / before);
        return needed < MinPlay ? MinPlay : needed > MaxPlay ? MaxPlay : needed;
    }

    public static BisectEvaluation Evaluate(BisectSession s, IReadOnlyList<CrashCase> crashes, PlaySessionLedger ledger)
    {
        if (s.Current?.StartedAt is not { } start)
            return new BisectEvaluation(BisectStatus.NotStarted, L.T("Noch nicht gestartet."), 0, TimeSpan.Zero, 0);

        var since = crashes.Where(c => c.Report.Time > start).ToList();
        var play = ledger.PlayTime(start, DateTime.Now);
        if (since.Count > 0)
            return new BisectEvaluation(BisectStatus.Crashed,
                L.F("Seit dem Start ist das Spiel {0}× abgestürzt (zuletzt {1:g}).", since.Count, since.Max(c => c.Report.Time)), 1, play, since.Count);

        var need = RequiredPlayTime(start, crashes, ledger);
        double p = Math.Min(1, play / need);
        return p >= 1
            ? new BisectEvaluation(BisectStatus.Stable,
                L.F("{0} gespielt, kein Absturz – das reicht für ein belastbares Ergebnis.", Hours(play)), 1, play, 0)
            : new BisectEvaluation(BisectStatus.Waiting,
                L.F("{0} gespielt, kein Absturz bisher. Spiele noch etwa {1}, dann ist das Ergebnis belastbar.", Hours(play), Hours(need - play)), p, play, 0);
    }

    private static string Hours(TimeSpan t) =>
        t.TotalMinutes < 90 ? L.F("{0:F0} Min.", Math.Max(0, t.TotalMinutes)) : L.F("{0:F1} Std.", t.TotalHours);
}

public static class BisectStore
{
    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameDoctor", "bisect.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static BisectSession? Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<BisectSession>(File.ReadAllText(FilePath), Json); }
        catch { /* defekt: wie „keine Suche“ behandeln */ }
        return null;
    }

    public static void Save(BisectSession s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(s, Json));
    }

    public static void Clear()
    {
        try { File.Delete(FilePath); } catch { /* egal */ }
    }
}
