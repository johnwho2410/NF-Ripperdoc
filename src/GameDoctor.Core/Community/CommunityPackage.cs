using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameDoctor.Core.Analysis;

namespace GameDoctor.Core.Community;

/// <summary>
/// Stufe 1 (anonym): „Bei Absturzstelle X war Mod Y installiert.“ Jede Meldung steht für sich –
/// sie hat einen eigenen Doppelzähl-Schlüssel und lässt sich nicht mit anderen Meldungen desselben Spielers verknüpfen.
/// </summary>
public sealed record ModPairRecord(int Schema, string GameBuild, string Signature, int NexusModId, string? ModVersion, string DedupKey);

/// <summary>
/// Stufe 1 (anonym): „Mod Y ist installiert.“ – für den Vergleichswert, wie verbreitet eine Mod überhaupt ist.
/// Ebenfalls einzeln und unverknüpft.
/// </summary>
public sealed record ModCensusRecord(int Schema, string? Patch, int NexusModId, string? ModVersion, string DedupKey);

/// <summary>
/// Stufe 1 (anonym): „Bei Absturzstelle X hat Maßnahme Y (z. B. Mod deaktivieren) geholfen / nicht geholfen.“
/// Nur wenn die Maßnahme eine öffentliche Mod betrifft (Nexus-ID) und das Ergebnis belastbar ist.
/// Notizen, Zeitpunkte und Spielzeiten werden nie geteilt.
/// </summary>
public sealed record FixOutcomeRecord(int Schema, string GameBuild, string Signature, string Action, int? NexusModId, string? ModuleFile, string Outcome, string Evidence, string DedupKey);

/// <summary>Alles, was Stufe 1 teilen würde – in zufälliger Reihenfolge, ohne gemeinsame Kennzeichen.</summary>
public sealed class CommunityPackage
{
    public List<CrashFingerprint> Fingerprints { get; } = [];
    public List<ModPairRecord> Pairs { get; } = [];
    public List<ModCensusRecord> Census { get; } = [];
    public List<FixOutcomeRecord> FixOutcomes { get; } = [];
    public int BlockedByGuard { get; set; }
    public int UnusableCrashes { get; set; }
    public List<PrivacyGuard.Violation> Violations { get; } = [];

    public static CommunityPackage Build(AnalysisResult r, FingerprintBuilder builder, ModInventory inventory, byte[] secret,
        IEnumerable<(FixAttempt Attempt, FixEvaluation Evaluation)>? fixes = null)
    {
        var pkg = new CommunityPackage();

        // 1) Fingerabdrücke – pro Absturzstelle nur einer (Doppelzähl-Schutz)
        foreach (var c in r.Crashes)
        {
            var fp = builder.TryBuild(c, r, out var v);
            if (fp is null)
            {
                if (v.Count > 0) { pkg.BlockedByGuard++; pkg.Violations.AddRange(v); }
                else pkg.UnusableCrashes++;
                continue;
            }
            if (pkg.Fingerprints.All(x => x.DedupKey != fp.DedupKey)) pkg.Fingerprints.Add(fp);
        }

        // 2) Paar-Meldungen: jede Absturzstelle × jede öffentliche Mod, jede mit eigenem Schlüssel
        foreach (var fp in pkg.Fingerprints)
            foreach (var mod in inventory.Mods)
                pkg.Pairs.Add(new ModPairRecord(1, fp.GameBuild, fp.Signature, mod.NexusId, mod.Version,
                    CommunityKeys.Derive(secret, $"pair|{fp.GameBuild}|{fp.Signature}|{mod.NexusId}")));

        // 3) Verbreitungs-Meldungen: jede öffentliche Mod einzeln
        var patch = r.Crashes.FirstOrDefault()?.Report.Patch;
        foreach (var mod in inventory.Mods)
            pkg.Census.Add(new ModCensusRecord(1, patch, mod.NexusId, mod.Version,
                CommunityKeys.Derive(secret, $"census|{mod.NexusId}")));

        // 4) Ergebnisse von Lösungsversuchen – nur öffentliche Mods, nur belastbare Ergebnisse, je Absturzstelle eine Meldung
        // DLL-Namen, die in geteilten Fingerabdrücken ohnehin als „Mod auf dem Stack“ vorkommen
        var stackFiles = pkg.Fingerprints.SelectMany(f => f.ModulesOnStack).Select(m => m.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (a, ev) in fixes ?? [])
        {
            if (a.GameBuild is null || ev.ShareableOutcome(a) is not { } outcome) continue;
            // Ziel muss öffentlich bekannt sein: Nexus-ID oder eine DLL, die im Aufrufpfad eines geteilten Absturzes lag
            var file = a.NexusModId is null && a.ModuleFile is { } mf && stackFiles.Contains(mf) ? mf : null;
            if (a.NexusModId is null && file is null) continue;
            var target = a.NexusModId?.ToString() ?? file!.ToLowerInvariant();
            var evidence = ev.Status is FixStatus.LikelyHelped or FixStatus.NotHelped ? "Auto" : "User";
            foreach (var sig in a.Signatures.Distinct())
                pkg.FixOutcomes.Add(new FixOutcomeRecord(1, a.GameBuild, sig, a.Action.ToString(), a.NexusModId, file, outcome, evidence,
                    CommunityKeys.Derive(secret, $"fix|{a.GameBuild}|{sig}|{a.Action}|{target}")));
        }

        // Letzte Prüfung über alle Einzelmeldungen; auffällige werden entfernt
        var versions = inventory.Mods.Select(m => m.Version).Concat(pkg.Fingerprints.Select(f => f.GameBuild))
            .Concat(pkg.FixOutcomes.Select(f => f.GameBuild)).OfType<string>().ToList();
        pkg.Pairs.RemoveAll(p => Guard(pkg, p, versions));
        pkg.Census.RemoveAll(c => Guard(pkg, c, versions));
        pkg.FixOutcomes.RemoveAll(f => Guard(pkg, f, versions));

        // Zufällige Reihenfolge: nichts verrät, welche Meldungen zusammen entstanden sind
        Shuffle(pkg.Fingerprints);
        Shuffle(pkg.Pairs);
        Shuffle(pkg.Census);
        Shuffle(pkg.FixOutcomes);
        return pkg;
    }

    private static bool Guard<T>(CommunityPackage pkg, T record, List<string> versions)
    {
        var v = PrivacyGuard.Check(JsonSerializer.Serialize(record), versions);
        if (v.Count == 0) return false;
        pkg.BlockedByGuard++;
        pkg.Violations.AddRange(v);
        return true;
    }

    private static void Shuffle<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = RandomNumberGenerator.GetInt32(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}

/// <summary>Leitet aus dem lokalen Geheimnis kurze, nicht umkehrbare Doppelzähl-Schlüssel ab.</summary>
public static class CommunityKeys
{
    public static string Derive(byte[] secret, string context)
    {
        using var hmac = new HMACSHA256(secret);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(context));
        return Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
    }
}
