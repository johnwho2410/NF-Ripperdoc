using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameDoctor.Core.Games.Cyberpunk;

/// <summary>
/// Ein Absturzbericht des REDEngine-Crashreporters
/// (%LOCALAPPDATA%\REDEngine\ReportQueue\Cyberpunk2077-YYYYMMDD-HHMMSS-PID-TID).
/// </summary>
public sealed class RedCrashReport
{
    public required string Folder { get; init; }
    public required DateTime Time { get; init; }
    public string? ErrorReason { get; init; }
    public string? Expression { get; init; }
    public string? Message { get; init; }
    public string? InternalVersion { get; init; }
    public IReadOnlyDictionary<string, string> Engine { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> LoadedModules { get; init; } = [];
    public string? DumpPath { get; init; }
    public JsonElement? SysInfo { get; init; }

    public string Id => Path.GetFileName(Folder);
    public string? Patch => Engine.GetValueOrDefault("Game/Patch");
    public int? UptimeSeconds => int.TryParse(Engine.GetValueOrDefault("uptimeSeconds"), out var s) ? s : null;
    public string? LoadingStage => Engine.GetValueOrDefault("Game/LoadingStage");
    public bool OutOfMemory => Engine.GetValueOrDefault("Engine/OOM") == "true";
    public int? VramUsedMb => int.TryParse(Engine.GetValueOrDefault("Gpu/Device/UsedMemoryMB"), out var v) ? v : null;
    public int? VramTotalMb => int.TryParse(Engine.GetValueOrDefault("Gpu/Device/TotalMemoryMB"), out var v) ? v : null;
    public string? GpuName => Engine.GetValueOrDefault("Gpu/Device/Name");
    public string? GpuDriver => Engine.GetValueOrDefault("Gpu/Device/DriverVersion");
    public bool? RayTracing => Engine.TryGetValue("Gpu/RayTracing/Enabled", out var r) ? r == "true" : null;

    /// <summary>Kameraposition im Spiel zum Absturzzeitpunkt (x, y, z in Metern); null, wenn unbekannt oder ungültig.</summary>
    public (double X, double Y, double Z)? Position
    {
        get
        {
            if (Engine.GetValueOrDefault("Streaming/IsLastObserverPositionValid") == "false") return null;
            var raw = Engine.GetValueOrDefault("Streaming/LastObserverPosition");
            if (raw is null) return null;
            var parts = raw.Trim('[', ']', ' ').Split(',');
            if (parts.Length < 3) return null;
            var nums = parts.Select(p => double.TryParse(p.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToArray();
            if (nums.Any(double.IsNaN)) return null;
            // [0, 3, 1] o. ä. = Hauptmenü/noch keine Welt geladen
            if (Math.Abs(nums[0]) < 10 && Math.Abs(nums[1]) < 10) return null;
            return (nums[0], nums[1], nums[2]);
        }
    }

    public static string DefaultQueueDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "REDEngine", "ReportQueue");

    public static IReadOnlyList<RedCrashReport> LoadAll(string? queueDir = null)
    {
        queueDir ??= DefaultQueueDir;
        if (!Directory.Exists(queueDir)) return [];
        return Directory.EnumerateDirectories(queueDir, "Cyberpunk2077-*")
            .Select(TryLoad)
            .OfType<RedCrashReport>()
            .OrderByDescending(r => r.Time)
            .ToList();
    }

    private static readonly Regex FolderTime = new(@"-(\d{8})-(\d{6})-", RegexOptions.Compiled);
    // "Game/Patch@7#TID=0":"2.5"   bzw.  "uptimeSeconds":309
    private static readonly Regex EngineLine = new("^\"(?<k>[^\"@]+)(?:@[^\"]*)?\":\"?(?<v>.*?)\"?$", RegexOptions.Compiled);

    public static RedCrashReport? TryLoad(string folder)
    {
        try
        {
            var name = Path.GetFileName(folder);
            var m = FolderTime.Match(name);
            var time = m.Success
                ? DateTime.ParseExact(m.Groups[1].Value + m.Groups[2].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture)
                : Directory.GetLastWriteTime(folder);

            var report = ReadKeyValues(Path.Combine(folder, "report.txt"));
            var stack = ReadKeyValues(Path.Combine(folder, "stacktrace.txt"), ':');

            var modules = report
                .Where(kv => kv.Key.StartsWith("LoadedModule[", StringComparison.Ordinal))
                .Select(kv => kv.Value)
                .ToList();

            var engine = new Dictionary<string, string>(StringComparer.Ordinal);
            var attch = Path.Combine(folder, "attch");
            if (Directory.Exists(attch))
            {
                foreach (var log in Directory.EnumerateFiles(attch, "*.txt"))
                {
                    foreach (var line in File.ReadLines(log))
                    {
                        var em = EngineLine.Match(line.Trim());
                        if (em.Success) engine[em.Groups["k"].Value] = em.Groups["v"].Value;
                    }
                }
            }

            JsonElement? sys = null;
            var sysPath = Path.Combine(folder, "sysinfo.json");
            if (File.Exists(sysPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(sysPath));
                sys = doc.RootElement.Clone();
            }

            var dump = Directory.EnumerateFiles(folder, "*.dmp").FirstOrDefault();

            return new RedCrashReport
            {
                Folder = folder,
                Time = time,
                ErrorReason = stack.GetValueOrDefault("Error reason") ?? report.GetValueOrDefault("Sig[1].Value"),
                Expression = stack.GetValueOrDefault("Expression"),
                Message = stack.GetValueOrDefault("Message"),
                InternalVersion = report.GetValueOrDefault("Sig[2].Value")?.Trim(),
                Engine = engine,
                LoadedModules = modules,
                DumpPath = dump,
                SysInfo = sys,
            };
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string> ReadKeyValues(string path, char sep = '=')
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return dict;
        foreach (var line in File.ReadLines(path))
        {
            int i = line.IndexOf(sep);
            if (i <= 0) continue;
            dict.TryAdd(line[..i].Trim(), line[(i + 1)..].Trim());
        }
        return dict;
    }
}
