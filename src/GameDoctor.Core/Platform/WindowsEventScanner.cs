using System.Diagnostics.Eventing.Reader;
using GameDoctor.Core.Localization;

namespace GameDoctor.Core.Platform;

public enum SystemEventKind
{
    AppCrash,       // Application Error 1000
    GpuTimeout,     // Display 4101 (TDR) / nvlddmkm
    Whea,           // Hardwarefehler (CPU/RAM/PCIe)
    UnexpectedShutdown, // Kernel-Power 41
}

public sealed record SystemEvent(DateTime Time, SystemEventKind Kind, string Summary, string? Module = null, string? ExceptionCode = null, string? Offset = null);

/// <summary>Liest relevante Ereignisse aus der Windows-Ereignisanzeige.</summary>
public static class WindowsEventScanner
{
    public static List<SystemEvent> Scan(string processName, TimeSpan window)
    {
        var events = new List<SystemEvent>();
        long ms = (long)window.TotalMilliseconds;

        // Programmabstürze des Spiels
        Query("Application",
            $"*[System[Provider[@Name='Application Error'] and EventID=1000 and TimeCreated[timediff(@SystemTime) <= {ms}]]]",
            rec =>
            {
                var p = rec.Properties;
                if (p.Count < 8) return;
                var app = p[0].Value?.ToString() ?? "";
                if (!app.Equals(processName, StringComparison.OrdinalIgnoreCase)) return;
                var module = p[3].Value?.ToString();
                var code = FormatHex(p[6].Value);
                var offset = FormatHex(p[7].Value);
                events.Add(new SystemEvent(rec.TimeCreated ?? DateTime.MinValue, SystemEventKind.AppCrash,
                    L.F("{0} abgestürzt in {1} (Code {2}, Offset {3})", app, module, code, offset), module, code, offset));
            });

        // Grafiktreiber-Timeouts (TDR)
        Query("System",
            $"*[System[(Provider[@Name='Display'] or Provider[@Name='nvlddmkm'] or Provider[@Name='amdkmdag'] or Provider[@Name='amdwddmg']) and TimeCreated[timediff(@SystemTime) <= {ms}]]]",
            rec =>
            {
                string provider = rec.ProviderName;
                if (provider == "Display" && rec.Id != 4101) return;
                events.Add(new SystemEvent(rec.TimeCreated ?? DateTime.MinValue, SystemEventKind.GpuTimeout,
                    L.F("Grafiktreiber-Problem ({0}, Ereignis {1})", provider, rec.Id)));
            });

        // Hardwarefehler
        Query("System",
            $"*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and TimeCreated[timediff(@SystemTime) <= {ms}]]]",
            rec => events.Add(new SystemEvent(rec.TimeCreated ?? DateTime.MinValue, SystemEventKind.Whea,
                L.F("Hardwarefehler gemeldet (WHEA, Ereignis {0}, Stufe {1})", rec.Id, rec.LevelDisplayNameSafe()))));

        // Unerwartete Neustarts
        Query("System",
            $"*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41 and TimeCreated[timediff(@SystemTime) <= {ms}]]]",
            rec => events.Add(new SystemEvent(rec.TimeCreated ?? DateTime.MinValue, SystemEventKind.UnexpectedShutdown,
                L.T("PC wurde unerwartet neu gestartet / ausgeschaltet (Kernel-Power 41)"))));

        return events.OrderByDescending(e => e.Time).ToList();
    }

    private static string LevelDisplayNameSafe(this EventRecord rec)
    {
        try { return rec.LevelDisplayName ?? rec.Level?.ToString() ?? "?"; }
        catch { return rec.Level?.ToString() ?? "?"; }
    }

    private static string FormatHex(object? v) => v switch
    {
        uint u => $"0x{u:X8}",
        int i => $"0x{i:X8}",
        ulong ul => $"0x{ul:X}",
        long l => $"0x{l:X}",
        string s => s,
        _ => v?.ToString() ?? "?",
    };

    private static void Query(string log, string xpath, Action<EventRecord> handle)
    {
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true });
            int n = 0;
            for (var rec = reader.ReadEvent(); rec is not null && n < 500; rec = reader.ReadEvent(), n++)
            {
                using (rec) handle(rec);
            }
        }
        catch (EventLogException)
        {
            // Log nicht vorhanden oder keine Berechtigung – überspringen
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
