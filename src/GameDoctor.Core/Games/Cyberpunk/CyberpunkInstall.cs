using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameDoctor.Core.Platform;
using Microsoft.Win32;

namespace GameDoctor.Core.Games.Cyberpunk;

/// <summary>Ein geprüfter Fundort bei der Suche nach dem Spiel – für die Anzeige „Datenquellen“.</summary>
public sealed record LocateProbe(string Source, string? Path, bool Found, string? Note = null);

/// <summary>
/// Findet die Cyberpunk-Installation und liefert wichtige Pfade.
///
/// Reihenfolge (erste gültige gewinnt):
/// 1. vom Nutzer festgelegter Ordner
/// 2. laufendes Spiel (Pfad der Cyberpunk2077.exe)
/// 3. Steam, GOG, Epic, Xbox/Game Pass
/// 4. zuletzt erfolgreich gefundener Ordner
/// Der PC wird dabei nicht durchsucht – nur bekannte Fundorte. Eine Laufwerkssuche gibt es nur auf ausdrücklichen
/// Wunsch (<see cref="SearchDrives"/>).
/// </summary>
public sealed class CyberpunkInstall
{
    public string Root { get; }
    public string Store { get; }
    /// <summary>Wie der Ordner gefunden wurde, z. B. „Festgelegt“, „Laufendes Spiel“, „Steam“.</summary>
    public string FoundVia { get; }

    private CyberpunkInstall(string root, string store, string foundVia)
    {
        Root = root.TrimEnd('\\', '/');
        Store = store;
        FoundVia = foundVia;
    }

    public string BinDir => Path.Combine(Root, "bin", "x64");
    public string ExePath => Path.Combine(BinDir, "Cyberpunk2077.exe");
    public string Red4ExtDir => Path.Combine(Root, "red4ext");
    public string CetDir => Path.Combine(BinDir, "plugins", "cyber_engine_tweaks");
    public string RedscriptLogDir => Path.Combine(Root, "r6", "logs");
    public string ArchiveModDir => Path.Combine(Root, "archive", "pc", "mod");
    public string RedscriptDir => Path.Combine(Root, "r6", "scripts");
    public string RedModDir => Path.Combine(Root, "mods");

    public string? GameVersion =>
        File.Exists(ExePath) ? FileVersionInfo.GetVersionInfo(ExePath).FileVersion : null;

    public static bool IsGameFolder(string? root) =>
        !string.IsNullOrWhiteSpace(root) && File.Exists(Path.Combine(root, "bin", "x64", "Cyberpunk2077.exe"));

    public static CyberpunkInstall? FromPath(string root, string foundVia = "Festgelegt") =>
        IsGameFolder(root) ? new CyberpunkInstall(root, InferStore(root), foundVia) : null;

    public static CyberpunkInstall? Locate() => Locate(PathSettings.Load(), out _);

    public static CyberpunkInstall? Locate(PathSettings settings, out List<LocateProbe> probes)
    {
        probes = [];
        foreach (var (source, path) in Candidates(settings, probes))
        {
            bool ok = false;
            try { ok = IsGameFolder(path); } catch { /* Laufwerk nicht verfügbar o. ä. */ }
            probes.Add(new LocateProbe(source, path, ok, ok ? null : "kein Cyberpunk2077.exe gefunden"));
            if (!ok) continue;

            var install = new CyberpunkInstall(path, InferStore(path), source);
            if (!string.Equals(settings.LastKnownGamePath, install.Root, StringComparison.OrdinalIgnoreCase))
            {
                settings.LastKnownGamePath = install.Root;
                settings.Save();
            }
            return install;
        }
        return null;
    }

    private static IEnumerable<(string Source, string Path)> Candidates(PathSettings settings, List<LocateProbe> probes)
    {
        // 1) Festgelegt
        if (!string.IsNullOrWhiteSpace(settings.GamePath))
            yield return ("Festgelegt", settings.GamePath);

        // 2) Laufendes Spiel
        foreach (var p in Process.GetProcessesByName("Cyberpunk2077"))
        {
            string? exe = null;
            using (p)
            {
                try { exe = p.MainModule?.FileName; } catch { /* z. B. mit Adminrechten gestartet */ }
            }
            if (exe is null)
            {
                probes.Add(new LocateProbe("Laufendes Spiel", null, false, "Spiel läuft, aber der Pfad ist nicht lesbar (Spiel mit Adminrechten gestartet?)"));
                continue;
            }
            // …\bin\x64\Cyberpunk2077.exe → Spielordner
            var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(exe)));
            if (root is not null) yield return ("Laufendes Spiel", root);
        }

        // 3a) Steam: alle Bibliotheksordner aus libraryfolders.vdf
        var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (steam is not null)
        {
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            var libraries = new List<string> { steam };
            if (File.Exists(vdf))
                foreach (Match m in Regex.Matches(SafeRead(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            foreach (var lib in libraries.Select(l => l.Replace('/', '\\')).Distinct(StringComparer.OrdinalIgnoreCase))
                yield return ("Steam", Path.Combine(lib, "steamapps", "common", "Cyberpunk 2077"));
        }
        else probes.Add(new LocateProbe("Steam", null, false, "Steam nicht installiert"));

        // 3b) GOG (32- und 64-Bit-Registry)
        foreach (var keyPath in new[] { @"SOFTWARE\WOW6432Node\GOG.com\Games", @"SOFTWARE\GOG.com\Games" })
        {
            using var gog = Registry.LocalMachine.OpenSubKey(keyPath);
            if (gog is null) continue;
            foreach (var id in gog.GetSubKeyNames())
            {
                using var k = gog.OpenSubKey(id);
                if (k is not null
                    && (k.GetValue("gameName") as string)?.Contains("Cyberpunk", StringComparison.OrdinalIgnoreCase) == true
                    && k.GetValue("path") is string p)
                    yield return ("GOG", p);
            }
        }

        // 3c) Epic
        var epic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (Directory.Exists(epic))
        {
            foreach (var item in Directory.EnumerateFiles(epic, "*.item"))
            {
                string? loc = null;
                try
                {
                    using var doc = JsonDocument.Parse(SafeRead(item));
                    if (doc.RootElement.TryGetProperty("DisplayName", out var dn)
                        && dn.GetString()?.Contains("Cyberpunk", StringComparison.OrdinalIgnoreCase) == true
                        && doc.RootElement.TryGetProperty("InstallLocation", out var il))
                        loc = il.GetString();
                }
                catch { /* defektes Manifest ignorieren */ }
                if (loc is not null) yield return ("Epic", loc);
            }
        }

        // 3d) Xbox-App / Game Pass: <Laufwerk>\XboxGames\<Spiel>\Content
        foreach (var drive in FixedDrives())
        {
            var xbox = Path.Combine(drive, "XboxGames");
            if (!Directory.Exists(xbox)) continue;
            IEnumerable<string> games;
            try { games = Directory.EnumerateDirectories(xbox, "*Cyberpunk*").ToList(); } catch { continue; }
            foreach (var g in games)
                yield return ("Xbox / Game Pass", Path.Combine(g, "Content"));
        }

        // 4) Zuletzt gefunden
        if (!string.IsNullOrWhiteSpace(settings.LastKnownGamePath))
            yield return ("Zuletzt gefunden", settings.LastKnownGamePath);
    }

    /// <summary>
    /// Ausdrückliche Suche auf allen lokalen Laufwerken (nur auf Klick des Nutzers). Durchsucht nur Ordnernamen bis
    /// Tiefe 5, überspringt Windows- und Systemordner und bricht nach der Zeitvorgabe ab.
    /// </summary>
    public static List<string> SearchDrives(TimeSpan timeout, CancellationToken ct = default, Action<string>? progress = null)
    {
        var found = new List<string>();
        var deadline = DateTime.UtcNow + timeout;
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Windows", "$Recycle.Bin", "System Volume Information", "Recovery", "ProgramData", "PerfLogs",
            "AppData", "node_modules", ".git", "WinSxS", "Config.Msi", "$WinREAgent", "Microsoft",
        };

        // Eine gemeinsame Warteschlange für alle Laufwerke, vielversprechende Ordnernamen zuerst
        // (sonst verbraucht ein großes Laufwerk C: die ganze Zeit, bevor das Spiel-Laufwerk drankommt).
        var queue = new PriorityQueue<(string Dir, int Depth), int>();
        foreach (var drive in FixedDrives()) queue.Enqueue((drive, 0), 0);

        while (queue.Count > 0 && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var (dir, depth) = queue.Dequeue();
            if (IsGameFolder(dir))
            {
                found.Add(dir.TrimEnd('\\'));
                // Nach dem ersten Fund nur noch kurz nach weiteren Installationen suchen
                var shortly = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                if (shortly < deadline) deadline = shortly;
                continue;
            }
            if (depth >= 6) continue;
            if (depth <= 2) progress?.Invoke(dir);
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (skip.Contains(name) || name.StartsWith('.') || name.StartsWith('$')) continue;
                    queue.Enqueue((sub, depth + 1), (depth + 1) * 10 - Promise(name));
                }
            }
            catch { /* kein Zugriff */ }
        }
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Wie wahrscheinlich liegt unter diesem Ordnernamen ein Spiel? (höher = früher durchsuchen)</summary>
    private static int Promise(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("cyberpunk")) return 100;
        if (n is "steamapps" or "common" or "steamlibrary" or "xboxgames" or "gog games" or "epic games") return 60;
        if (n.Contains("steam") || n.Contains("game") || n.Contains("spiele") || n.Contains("gog") || n.Contains("epic")) return 40;
        if (n.StartsWith("program files")) return 20;
        if (n is "users" or "benutzer") return -20; // riesig, selten der Spielort
        return 0;
    }

    private static IEnumerable<string> FixedDrives()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            bool ok;
            try { ok = d.DriveType == DriveType.Fixed && d.IsReady; } catch { ok = false; }
            if (ok) yield return d.RootDirectory.FullName;
        }
    }

    /// <summary>Aus welchem Shop stammt die Installation? (an typischen Dateien erkannt)</summary>
    private static string InferStore(string root)
    {
        try
        {
            if (File.Exists(Path.Combine(root, "bin", "x64", "steam_api64.dll"))) return "Steam";
            if (Directory.EnumerateFiles(root, "goggame-*.info").Any()) return "GOG";
            if (Directory.Exists(Path.Combine(root, ".egstore"))) return "Epic";
            if (root.Contains(@"\XboxGames\", StringComparison.OrdinalIgnoreCase)
                || File.Exists(Path.Combine(root, "MicrosoftGame.config"))
                || File.Exists(Path.Combine(root, "appxmanifest.xml"))) return "Xbox / Game Pass";
        }
        catch { /* egal */ }
        return "unbekannt";
    }

    private static string SafeRead(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }
}
