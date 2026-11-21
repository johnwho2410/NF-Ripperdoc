using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameDoctor.Core.Games.Cyberpunk;
using GameDoctor.Core.Platform;

namespace GameDoctor.Core.Community;

/// <summary>Eine installierte Mod mit öffentlicher Nexus-ID. Name nur zur lokalen Anzeige – geteilt werden nur ID und Version.</summary>
public sealed record InstalledMod(int NexusId, string? Version, string DisplayName);

/// <summary>
/// Welche Mods sind installiert – und woher wissen wir das? Unterstützt:
/// - <b>Vortex</b>: <c>vortex.deployment.json</c> im Spielordner (Datei → Mod-Paket mit Nexus-ID)
/// - <b>Mod Organizer 2</b>: Instanz mit <c>ModOrganizer.ini</c>, aktive Mods aus <c>modlist.txt</c>, Nexus-ID aus <c>meta.ini</c>.
///   Die Mods liegen dort nicht im Spielordner, sondern werden virtuell eingeblendet – ihre Ordner (und „overwrite“)
///   stehen deshalb in <see cref="ExtraRoots"/> und werden wie zusätzliche Spielordner-Ebenen behandelt.
/// - <b>REDmod</b>: offizielle Mods im Ordner <c>mods\</c> (nur Anzahl/Namen, keine Nexus-IDs)
///
/// Datenschutz: Nur Mods mit Nexus-ID gelten als „öffentlich“ und dürfen in Community-Meldungen auftauchen.
/// Staging-Pfade, Installationszeiten, Instanz-IDs und Namen privater Mods werden nie weitergegeben.
/// </summary>
public sealed class ModInventory
{
    private readonly Dictionary<string, InstalledMod> _byFile = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, List<string>> _filesById = [];
    // Name des Mod-Pakets im Mod-Manager je Datei – auch für Mods ohne Nexus-ID. Nur für lokale Anleitungen, nie geteilt.
    private readonly Dictionary<string, string> _packageByFile = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Wie heißt das Mod-Paket dieser Datei im Mod-Manager? (nur lokale Anzeige)</summary>
    public string? PackageNameForFile(string fileName) => _packageByFile.GetValueOrDefault(Path.GetFileName(fileName));

    public IReadOnlyList<InstalledMod> Mods { get; private set; } = [];
    public int PackagesWithoutId { get; private set; }
    /// <summary>„Vortex“, „Mod Organizer 2“ oder null (kein Mod-Manager erkannt).</summary>
    public string? Manager { get; private set; }
    /// <summary>Wo der Mod-Manager gefunden wurde (nur lokale Anzeige).</summary>
    public string? ManagerPath { get; private set; }
    /// <summary>Weitere Ordner mit Spielordner-Struktur (MO2: aktive Mods + overwrite).</summary>
    public IReadOnlyList<string> ExtraRoots { get; private set; } = [];
    public IReadOnlyList<string> RedMods { get; private set; } = [];
    public List<string> Notes { get; } = [];

    public bool FromVortex => Manager == "Vortex";

    /// <summary>Dateinamen (ohne Pfad), die zu dieser Mod gehören.</summary>
    public IReadOnlyList<string> FilesOf(int nexusId) => _filesById.TryGetValue(nexusId, out var l) ? l : [];

    /// <summary>Zu welcher (öffentlichen) Mod gehört diese Datei? z. B. "Boneware.dll".</summary>
    public InstalledMod? ForFile(string fileName) => _byFile.GetValueOrDefault(Path.GetFileName(fileName));

    // ------------------------------------------------------------------ Erkennung

    public static ModInventory Detect(CyberpunkInstall install, PathSettings? settings = null)
    {
        settings ??= PathSettings.Load();
        var inv = new ModInventory();
        try
        {
            inv.RedMods = ReadRedMods(install);

            // MO2 hat Vorrang, wenn eine Instanz für genau diesen Spielordner existiert –
            // ein altes Vortex-Manifest könnte sonst einen veralteten Stand zeigen.
            var mo2 = FindMo2Instance(install, settings);
            if (mo2 is not null)
                inv.LoadMo2(mo2);
            else if (File.Exists(Path.Combine(install.Root, "vortex.deployment.json")))
                inv.LoadVortex(Path.Combine(install.Root, "vortex.deployment.json"));
            else
                inv.Notes.Add("Kein Mod-Manager erkannt (Vortex oder Mod Organizer 2). Mods werden nur gezählt, ohne Nexus-IDs.");
        }
        catch (Exception ex)
        {
            inv.Notes.Add("Mod-Liste konnte nicht gelesen werden: " + ex.Message);
        }
        return inv;
    }

    /// <summary>Nur Vortex (für Tests und Altaufrufe).</summary>
    public static ModInventory Load(string gameRoot)
    {
        var inv = new ModInventory();
        var manifest = Path.Combine(gameRoot, "vortex.deployment.json");
        if (File.Exists(manifest)) inv.LoadVortex(manifest);
        return inv;
    }

    // ------------------------------------------------------------------ Vortex

    // "02_GYMFIEND_VTK 6423 1.4.3 2026-10-10T19-28Z KRvfRxmhk"   (Vortex-Staging-Name)
    private static readonly Regex VortexStyle = new(@"^(?<name>.*?)[ _](?<id>\d{2,6})[ _](?<ver>\S+)[ _]\d{4}-\d{2}-\d{2}T", RegexOptions.Compiled);
    // "03_GYMFIEND_CLOTHES_VANILLA_DLC_REFITS-6423-1-4-2--1740164776"   (Nexus-Download-Name)
    private static readonly Regex NexusStyle = new(@"^(?<name>.*?)-(?<id>\d{2,6})-(?<ver>[\dA-Za-z-]+?)-+\d{9,10}$", RegexOptions.Compiled);
    private static readonly Regex SafeVersion = new(@"^[0-9A-Za-z][0-9A-Za-z.\-]{0,19}$", RegexOptions.Compiled);

    private void LoadVortex(string manifest)
    {
        Manager = "Vortex";
        ManagerPath = manifest;
        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
        var bySource = new Dictionary<string, InstalledMod?>(StringComparer.Ordinal);
        foreach (var file in doc.RootElement.GetProperty("files").EnumerateArray())
        {
            var rel = file.GetProperty("relPath").GetString();
            var source = file.GetProperty("source").GetString();
            if (rel is null || source is null) continue;
            if (!bySource.TryGetValue(source, out var mod))
                bySource[source] = mod = ParseVortexSource(source);
            _packageByFile[Path.GetFileName(rel)] = VortexDisplayName(source);
            if (mod is not null) AddFile(Path.GetFileName(rel), mod);
        }
        Mods = bySource.Values.OfType<InstalledMod>().GroupBy(m => m.NexusId).Select(g => g.First()).OrderBy(m => m.NexusId).ToList();
        PackagesWithoutId = bySource.Values.Count(m => m is null);
    }

    /// <summary>Lesbarer Name aus dem Vortex-Staging-Ordner (ohne Nexus-ID, Version und Zeitstempel).</summary>
    private static string VortexDisplayName(string source)
    {
        var m = VortexStyle.Match(source);
        if (!m.Success) m = NexusStyle.Match(source);
        return (m.Success ? m.Groups["name"].Value : source).Replace('_', ' ').Trim();
    }

    private static InstalledMod? ParseVortexSource(string source)
    {
        var m = VortexStyle.Match(source);
        if (!m.Success) m = NexusStyle.Match(source);
        if (!m.Success || !int.TryParse(m.Groups["id"].Value, out var id) || id <= 0) return null;
        var ver = m.Groups["ver"].Value.Replace('-', '.').Trim('.');
        return new InstalledMod(id, SafeVersion.IsMatch(ver) ? ver : null, m.Groups["name"].Value.Replace('_', ' ').Trim());
    }

    // ------------------------------------------------------------------ Mod Organizer 2

    public sealed record Mo2Instance(string Dir, string ModsDir, string ProfilesDir, string OverwriteDir, string Profile, string? GamePath);

    /// <summary>
    /// Sucht eine MO2-Instanz für diesen Spielordner: festgelegt → laufendes MO2 (portable Instanz) →
    /// globale Instanzen unter %LOCALAPPDATA%\ModOrganizer.
    /// </summary>
    public static Mo2Instance? FindMo2Instance(CyberpunkInstall install, PathSettings settings)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.Mo2InstancePath)) candidates.Add(settings.Mo2InstancePath);

        foreach (var p in Process.GetProcessesByName("ModOrganizer"))
            using (p)
            {
                try { if (Path.GetDirectoryName(p.MainModule?.FileName) is { } d) candidates.Add(d); } catch { /* kein Zugriff */ }
            }

        var global = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModOrganizer");
        if (Directory.Exists(global)) candidates.AddRange(Directory.EnumerateDirectories(global));

        foreach (var dir in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var ini = Path.Combine(dir, "ModOrganizer.ini");
            if (!File.Exists(ini)) continue;
            var inst = ParseMo2Ini(dir, ini);
            bool forced = string.Equals(dir, settings.Mo2InstancePath, StringComparison.OrdinalIgnoreCase);
            bool sameGame = inst.GamePath is not null && string.Equals(inst.GamePath.TrimEnd('\\'), install.Root, StringComparison.OrdinalIgnoreCase);
            if (forced || sameGame) return inst;
        }
        return null;
    }

    private static Mo2Instance ParseMo2Ini(string dir, string ini)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(ini))
        {
            int i = line.IndexOf('=');
            if (i <= 0 || line.StartsWith('[')) continue;
            values.TryAdd(line[..i].Trim(), Mo2Value(line[(i + 1)..]));
        }
        var baseDir = values.TryGetValue("base_directory", out var b) && b.Length > 0 ? b : dir;
        string Resolve(string key, string fallback) =>
            values.TryGetValue(key, out var v) && v.Length > 0 ? v.Replace("%BASE_DIR%", baseDir, StringComparison.OrdinalIgnoreCase) : Path.Combine(baseDir, fallback);

        return new Mo2Instance(dir,
            Resolve("mod_directory", "mods"),
            Resolve("profiles_directory", "profiles"),
            Resolve("overwrite_directory", "overwrite"),
            values.GetValueOrDefault("selected_profile") is { Length: > 0 } prof ? prof : "Default",
            values.GetValueOrDefault("gamePath"));
    }

    /// <summary>MO2 speichert Werte teils als @ByteArray(...) mit doppelten Backslashes.</summary>
    private static string Mo2Value(string raw)
    {
        var v = raw.Trim();
        if (v.StartsWith("@ByteArray(", StringComparison.Ordinal) && v.EndsWith(')')) v = v[11..^1];
        return v.Replace(@"\\", @"\").Replace('/', '\\');
    }

    private void LoadMo2(Mo2Instance inst)
    {
        Manager = "Mod Organizer 2";
        ManagerPath = inst.Dir;
        var modlist = Path.Combine(inst.ProfilesDir, inst.Profile, "modlist.txt");
        if (!File.Exists(modlist))
        {
            Notes.Add($"Mod Organizer 2: Profil „{inst.Profile}“ ohne modlist.txt gefunden.");
            return;
        }

        var enabled = File.ReadLines(modlist)
            .Where(l => l.StartsWith('+'))
            .Select(l => l[1..].Trim())
            .Where(n => n.Length > 0 && !n.EndsWith("_separator", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var roots = new List<string>();
        var mods = new List<InstalledMod>();
        int withoutId = 0;
        foreach (var name in enabled)
        {
            var modDir = Path.Combine(inst.ModsDir, name);
            if (!Directory.Exists(modDir)) continue;
            roots.Add(modDir);

            try
            {
                foreach (var f in Directory.EnumerateFiles(modDir, "*", SearchOption.AllDirectories))
                    _packageByFile[Path.GetFileName(f)] = name;
            }
            catch { /* kein Zugriff */ }

            var mod = ReadMo2Meta(modDir, name);
            if (mod is null) { withoutId++; continue; }
            mods.Add(mod);
            try
            {
                foreach (var f in Directory.EnumerateFiles(modDir, "*", SearchOption.AllDirectories))
                    if (!Path.GetFileName(f).Equals("meta.ini", StringComparison.OrdinalIgnoreCase))
                        AddFile(Path.GetFileName(f), mod);
            }
            catch { /* kein Zugriff */ }
        }
        if (Directory.Exists(inst.OverwriteDir)) roots.Add(inst.OverwriteDir);

        ExtraRoots = roots;
        Mods = mods.GroupBy(m => m.NexusId).Select(g => g.First()).OrderBy(m => m.NexusId).ToList();
        PackagesWithoutId = withoutId;
    }

    private static InstalledMod? ReadMo2Meta(string modDir, string name)
    {
        var meta = Path.Combine(modDir, "meta.ini");
        if (!File.Exists(meta)) return null;
        int id = 0;
        string? version = null;
        foreach (var line in File.ReadLines(meta))
        {
            if (line.StartsWith("modid=", StringComparison.OrdinalIgnoreCase)) int.TryParse(line[6..].Trim(), out id);
            else if (line.StartsWith("version=", StringComparison.OrdinalIgnoreCase)) version = line[8..].Trim().TrimStart('d');
        }
        if (id <= 0) return null;
        return new InstalledMod(id, version is not null && SafeVersion.IsMatch(version) ? version : null, name);
    }

    // ------------------------------------------------------------------ REDmod

    private static List<string> ReadRedMods(CyberpunkInstall install)
    {
        if (!Directory.Exists(install.RedModDir)) return [];
        return Directory.EnumerateDirectories(install.RedModDir)
            .Where(d => File.Exists(Path.Combine(d, "info.json")))
            .Select(d => Path.GetFileName(d)!)
            .ToList();
    }

    private void AddFile(string fileName, InstalledMod mod)
    {
        _byFile[fileName] = mod;
        if (!_filesById.TryGetValue(mod.NexusId, out var files)) _filesById[mod.NexusId] = files = [];
        files.Add(fileName);
    }
}
