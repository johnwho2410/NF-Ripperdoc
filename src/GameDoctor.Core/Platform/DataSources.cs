using GameDoctor.Core.Analysis;
using GameDoctor.Core.Community;
using GameDoctor.Core.Games.Cyberpunk;

namespace GameDoctor.Core.Platform;

public enum DataSourceStatus
{
    Ok,
    /// <summary>Gefunden, aber unvollständig (z. B. Mod-Liste ohne Nexus-IDs).</summary>
    Partial,
    Missing,
    /// <summary>Reine Information, kein Problem.</summary>
    Info,
}

/// <summary>Eine Datenquelle mit Status – damit sichtbar ist, worauf eine Analyse beruht und was fehlt.</summary>
public sealed record DataSource(string Name, DataSourceStatus Status, string Detail, string? Hint = null);

public static class DataSourceReport
{
    public static List<DataSource> Build(AnalysisResult r)
    {
        var list = new List<DataSource>();
        var inst = r.Install;

        // Spielordner
        if (inst is not null)
            list.Add(new DataSource("Spielordner", DataSourceStatus.Ok,
                $"{inst.Root} · {inst.Store} · gefunden über: {inst.FoundVia}"));
        else
            list.Add(new DataSource("Spielordner", DataSourceStatus.Missing,
                "Cyberpunk 2077 wurde nicht gefunden. Geprüft: " +
                (r.LocateProbes.Count > 0 ? string.Join(", ", r.LocateProbes.Select(p => p.Source).Distinct()) : "Steam, GOG, Epic, Xbox"),
                "Spielordner festlegen oder „Suchen“ – oder das Spiel starten, dann erkennt GameDoctor den Ordner automatisch."));

        // Crashreports
        var queue = RedCrashReport.DefaultQueueDir;
        if (!Directory.Exists(queue))
            list.Add(new DataSource("Crashreports", DataSourceStatus.Missing,
                "Der Ordner der REDEngine-Crashreports existiert nicht.",
                "Entweder ist das Spiel noch nie abgestürzt, oder der Crash-Reporter ist deaktiviert (z. B. durch ein Startargument)."));
        else
        {
            int withDump = r.Crashes.Count(c => c.Dump is { Error: null });
            list.Add(new DataSource("Crashreports", r.Crashes.Count == 0 || withDump == r.Crashes.Count ? DataSourceStatus.Ok : DataSourceStatus.Partial,
                $"{r.Crashes.Count} im Zeitraum, davon {withDump} mit auswertbarem Speicherabbild",
                withDump < r.Crashes.Count ? "Ohne Speicherabbild (Dump) lässt sich die Absturzstelle nicht bestimmen." : null));
        }

        // Spieleinstellungen
        var settings = CyberpunkSettings.DefaultPath;
        list.Add(File.Exists(settings)
            ? new DataSource("Spieleinstellungen", DataSourceStatus.Ok, settings)
            : new DataSource("Spieleinstellungen", DataSourceStatus.Missing, "UserSettings.json nicht gefunden.",
                inst?.Store == "Xbox / Game Pass"
                    ? "Die Game-Pass-Version speichert ihre Einstellungen an einem anderen Ort – Grafiktipps fallen dann allgemeiner aus."
                    : "Einmal das Spiel starten und die Einstellungen öffnen, dann legt es die Datei an."));

        // Mod-Manager / Mod-Liste
        var inv = r.Inventory;
        if (inst is not null)
        {
            if (inv.Manager is null)
                list.Add(new DataSource("Mod-Liste", DataSourceStatus.Partial,
                    "Kein Mod-Manager erkannt (Vortex oder Mod Organizer 2) – Mods werden nur gezählt.",
                    "Mit Mod Organizer 2: die MO2-Instanz festlegen. Manuell installierte Mods haben keine Nexus-IDs und fließen nicht in Community-Daten ein."));
            else
                list.Add(new DataSource("Mod-Liste", DataSourceStatus.Ok,
                    $"{inv.Manager}: {inv.Mods.Count} Mods mit Nexus-ID, {inv.PackagesWithoutId} ohne · {inv.ManagerPath}"
                    + (inv.ExtraRoots.Count > 0 ? $" · {inv.ExtraRoots.Count} virtuelle Mod-Ordner" : "")));
            foreach (var note in inv.Notes.Where(n => !n.StartsWith("Kein Mod-Manager", StringComparison.Ordinal)))
                list.Add(new DataSource("Mod-Liste", DataSourceStatus.Partial, note));
            if (inv.RedMods.Count > 0)
                list.Add(new DataSource("REDmod", DataSourceStatus.Info, $"{inv.RedMods.Count} offizielle Mods im Ordner „mods“"));
        }

        // Mod-Logs
        if (inst is not null && r.ModLogs is { } logs)
        {
            void Log(string name, string fileHint, string missingHint)
            {
                var file = logs.ScannedFiles.FirstOrDefault(f => Path.GetFileName(f).Contains(fileHint, StringComparison.OrdinalIgnoreCase));
                list.Add(file is not null
                    ? new DataSource(name, DataSourceStatus.Ok, $"{file} (Stand {File.GetLastWriteTime(file):dd.MM. HH:mm})")
                    : new DataSource(name, DataSourceStatus.Info, "kein Log gefunden", missingHint));
            }
            Log("RED4ext-Log", "red4ext-", "RED4ext nicht installiert – oder das Spiel wurde seit der Installation nicht gestartet.");
            Log("CET-Log", "cyber_engine_tweaks", "Cyber Engine Tweaks nicht installiert.");
            Log("redscript-Log", "redscript", "redscript nicht installiert.");
            Log("ArchiveXL-Log", "ArchiveXL", "ArchiveXL nicht installiert.");
        }

        // Windows
        list.Add(new DataSource("Windows-Ereignisanzeige", DataSourceStatus.Info,
            $"{r.SystemEvents.Count} relevante Ereignisse im Zeitraum (Programmabstürze, Treiber-Timeouts, Hardwarefehler, Neustarts)"));
        return list;
    }
}
