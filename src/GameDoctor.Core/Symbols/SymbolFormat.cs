using GameDoctor.Core.Localization;

namespace GameDoctor.Core.Symbols;

/// <summary>Anzeigetexte für aufgelöste Funktionen (gekürzt, mit Hinweis auf zusammengelegte Funktionen).</summary>
public static class SymbolFormat
{
    /// <summary>Funktionsname für die Anzeige. Template-Argumente machen Namen sehr lang, deshalb wird gekürzt (der volle Name steht im Tooltip/Bericht).</summary>
    public static string Short(ResolvedFunction f, int max = 110)
    {
        var name = f.DisplayName ?? L.T("(unbenannt, nur Hash bekannt)");
        if (name.Length > max) name = name[..(max - 1)] + "…";
        return f.Folded ? L.F("{0}  (+{1} weitere identische Funktionen)", name, f.Names.Count - 1) : name;
    }

    /// <summary>Benannter Aufrufpfad (oben = zuletzt), nur Frames mit Namen, gekürzt.</summary>
    public static IEnumerable<string> CallPath(CrashSymbols s, int take = 8, int max = 90) =>
        s.Frames.Where(f => f.Function.DisplayName is not null).Take(take)
            .Select(f => $"+0x{f.Offset:X}  {Short(f.Function, max)}");
}
