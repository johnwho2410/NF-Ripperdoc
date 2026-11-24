using GameDoctor.Core.Analysis;
using GameDoctor.Core.Localization;

namespace GameDoctor.Core.Community;

/// <summary>Ein vorgeschlagener Lösungsversuch, abgeleitet aus den Befunden eines Absturzes.</summary>
public sealed record FixSuggestion(string Label, string Reason, FixAction Action, string TargetLabel, int? NexusModId, IReadOnlyList<string> TargetFiles)
{
    public string? ModuleFile { get; init; }
}

public static class FixSuggestions
{
    /// <summary>Vorschläge für einen Absturz – stärkste Indizien zuerst.</summary>
    /// <param name="focusConflicts">true = der Spieler kommt von einem Mod-Konflikt: diese Mods werden zuerst vorgeschlagen.</param>
    public static List<FixSuggestion> For(CrashCase crash, AnalysisResult r, ModInventory inventory, ModuleClassifier classifier, bool focusConflicts = false)
    {
        var list = new List<FixSuggestion>();

        // 0) Mods, die dieselbe Spielfunktion ersetzen (redscript-Konflikt): nur eine davon kann wirken
        var conflictList = new List<FixSuggestion>();
        foreach (var c in r.ModLogs?.RedscriptConflicts ?? [])
        {
            if (c.Others.Count == 0) continue;
            // Anzeigenamen aus dem Mod-Manager, sonst der Script-Ordner
            string Display(string name, string file) => inventory.ForFile(Path.GetFileName(file))?.DisplayName ?? name;
            foreach (var (name, file) in c.ScriptFiles)
            {
                var mod = inventory.ForFile(Path.GetFileName(file));
                var label = mod?.DisplayName ?? name;
                var others = string.Join(", ", c.ScriptFiles.Where(k => !k.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(k => $"„{Display(k.Key, k.Value)}“"));
                var files = mod is not null ? inventory.FilesOf(mod.NexusId) : [Path.GetFileName(file)];
                conflictList.Add(new FixSuggestion(L.F("„{0}“ deaktivieren", label),
                    L.F("Ersetzt dieselbe Spielfunktion wie {0} – nur eine der Mods kann wirken. Teste, ob es besser läuft, wenn nur eine davon aktiv ist.", others),
                    FixAction.DisableMod, label, mod?.NexusId, files.Count > 0 ? files : [Path.GetFileName(file)]));
            }
        }
        if (focusConflicts) list.AddRange(conflictList);

        // 1) Mods im Aufrufpfad dieses Absturzes
        if (crash.Dump is { Error: null } d)
        {
            var sameSig = r.Crashes.Where(c => c.Signature == crash.Signature).ToList();
            foreach (var g in d.StackHits.Take(40).Select(h => h.Module)
                         .Where(m => classifier.Classify(m) == ModuleKind.Mod)
                         .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            {
                var module = g.First();
                var mod = inventory.ForFile(module.Name);
                var label = classifier.FriendlyName(module);
                int coupled = sameSig.Count(c => c.Dump?.StackHits.Any(h => h.Module.Name.Equals(module.Name, StringComparison.OrdinalIgnoreCase)) == true);
                var reason = sameSig.Count >= 2
                    ? L.F("Lag bei {0} von {1} Abstürzen an dieser Stelle im Aufrufpfad.", coupled, sameSig.Count)
                    : L.T("Lag beim Absturz im Aufrufpfad.");
                var files = mod is not null ? inventory.FilesOf(mod.NexusId) : [module.Name];
                list.Add(new FixSuggestion(L.F("„{0}“ deaktivieren", label), reason, FixAction.DisableMod,
                    mod?.DisplayName ?? label, mod?.NexusId, files.Count > 0 ? files : [module.Name])
                { ModuleFile = module.Name });
            }
        }

        // 2) Mods, deren Inhalte das Spiel nicht findet (z. B. Garagen ohne geladene Orte)
        foreach (var u in r.ModLogs?.UnresolvedContent ?? [])
        {
            var token = u.Owner.ToLowerInvariant().TrimEnd('s');
            var mods = inventory.Mods.Where(m => inventory.FilesOf(m.NexusId)
                .Any(f => f.ToLowerInvariant().Replace(" ", "_").Contains(token))).ToList();
            foreach (var mod in mods.Take(2))
                list.Add(new FixSuggestion(L.F("„{0}“ deaktivieren", mod.DisplayName),
                    L.F("{0} Inhalte dieser Mod („{1}“) werden vom Spiel nicht gefunden.", u.Items.Count, u.Owner),
                    FixAction.DisableMod, mod.DisplayName, mod.NexusId, inventory.FilesOf(mod.NexusId)));
        }

        if (!focusConflicts) list.AddRange(conflictList);

        return list.GroupBy(s => (s.NexusModId, s.TargetLabel)).Select(g => g.First()).ToList();
    }
}
