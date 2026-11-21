using Microsoft.Win32;

namespace GameDoctor.Core.Platform;

/// <summary>Konfiguration der Windows-Auslagerungsdatei (aus der Registry, ohne Adminrechte lesbar).</summary>
public sealed record PageFileEntry(string Path, bool SystemManaged, int InitialMb, int MaximumMb)
{
    public string Drive => System.IO.Path.GetPathRoot(Path)?.TrimEnd('\\').ToUpperInvariant() ?? "?";

    public double? DriveFreeGb
    {
        get
        {
            try { return new DriveInfo(Drive).AvailableFreeSpace / 1024d / 1024 / 1024; }
            catch { return null; }
        }
    }
}

public sealed class PageFileInfo
{
    public bool FullyAutomatic { get; init; }
    public bool Disabled { get; init; }
    public List<PageFileEntry> Files { get; init; } = [];

    public static PageFileInfo Read()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
            var values = key?.GetValue("PagingFiles") as string[] ?? [];
            var entries = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            if (entries.Count == 0) return new PageFileInfo { Disabled = true };

            // "?:\pagefile.sys" = für alle Laufwerke automatisch verwaltet
            if (entries.Any(e => e.TrimStart().StartsWith("?:", StringComparison.Ordinal)))
                return new PageFileInfo { FullyAutomatic = true };

            var files = new List<PageFileEntry>();
            foreach (var e in entries)
            {
                var parts = e.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int init = parts.Length > 1 && int.TryParse(parts[1], out var i) ? i : 0;
                int max = parts.Length > 2 && int.TryParse(parts[2], out var m) ? m : 0;
                // "0 0" oder ohne Größen = vom System verwaltet (für dieses Laufwerk)
                files.Add(new PageFileEntry(parts[0], init == 0 && max == 0, init, max));
            }
            return new PageFileInfo { Files = files };
        }
        catch
        {
            return new PageFileInfo { FullyAutomatic = true };
        }
    }

    public string Describe()
    {
        if (Disabled) return "Auslagerungsdatei ist deaktiviert";
        if (FullyAutomatic) return "Auslagerungsdatei wird automatisch von Windows verwaltet";
        return string.Join("; ", Files.Select(f => f.SystemManaged
            ? $"{f.Drive} vom System verwaltet"
            : $"{f.Drive} fest: Start {f.InitialMb / 1024.0:F0} GB, maximal {f.MaximumMb / 1024.0:F0} GB"));
    }

    /// <summary>
    /// Kann die Auslagerungsdatei auf diesem Laufwerk (z. B. "C:") wachsen und braucht dort also freien Platz?
    /// Bei vollautomatischer Verwaltung legt Windows sie auf das Systemlaufwerk.
    /// </summary>
    public bool CanGrowOn(string drive, string systemDrive = "C:")
    {
        drive = Norm(drive);
        if (Disabled) return false;
        if (FullyAutomatic) return drive == Norm(systemDrive);
        return Files.Any(f => f.Drive == drive && (f.SystemManaged || f.MaximumMb > f.InitialMb));
    }

    /// <summary>Laufwerke, auf denen eine Auslagerungsdatei liegt (bei vollautomatischer Verwaltung das Systemlaufwerk).</summary>
    public IReadOnlyList<string> Drives(string systemDrive = "C:") =>
        Disabled ? [] : FullyAutomatic ? [Norm(systemDrive)] : Files.Select(f => f.Drive).Distinct().ToList();

    private static string Norm(string drive) => drive.TrimEnd('\\').ToUpperInvariant();

    /// <summary>Startgröße der festen Auslagerungsdatei(en) in MB, 0 bei automatischer Verwaltung.</summary>
    public int FixedInitialMb => FullyAutomatic ? 0 : Files.Where(f => !f.SystemManaged).Sum(f => f.InitialMb);
}
