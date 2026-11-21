using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameDoctor.Core.Analysis;

namespace GameDoctor.Core.Games.Cyberpunk;

public sealed record PlaySession(DateTime Start, DateTime End)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>
/// Dauerhaftes Protokoll der Spielsitzungen – Grundlage, um „X Stunden ohne Absturz“ belastbar zu messen.
/// RED4ext behält nur seine letzten Logs; GameDoctor sammelt die Sitzungen deshalb bei jeder Analyse ein und
/// speichert sie unter %LOCALAPPDATA%\GameDoctor\sessions.json (bleibt lokal, wird nie geteilt).
///
/// Quellen:
/// - RED4ext-Logs „red4ext-JJJJ-MM-TT-hh-mm-ss.log“: Start = Name, Ende = letzte Änderung
/// - Crashreports: Ende = Absturzzeit, Start = Absturzzeit − Laufzeit
/// </summary>
public sealed class PlaySessionLedger
{
    private static readonly TimeSpan MaxSession = TimeSpan.FromHours(14);
    private static readonly Regex Red4ExtLog = new(@"red4ext-(\d{4}-\d{2}-\d{2}-\d{2}-\d{2}-\d{2})\.log$", RegexOptions.Compiled);

    public List<PlaySession> Sessions { get; init; } = [];

    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameDoctor", "sessions.json");

    public static PlaySessionLedger Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<PlaySessionLedger>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* defekt – neu anfangen */ }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }

    /// <summary>Neue Sitzungen aus RED4ext-Logs und Crashreports aufnehmen und überlappende zusammenführen.</summary>
    public void Harvest(CyberpunkInstall? install, IEnumerable<CrashCase> crashes)
    {
        var found = new List<PlaySession>();

        var logDir = install is null ? null : Path.Combine(install.Red4ExtDir, "logs");
        if (logDir is not null && Directory.Exists(logDir))
        {
            foreach (var f in Directory.EnumerateFiles(logDir, "red4ext-*.log"))
            {
                var m = Red4ExtLog.Match(f);
                if (!m.Success || !DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
                    continue;
                var end = File.GetLastWriteTime(f);
                if (end > start) found.Add(new PlaySession(start, end));
            }
        }

        foreach (var c in crashes)
            if (c.Report.UptimeSeconds is int up and > 0)
                found.Add(new PlaySession(c.Report.Time.AddSeconds(-up), c.Report.Time));

        Sessions.AddRange(found.Where(s => s.Duration > TimeSpan.Zero && s.Duration <= MaxSession));
        Normalize();
    }

    private void Normalize()
    {
        var merged = new List<PlaySession>();
        foreach (var s in Sessions.OrderBy(s => s.Start))
        {
            if (merged.Count > 0 && s.Start <= merged[^1].End)
            {
                var last = merged[^1];
                merged[^1] = last with { End = s.End > last.End ? s.End : last.End };
            }
            else merged.Add(s);
        }
        Sessions.Clear();
        Sessions.AddRange(merged);
    }

    /// <summary>Gespielte Zeit im Zeitraum [from, to).</summary>
    public TimeSpan PlayTime(DateTime from, DateTime to)
    {
        var total = TimeSpan.Zero;
        foreach (var s in Sessions)
        {
            var a = s.Start > from ? s.Start : from;
            var b = s.End < to ? s.End : to;
            if (b > a) total += b - a;
        }
        return total;
    }
}
