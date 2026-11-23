using System.Text;

namespace GameDoctor.Core.Symbols;

/// <summary>Eine aufgelöste Funktion: Anfang im Modul, bekannte Namen (leer = nur Hash bekannt) und Abstand der Adresse zum Funktionsanfang.</summary>
public sealed record ResolvedFunction(ulong Start, IReadOnlyList<string> Names, ulong Distance)
{
    public string? DisplayName => Names.Count > 0 ? Names[0] : null;
    /// <summary>Der Linker hat identische Funktionen zusammengelegt – an dieser Adresse liegen mehrere Namen.</summary>
    public bool Folded => Names.Count > 1;
}

/// <summary>
/// Lokaler Index der Funktionsnamen für genau eine Spielversion: alle Funktionsanfänge aus der Datei, die das Spiel selbst mitliefert
/// (<c>cyberpunk2077_addresses.json</c>) und die Namen aus dem Community-Projekt cp2077-symbols (MIT-Lizenz).
/// </summary>
public sealed class SymbolIndex
{
    private const uint Magic = 0x49534447; // "GDSI"
    private const int FormatVersion = 1;
    /// <summary>Weiter als das vom Funktionsanfang entfernt zählt eine Adresse nicht mehr als Teil dieser Funktion.</summary>
    private const ulong MaxFunctionSize = 0x10000;

    /// <summary>Identifiziert die Spielversion (Zeitstempel des Linkers aus der Spieldatei).</summary>
    public string LinkerTimestamp { get; private init; } = "";
    /// <summary>Wie viele der in der Spieldatei benannten Funktionen mit den Community-Namen übereinstimmen.</summary>
    public int VerifiedMatches { get; private init; }
    public int VerifiedTotal { get; private init; }
    public int NamedAddresses => _nameRvas.Length;
    public int FunctionStarts => _starts.Length;
    public DateTime BuiltAt { get; private init; }

    /// <summary>Die Namen passen zur installierten Spielversion (Stichprobe aus der Spieldatei stimmt überein).</summary>
    public bool IsTrusted => VerifiedTotal >= 10 && VerifiedMatches * 10 >= VerifiedTotal * 9;

    private uint[] _starts = [];
    private uint[] _nameRvas = [];
    private string[] _names = []; // je Adresse alle Namen, getrennt durch '\u0001'
    private uint _codeStart, _codeEnd;

    /// <summary>Löst eine Adresse im Modul (Offset von der Basis) auf. Rücksprungadressen zeigen hinter den Aufruf, daher 1 abziehen.</summary>
    public ResolvedFunction? Resolve(ulong moduleOffset, bool returnAddress)
    {
        ulong a = returnAddress ? moduleOffset - 1 : moduleOffset;
        if (a < _codeStart || a > _codeEnd) return null; // keine Code-Adresse (z. B. Daten, die wie ein Zeiger aussehen)

        int i = Array.BinarySearch(_starts, (uint)a);
        if (i < 0) i = ~i - 1;
        if (i < 0) return null;
        ulong start = _starts[i];
        if (a - start > MaxFunctionSize) return null;

        int n = Array.BinarySearch(_nameRvas, (uint)start);
        IReadOnlyList<string> names = n >= 0 ? _names[n].Split('\u0001') : [];
        return new ResolvedFunction(start, names, a - start);
    }

    public static SymbolIndex Create(string linkerTimestamp, IEnumerable<uint> starts, IEnumerable<(uint Rva, string Names)> names,
        int verifiedMatches, int verifiedTotal)
    {
        var s = starts.Distinct().Order().ToArray();
        var n = names.OrderBy(x => x.Rva).ToArray();
        var index = new SymbolIndex
        {
            LinkerTimestamp = linkerTimestamp,
            VerifiedMatches = verifiedMatches,
            VerifiedTotal = verifiedTotal,
            BuiltAt = DateTime.Now,
            _starts = s,
            _nameRvas = n.Select(x => x.Rva).ToArray(),
            _names = n.Select(x => x.Names).ToArray(),
        };
        index._codeStart = s.Length > 0 ? s[0] : 0;
        index._codeEnd = s.Length > 0 ? s[^1] + 0x2000 : 0;
        return index;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write))
        using (var bw = new BinaryWriter(fs, Encoding.UTF8))
        {
            bw.Write(Magic); bw.Write(FormatVersion);
            bw.Write(LinkerTimestamp); bw.Write(VerifiedMatches); bw.Write(VerifiedTotal); bw.Write(BuiltAt.Ticks);
            bw.Write(_starts.Length); foreach (var s in _starts) bw.Write(s);
            bw.Write(_nameRvas.Length);
            for (int i = 0; i < _nameRvas.Length; i++) { bw.Write(_nameRvas[i]); bw.Write(_names[i]); }
        }
        File.Move(temp, path, overwrite: true);
    }

    public static SymbolIndex? Load(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var br = new BinaryReader(fs, Encoding.UTF8);
            if (br.ReadUInt32() != Magic || br.ReadInt32() != FormatVersion) return null;
            var stamp = br.ReadString(); int ok = br.ReadInt32(), total = br.ReadInt32(); var built = new DateTime(br.ReadInt64());
            var starts = new uint[br.ReadInt32()];
            for (int i = 0; i < starts.Length; i++) starts[i] = br.ReadUInt32();
            int count = br.ReadInt32();
            var rvas = new uint[count]; var names = new string[count];
            for (int i = 0; i < count; i++) { rvas[i] = br.ReadUInt32(); names[i] = br.ReadString(); }
            var index = new SymbolIndex
            {
                LinkerTimestamp = stamp, VerifiedMatches = ok, VerifiedTotal = total, BuiltAt = built,
                _starts = starts, _nameRvas = rvas, _names = names,
            };
            index._codeStart = starts.Length > 0 ? starts[0] : 0;
            index._codeEnd = starts.Length > 0 ? starts[^1] + 0x2000 : 0;
            return index;
        }
        catch { return null; } // fehlende oder beschädigte Datei: wie „nicht installiert“ behandeln
    }
}
