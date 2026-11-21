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
    // Name/Beschreibung werden beim Anzeigen übersetzt:
    // i18n: "Neongelb und Cyan, abgeschrägte Ecken – passend zu Cyberpunk 2077"
    // i18n: "Ruhiges, dunkles Design ohne Spielbezug"
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

    /// <summary>Beim Start: gespeicherten Skin laden (oder den Standard).</summary>
    public static void ApplySaved() => Apply(AppSettings.Load().Skin ?? DefaultSkinId, save: false);

    public static void Apply(string id, bool save = true)
    {
        var skin = Skins.FirstOrDefault(s => s.Id == id) ?? Skins.First(s => s.Id == DefaultSkinId);
        var dict = new ResourceDictionary { Source = new Uri($"pack://application:,,,/GameDoctor;component/{skin.File}") };

        var merged = Application.Current.Resources.MergedDictionaries;
        // Der Skin ist immer das erste Wörterbuch (so in App.xaml angelegt).
        if (merged.Count > 0) merged[0] = dict;
        else merged.Add(dict);

        Current = skin;
        if (save) AppSettings.Update(s => s.Skin = skin.Id);
        Changed?.Invoke();
    }
}
