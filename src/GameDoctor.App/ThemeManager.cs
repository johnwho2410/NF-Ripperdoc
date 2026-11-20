using System.IO;
using System.Text.Json;
using System.Windows;

namespace GameDoctor.App;

/// <summary>Ein Skin: eine ResourceDictionary-Datei unter Themes/ mit festen Schlüsseln (siehe Neutral.xaml).</summary>
public sealed record Skin(string Id, string Name, string File, string Description);

/// <summary>
/// Lädt und wechselt Skins zur Laufzeit. Alle Farben, Formen und Schriften werden per DynamicResource
/// eingebunden, daher ändert sich das Aussehen sofort, ohne Neustart.
/// Später kann jedes unterstützte Spiel seinen eigenen Standard-Skin mitbringen.
/// </summary>
public static class ThemeManager
{
    public static readonly IReadOnlyList<Skin> Skins =
    [
        new("cyberpunk", "Night City", "Themes/Cyberpunk.xaml", "Neongelb und Cyan, abgeschrägte Ecken – passend zu Cyberpunk 2077"),
        new("neutral", "Neutral", "Themes/Neutral.xaml", "Ruhiges, dunkles Design ohne Spielbezug"),
    ];

    /// <summary>Standard-Skin für das aktuell unterstützte Spiel.</summary>
    public const string DefaultSkinId = "cyberpunk";

    public static Skin Current { get; private set; } = Skins[0];

    /// <summary>Wird nach jedem Wechsel ausgelöst (z. B. für selbst gezeichnete Diagramme).</summary>
    public static event Action? Changed;

    private static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameDoctor", "app-settings.json");

    /// <summary>Beim Start: gespeicherten Skin laden (oder den Standard).</summary>
    public static void ApplySaved() => Apply(LoadSettings().Skin ?? DefaultSkinId, save: false);

    public static void Apply(string id, bool save = true)
    {
        var skin = Skins.FirstOrDefault(s => s.Id == id) ?? Skins.First(s => s.Id == DefaultSkinId);
        var dict = new ResourceDictionary { Source = new Uri($"pack://application:,,,/GameDoctor;component/{skin.File}") };

        var merged = Application.Current.Resources.MergedDictionaries;
        // Der Skin ist immer das erste Wörterbuch (so in App.xaml angelegt).
        if (merged.Count > 0) merged[0] = dict;
        else merged.Add(dict);

        Current = skin;
        if (save)
        {
            var s = LoadSettings();
            s.Skin = skin.Id;
            SaveSettings(s);
        }
        Changed?.Invoke();
    }

    private sealed class AppSettings
    {
        public string? Skin { get; set; }
    }

    private static AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new AppSettings();
    }

    private static void SaveSettings(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Skin gilt trotzdem für diese Sitzung */ }
    }
}
