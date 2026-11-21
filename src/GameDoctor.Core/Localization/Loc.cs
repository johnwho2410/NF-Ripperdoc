using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace GameDoctor.Core.Localization;

/// <summary>
/// Übersetzung nach dem „Quelltext als Schlüssel“-Prinzip: Im Code steht der deutsche Text, die englische Fassung
/// liegt in eingebetteten JSON-Dateien (Localization/en/*.json, je Bereich eine Datei: „Deutsch“ → „English“).
/// Fehlt eine Übersetzung, erscheint der deutsche Text – nie ein leeres Feld.
///
/// Verwendung:
///   L.T("Abstürze")                                   – fester Text
///   L.F("{0} Abstürze in den letzten 7 Tagen", n)      – mit Platzhaltern (Zahlen-/Datumsformat folgt der Sprache)
/// </summary>
public static class L
{
    public const string German = "de";
    public const string English = "en";

    private static readonly Lazy<Dictionary<string, string>> En = new(LoadEnglish);
    private static readonly ConcurrentDictionary<string, byte> MissingKeys = new();

    public static string Language { get; private set; } = German;
    public static bool IsEnglish => Language == English;

    /// <summary>Nach jedem Sprachwechsel (Oberfläche aktualisiert sich, Analyse wird neu erstellt).</summary>
    public static event Action? LanguageChanged;

    /// <summary>Sprache beim ersten Start: Deutsch bei deutschem Windows, sonst Englisch.</summary>
    public static string FromSystem() =>
        CultureInfo.InstalledUICulture.TwoLetterISOLanguageName.Equals("de", StringComparison.OrdinalIgnoreCase) ? German : English;

    public static void SetLanguage(string? language)
    {
        Language = language == English ? English : German;
        // Zahlen, Dezimaltrennzeichen und Datumsformate folgen der Sprache – auch in Hintergrund-Threads
        var culture = CultureInfo.GetCultureInfo(Language == English ? "en-US" : "de-DE");
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = culture;
        LanguageChanged?.Invoke();
    }

    /// <summary>Übersetzt einen festen Text.</summary>
    public static string T(string german)
    {
        if (Language == German || string.IsNullOrEmpty(german)) return german;
        if (En.Value.TryGetValue(german, out var en)) return en;
        MissingKeys.TryAdd(german, 0);
        return german;
    }

    /// <summary>Übersetzt einen Text mit Platzhaltern {0}, {1:F1} … und setzt die Werte im Format der Sprache ein.</summary>
    public static string F(string germanFormat, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(germanFormat), args);

    /// <summary>Texte, für die zur Laufzeit keine Übersetzung gefunden wurde (Entwicklerhilfe).</summary>
    public static IReadOnlyCollection<string> Missing => MissingKeys.Keys.ToList();

    /// <summary>Alle englischen Einträge (für die Vollständigkeitsprüfung).</summary>
    public static IReadOnlyDictionary<string, string> EnglishTable => En.Value;

    private static Dictionary<string, string> LoadEnglish()
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        var asm = typeof(L).Assembly;
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.Contains(".Localization.en.") && n.EndsWith(".json")))
        {
            using var s = asm.GetManifestResourceStream(name);
            if (s is null) continue;
            var part = JsonSerializer.Deserialize<Dictionary<string, string>>(s);
            if (part is null) continue;
            foreach (var (k, v) in part) table[k] = v;
        }
        return table;
    }
}
