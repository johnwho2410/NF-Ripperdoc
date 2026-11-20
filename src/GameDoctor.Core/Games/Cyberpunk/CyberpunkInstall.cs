using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GameDoctor.Core.Games.Cyberpunk;

/// <summary>Findet die Cyberpunk-Installation (Steam, GOG, Epic) und liefert wichtige Pfade.</summary>
public sealed class CyberpunkInstall
{
    public string Root { get; }
    public string Store { get; }

    private CyberpunkInstall(string root, string store)
    {
        Root = root;
        Store = store;
    }

    public string BinDir => Path.Combine(Root, "bin", "x64");
    public string ExePath => Path.Combine(BinDir, "Cyberpunk2077.exe");
    public string Red4ExtDir => Path.Combine(Root, "red4ext");
    public string CetDir => Path.Combine(BinDir, "plugins", "cyber_engine_tweaks");
    public string RedscriptLogDir => Path.Combine(Root, "r6", "logs");
    public string ArchiveModDir => Path.Combine(Root, "archive", "pc", "mod");
    public string RedscriptDir => Path.Combine(Root, "r6", "scripts");

    public string? GameVersion =>
        File.Exists(ExePath) ? System.Diagnostics.FileVersionInfo.GetVersionInfo(ExePath).FileVersion : null;

    public static CyberpunkInstall? FromPath(string root) =>
        File.Exists(Path.Combine(root, "bin", "x64", "Cyberpunk2077.exe")) ? new CyberpunkInstall(root, "Manuell") : null;

    public static CyberpunkInstall? Locate()
    {
        foreach (var (path, store) in Candidates())
        {
            try
            {
                var inst = FromPath(path);
                if (inst is not null) return new CyberpunkInstall(inst.Root, store);
            }
            catch
            {
                // Laufwerk nicht verfügbar etc. – nächster Kandidat
            }
        }
        return null;
    }

    private static IEnumerable<(string Path, string Store)> Candidates()
    {
        // Steam: alle Bibliotheksordner aus libraryfolders.vdf
        var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (steam is not null)
        {
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            var libraries = new List<string> { steam };
            if (File.Exists(vdf))
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            }
            foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
                yield return (Path.Combine(lib, "steamapps", "common", "Cyberpunk 2077"), "Steam");
        }

        // GOG
        using (var gog = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games"))
        {
            if (gog is not null)
            {
                foreach (var id in gog.GetSubKeyNames())
                {
                    using var k = gog.OpenSubKey(id);
                    if (k is not null
                        && (k.GetValue("gameName") as string)?.Contains("Cyberpunk", StringComparison.OrdinalIgnoreCase) == true
                        && k.GetValue("path") is string p)
                        yield return (p, "GOG");
                }
            }
        }

        // Epic
        var epic = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
        if (Directory.Exists(epic))
        {
            foreach (var item in Directory.EnumerateFiles(epic, "*.item"))
            {
                string? loc = null;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(item));
                    if (doc.RootElement.TryGetProperty("DisplayName", out var dn)
                        && dn.GetString()?.Contains("Cyberpunk", StringComparison.OrdinalIgnoreCase) == true
                        && doc.RootElement.TryGetProperty("InstallLocation", out var il))
                        loc = il.GetString();
                }
                catch { /* defektes Manifest ignorieren */ }
                if (loc is not null) yield return (loc, "Epic");
            }
        }
    }
}
