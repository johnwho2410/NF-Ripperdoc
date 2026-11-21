using System.Text.Json;

namespace GameDoctor.Core.Platform;

/// <summary>
/// Vom Nutzer festgelegte bzw. zuletzt gefundene Pfade (%LOCALAPPDATA%\GameDoctor\paths.json).
/// Festgelegte Pfade haben immer Vorrang vor der automatischen Erkennung.
/// </summary>
public sealed class PathSettings
{
    /// <summary>Vom Nutzer festgelegter Spielordner (Vorrang vor allem anderen).</summary>
    public string? GamePath { get; set; }
    /// <summary>Vom Nutzer festgelegte Mod-Organizer-2-Instanz (Ordner mit ModOrganizer.ini).</summary>
    public string? Mo2InstancePath { get; set; }
    /// <summary>Zuletzt erfolgreich gefundener Spielordner – Rückfallebene, wenn sonst nichts greift.</summary>
    public string? LastKnownGamePath { get; set; }

    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameDoctor", "paths.json");

    public static PathSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<PathSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* defekt – Standardwerte */ }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* nicht kritisch */ }
    }
}
