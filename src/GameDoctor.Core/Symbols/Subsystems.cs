using GameDoctor.Core.Localization;

namespace GameDoctor.Core.Symbols;

/// <summary>Ein Teilsystem des Spiels (Sinne, Waffen, …), abgeleitet aus dem Namensraum einer Funktion.</summary>
public sealed record SubsystemInfo(string Key, string Label);

/// <summary>
/// Leitet aus Funktionsnamen das Teilsystem ab. Die Namen stammen aus Community-Daten und sind nur eine Orientierung.
/// Hilfsfunktionen (Nachschlagen, Freigeben, Container) sind überall im Spiel im Einsatz und sagen nichts über das Teilsystem –
/// deshalb zählt der erste Frame im Aufrufpfad, der in einem erkennbaren Namensraum liegt.
/// </summary>
public static class Subsystems
{
    // Namensräume der Engine-Grundbausteine: kommen in fast jedem Absturz vor und werden übersprungen.
    private static readonly HashSet<string> Helpers = new(StringComparer.Ordinal)
    {
        "red", "std", "rtti", "memory", "NameBuilder", "io", "IO", "Detail", "CRT",
        "job", // Job-System: sitzt am Fuß jedes Thread-Stacks (Dispatcher-Schleife) und sagt nichts über das Teilsystem
    };

    // Deutsche Beschriftungen (Schlüssel für die Übersetzung); Präfix = Namensraum, längster Treffer gewinnt.
    // i18n: "Sinne / Wahrnehmung"
    // i18n: "Waffen"
    // i18n: "Spieldatenbank (TweakDB)"
    // i18n: "Interaktionen"
    // i18n: "Fahrzeuge"
    // i18n: "Benutzeroberfläche"
    // i18n: "Animation"
    // i18n: "Physik"
    // i18n: "Welt / Streaming"
    // i18n: "Entities (Spielobjekte)"
    // i18n: "KI / NPC-Verhalten"
    // i18n: "Spawning (Erzeugen von Objekten)"
    // i18n: "Eingabe"
    // i18n: "Audio"
    // i18n: "Szenen / Dialoge"
    // i18n: "Quests"
    // i18n: "Rendering / Grafik"
    // i18n: "Ressourcen (Laden von Daten)"
    private static readonly (string Prefix, string Label)[] Table =
    [
        ("sense", "Sinne / Wahrnehmung"),
        ("game::weapon", "Waffen"),
        ("game::data", "Spieldatenbank (TweakDB)"),
        ("game::interactions", "Interaktionen"),
        ("game::vehicle", "Fahrzeuge"),
        ("game::ui", "Benutzeroberfläche"),
        ("ink", "Benutzeroberfläche"),
        ("anim", "Animation"),
        ("physics", "Physik"),
        ("world", "Welt / Streaming"),
        ("ent", "Entities (Spielobjekte)"),
        ("AI", "KI / NPC-Verhalten"),
        ("spawn", "Spawning (Erzeugen von Objekten)"),
        ("input", "Eingabe"),
        ("audio", "Audio"),
        ("scn", "Szenen / Dialoge"),
        ("quest", "Quests"),
        ("render", "Rendering / Grafik"),
        ("rend", "Rendering / Grafik"),
        ("gfx", "Rendering / Grafik"),
        ("GpuApi", "Rendering / Grafik"),
        ("res", "Ressourcen (Laden von Daten)"),
    ];

    /// <summary>
    /// Zerlegt einen Funktionsnamen (ohne Parameter) in die Namensraum-/Klassen-Kette ohne die Funktion selbst.
    /// „game::weapon::Object::~Object“ → [game, weapon, Object]; Template-Argumente werden verworfen.
    /// </summary>
    public static IReadOnlyList<string> Qualifier(string name)
    {
        // Compiler-Namen wie ::`scalar deleting destructor' abschneiden
        int bt = name.IndexOf("::`", StringComparison.Ordinal);
        if (bt >= 0) name = name[..bt] + "::x";

        var sb = new System.Text.StringBuilder(name.Length);
        int depth = 0;
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c == '<')
            {
                // „operator<“ und „operator<<“ sind keine Template-Klammern
                if (depth == 0 && sb.ToString().EndsWith("operator", StringComparison.Ordinal)) { sb.Append(c); continue; }
                depth++;
            }
            else if (c == '>')
            {
                if (depth > 0) depth--; else sb.Append(c);
            }
            else if (depth == 0) sb.Append(c);
        }

        var parts = sb.ToString().Split("::", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length <= 1 ? [] : parts[..^1];
    }

    /// <summary>Teilsystem für einen einzelnen Funktionsnamen, oder null (Hilfsfunktion, kein Namensraum, nicht eindeutig).</summary>
    public static SubsystemInfo? ForFunction(IReadOnlyList<string> names)
    {
        // Mehrere verschiedene Namen an einer Adresse = der Linker hat identische Funktionen zusammengelegt: nicht eindeutig
        if (names.Count != 1) return null;
        var q = Qualifier(names[0]);
        if (q.Count < 2 || Helpers.Contains(q[0])) return null;
        // Klassen der Render-Schicht heißen CRenderXyz (ohne Namensraum davor)
        if (q[0].StartsWith("CRender", StringComparison.Ordinal)) return new SubsystemInfo("render", L.T("Rendering / Grafik"));

        for (int len = Math.Min(q.Count, 3); len >= 1; len--)
        {
            var prefix = string.Join("::", q.Take(len));
            foreach (var (p, label) in Table)
                if (p == prefix) return new SubsystemInfo(p, L.T(label));
        }
        // Unbekannter Namensraum: roh anzeigen (erste ein bis zwei Ebenen)
        var raw = string.Join("::", q.Take(q.Count == 2 ? 1 : 2));
        return new SubsystemInfo(raw, raw);
    }
}
