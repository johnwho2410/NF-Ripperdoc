using GameDoctor.Core.Analysis;
using GameDoctor.Core.Community;
using GameDoctor.Core.Games.Cyberpunk;
using GameDoctor.Core.Localization;

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
    // Fundorte/Shops werden intern auf Deutsch geführt und erst bei der Anzeige übersetzt.
    // i18n: "Festgelegt"
    // i18n: "Festgelegt (Kommandozeile)"
    // i18n: "Laufendes Spiel"
    // i18n: "Zuletzt gefunden"
    // i18n: "unbekannt"
    public static List<DataSource> Build(AnalysisResult r)
    {
        var list = new List<DataSource>();
        var inst = r.Install;

        // Spielordner
        if (inst is not null)
            list.Add(new DataSource(L.T("Spielordner"), DataSourceStatus.Ok,
                L.F("{0} · {1} · gefunden über: {2}", inst.Root, L.T(inst.Store), L.T(inst.FoundVia))));
        else
            list.Add(new DataSource(L.T("Spielordner"), DataSourceStatus.Missing,
                L.F("Cyberpunk 2077 wurde nicht gefunden. Geprüft: {0}",
                    r.LocateProbes.Count > 0 ? string.Join(", ", r.LocateProbes.Select(p => L.T(p.Source)).Distinct()) : "Steam, GOG, Epic, Xbox"),
                L.T("Spielordner festlegen oder „Suchen“ – oder das Spiel starten, dann erkennt GameDoctor den Ordner automatisch.")));

        // Crashreports
        var queue = RedCrashReport.DefaultQueueDir;
        if (!Directory.Exists(queue))
            list.Add(new DataSource(L.T("Crashreports"), DataSourceStatus.Missing,
                L.T("Der Ordner der REDEngine-Crashreports existiert nicht."),
                L.T("Entweder ist das Spiel noch nie abgestürzt, oder der Crash-Reporter ist deaktiviert (z. B. durch ein Startargument).")));
        else
        {
            int withDump = r.Crashes.Count(c => c.Dump is { Error: null });
            list.Add(new DataSource(L.T("Crashreports"), r.Crashes.Count == 0 || withDump == r.Crashes.Count ? DataSourceStatus.Ok : DataSourceStatus.Partial,
                L.F("{0} im Zeitraum, davon {1} mit auswertbarem Speicherabbild", r.Crashes.Count, withDump),
                withDump < r.Crashes.Count ? L.T("Ohne Speicherabbild (Dump) lässt sich die Absturzstelle nicht bestimmen.") : null));
        }

        // Spieleinstellungen
        var settings = CyberpunkSettings.DefaultPath;
        list.Add(File.Exists(settings)
            ? new DataSource(L.T("Spieleinstellungen"), DataSourceStatus.Ok, settings)
            : new DataSource(L.T("Spieleinstellungen"), DataSourceStatus.Missing, L.T("UserSettings.json nicht gefunden."),
                inst?.Store == "Xbox / Game Pass"
                    ? L.T("Die Game-Pass-Version speichert ihre Einstellungen an einem anderen Ort – Grafiktipps fallen dann allgemeiner aus.")
                    : L.T("Einmal das Spiel starten und die Einstellungen öffnen, dann legt es die Datei an.")));

        // Mod-Manager / Mod-Liste
        var inv = r.Inventory;
        if (inst is not null)
        {
            if (inv.Manager is null)
                list.Add(new DataSource(L.T("Mod-Liste"), DataSourceStatus.Partial,
                    L.T("Kein Mod-Manager erkannt (Vortex oder Mod Organizer 2) – Mods werden nur gezählt."),
                    L.T("Mit Mod Organizer 2: die MO2-Instanz festlegen. Manuell installierte Mods haben keine Nexus-IDs und fließen nicht in Community-Daten ein.")));
            else
                list.Add(new DataSource(L.T("Mod-Liste"), DataSourceStatus.Ok,
                    L.F("{0}: {1} Mods mit Nexus-ID, {2} ohne · {3}", inv.Manager, inv.Mods.Count, inv.PackagesWithoutId, inv.ManagerPath)
                    + (inv.ExtraRoots.Count > 0 ? " · " + L.F("{0} virtuelle Mod-Ordner", inv.ExtraRoots.Count) : "")));
            foreach (var note in inv.Notes.Where(n => n != ModInventory.NoManagerNote))
                list.Add(new DataSource(L.T("Mod-Liste"), DataSourceStatus.Partial, note));
            if (inv.RedMods.Count > 0)
                list.Add(new DataSource("REDmod", DataSourceStatus.Info, L.F("{0} offizielle Mods im Ordner „mods“", inv.RedMods.Count)));
        }

        // Mod-Logs
        if (inst is not null && r.ModLogs is { } logs)
        {
            void Log(string name, string fileHint, string missingHint)
            {
                var file = logs.ScannedFiles.FirstOrDefault(f => Path.GetFileName(f).Contains(fileHint, StringComparison.OrdinalIgnoreCase));
                list.Add(file is not null
                    ? new DataSource(name, DataSourceStatus.Ok, L.F("{0} (Stand {1:g})", file, File.GetLastWriteTime(file)))
                    : new DataSource(name, DataSourceStatus.Info, L.T("kein Log gefunden"), missingHint));
            }
            Log(L.T("RED4ext-Log"), "red4ext-", L.T("RED4ext nicht installiert – oder das Spiel wurde seit der Installation nicht gestartet."));
            Log(L.T("CET-Log"), "cyber_engine_tweaks", L.T("Cyber Engine Tweaks nicht installiert."));
            Log(L.T("redscript-Log"), "redscript", L.T("redscript nicht installiert."));
            Log(L.T("ArchiveXL-Log"), "ArchiveXL", L.T("ArchiveXL nicht installiert."));
        }

        // Windows
        list.Add(new DataSource(L.T("Windows-Ereignisanzeige"), DataSourceStatus.Info,
            L.F("{0} relevante Ereignisse im Zeitraum (Programmabstürze, Treiber-Timeouts, Hardwarefehler, Neustarts)", r.SystemEvents.Count)));
        return list;
    }
}
