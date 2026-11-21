using System.IO.Compression;
using System.Text.Json;

namespace GameDoctor.Core.Performance;

/// <summary>Speichert Aufnahmen komprimiert, damit sie später (z. B. mit verbesserten Regeln) neu ausgewertet werden können.</summary>
public static class RecordingStore
{
    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameDoctor", "recordings");

    // Populate: die Listen Frames/Sensors haben keinen Setter und werden beim Laden befüllt
    private static readonly JsonSerializerOptions Json = new()
    {
        IncludeFields = true,
        PreferredObjectCreationHandling = System.Text.Json.Serialization.JsonObjectCreationHandling.Populate,
    };

    public static string Save(PerformanceRecording rec)
    {
        Directory.CreateDirectory(Dir);
        var path = Path.Combine(Dir, $"perf-{rec.StartedAt:yyyyMMdd-HHmmss}.json.gz");
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Optimal);
        JsonSerializer.Serialize(gz, rec, Json);
        return path;
    }

    public static PerformanceRecording Load(string path)
    {
        using var fs = File.OpenRead(path);
        using Stream s = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(fs, CompressionMode.Decompress) : fs;
        return JsonSerializer.Deserialize<PerformanceRecording>(s, Json)
               ?? throw new InvalidDataException(Localization.L.T("Aufnahme konnte nicht gelesen werden."));
    }

    public static string? Latest() =>
        Directory.Exists(Dir) ? Directory.EnumerateFiles(Dir, "perf-*.json.gz").OrderDescending().FirstOrDefault() : null;
}
