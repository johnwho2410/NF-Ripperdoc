using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameDoctor.App;

/// <summary>Einstellungen der Oberfläche (Skin, Overlay …) in %LOCALAPPDATA%\GameDoctor\app-settings.json.</summary>
public sealed class AppSettings
{
    public string? Skin { get; set; }

    /// <summary>„de“ oder „en“; null = beim ersten Start nach der Windows-Sprache wählen.</summary>
    public string? Language { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OverlayCorner OverlayCorner { get; set; } = OverlayCorner.TopLeft;

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameDoctor", "app-settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new AppSettings();
    }

    /// <summary>Lädt, ändert und speichert in einem Schritt.</summary>
    public static void Update(Action<AppSettings> change)
    {
        var s = Load();
        change(s);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* gilt trotzdem für diese Sitzung */ }
    }
}
