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
    public static List<FixSuggestion> For(CrashCase crash, AnalysisResult r, ModInventory inventory, ModuleClassifier classifier)
    {
        var list = new List<FixSuggestion>();

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

        return list.GroupBy(s => (s.NexusModId, s.TargetLabel)).Select(g => g.First()).ToList();
    }
}
