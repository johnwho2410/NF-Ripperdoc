using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameDoctor.Core.Community;

/// <summary>Eine installierte Mod mit öffentlicher Nexus-ID. Name nur zur lokalen Anzeige – geteilt werden nur ID und Version.</summary>
public sealed record InstalledMod(int NexusId, string? Version, string DisplayName);

/// <summary>
/// Liest aus Vortex' <c>vortex.deployment.json</c>, welche Datei im Spielordner zu welchem Mod-Paket gehört,
/// und ermittelt daraus die öffentlichen Nexus-Mod-IDs.
///
/// Datenschutz: Nur Mods mit Nexus-ID werden berücksichtigt. Private, selbstgebaute oder manuell installierte
/// Mods (deren Namen persönliche Angaben enthalten könnten) fallen grundsätzlich heraus. Staging-Pfade,
/// Installationszeiten und die Vortex-Instanz-ID werden gelesen, aber nie weitergegeben.
/// </summary>
public sealed class ModInventory
{
    private readonly Dictionary<string, InstalledMod> _byFile = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, List<string>> _filesById = [];

    /// <summary>Dateinamen (ohne Pfad), die zu dieser Mod gehören.</summary>
    public IReadOnlyList<string> FilesOf(int nexusId) => _filesById.TryGetValue(nexusId, out var l) ? l : [];

    public IReadOnlyList<InstalledMod> Mods { get; private init; } = [];
    public int PackagesWithoutId { get; private init; }
    public bool FromVortex { get; private init; }

    // "02_GYMFIEND_VTK 6423 1.4.3 2026-10-10T19-28Z KRvfRxmhk"   (Vortex-Staging-Name)
    private static readonly Regex VortexStyle = new(@"^(?<name>.*?)[ _](?<id>\d{2,6})[ _](?<ver>\S+)[ _]\d{4}-\d{2}-\d{2}T", RegexOptions.Compiled);
    // "03_GYMFIEND_CLOTHES_VANILLA_DLC_REFITS-6423-1-4-2--1740164776"   (Nexus-Download-Name)
    private static readonly Regex NexusStyle = new(@"^(?<name>.*?)-(?<id>\d{2,6})-(?<ver>[\dA-Za-z-]+?)-+\d{9,10}$", RegexOptions.Compiled);
    private static readonly Regex SafeVersion = new(@"^[0-9A-Za-z][0-9A-Za-z.\-]{0,19}$", RegexOptions.Compiled);

    public static ModInventory Load(string gameRoot)
    {
        var manifest = Path.Combine(gameRoot, "vortex.deployment.json");
        if (!File.Exists(manifest)) return new ModInventory();

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            var bySource = new Dictionary<string, InstalledMod?>(StringComparer.Ordinal);
            var inv = new Dictionary<string, InstalledMod>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in doc.RootElement.GetProperty("files").EnumerateArray())
            {
                var rel = file.GetProperty("relPath").GetString();
                var source = file.GetProperty("source").GetString();
                if (rel is null || source is null) continue;
                if (!bySource.TryGetValue(source, out var mod))
                    bySource[source] = mod = Parse(source);
                if (mod is not null) inv[Path.GetFileName(rel)] = mod;
            }

            var result = new ModInventory
            {
                FromVortex = true,
                Mods = bySource.Values.OfType<InstalledMod>().GroupBy(m => m.NexusId).Select(g => g.First()).OrderBy(m => m.NexusId).ToList(),
                PackagesWithoutId = bySource.Values.Count(m => m is null),
            };
            foreach (var (k, v) in inv)
            {
                result._byFile[k] = v;
                if (!result._filesById.TryGetValue(v.NexusId, out var files)) result._filesById[v.NexusId] = files = [];
                files.Add(k);
            }
            return result;
        }
        catch
        {
            return new ModInventory();
        }
    }

    /// <summary>Zu welcher (öffentlichen) Mod gehört diese Datei? z. B. "Boneware.dll".</summary>
    public InstalledMod? ForFile(string fileName) => _byFile.GetValueOrDefault(Path.GetFileName(fileName));

    private static InstalledMod? Parse(string source)
    {
        var m = VortexStyle.Match(source);
        if (!m.Success) m = NexusStyle.Match(source);
        if (!m.Success || !int.TryParse(m.Groups["id"].Value, out var id) || id <= 0) return null;
        var ver = m.Groups["ver"].Value.Replace('-', '.').Trim('.');
        return new InstalledMod(id, SafeVersion.IsMatch(ver) ? ver : null, m.Groups["name"].Value.Replace('_', ' ').Trim());
    }
}
