using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameDoctor.Core.Dump;
using GameDoctor.Core.Localization;

namespace GameDoctor.Core.Symbols;

public enum SymbolStatus
{
    /// <summary>Kein Spielordner bekannt.</summary>
    NoGame,
    /// <summary>Die Funktionsnamen wurden (noch) nicht geladen.</summary>
    NotInstalled,
    /// <summary>Geladen, passen aber nicht zur installierten Spielversion (z. B. nach einem Patch) – es werden keine Namen angezeigt.</summary>
    WrongVersion,
    Ready,
}

public sealed record ResolvedFrame(ulong Offset, ResolvedFunction Function);

/// <summary>Funktionsnamen zu einem Absturz: die Absturzstelle, die Frames im Aufrufpfad und das daraus abgeleitete Teilsystem.</summary>
public sealed record CrashSymbols(ResolvedFunction? Site, IReadOnlyList<ResolvedFrame> Frames, SubsystemInfo? Subsystem, string? SubsystemFunction);

/// <summary>
/// Optionale Funktionsnamen für Absturzstellen. Die Namen kommen vom Community-Projekt cp2077-symbols (MIT-Lizenz), das sie anhand der
/// Hashes in der Spieldatei <c>cyberpunk2077_addresses.json</c> ermittelt hat. GameDoctor lädt sie nur nach Zustimmung des Nutzers,
/// prüft sie per SHA-256 und legt sie lokal ab; es wird nichts gesendet. Stand der Daten: Patch 2.5.
/// </summary>
public static class SymbolDatabase
{
    public const string ProjectUrl = "https://codeberg.org/bartmoss/cp2077-symbols";
    /// <summary>Fester Stand des Projekts (27.10.2026), damit der Download reproduzierbar und prüfbar bleibt.</summary>
    public const string DownloadUrl = "https://codeberg.org/bartmoss/cp2077-symbols/raw/commit/7e2a18a88ae5ca903b114f01d5c241ee927cbb78/data/names_by_address.csv";
    public const long DownloadBytes = 59_460_423;
    private const string Sha256 = "DA937245AB6790323D8496E04A12893DC0843603D386B9FA8FACB29DA084787E";
    private const ulong ImageBase = 0x140000000;

    public const string GameExe = "Cyberpunk2077.exe";

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameDoctor", "symbols");
    public static string IndexPath => Path.Combine(Dir, "index.bin");
    public static string AddressFile(string gameRoot) => Path.Combine(gameRoot, "bin", "x64", "cyberpunk2077_addresses.json");

    private static SymbolIndex? _cache;
    private static DateTime _cacheTime;

    /// <summary>Öffnet den lokalen Index, wenn er zur installierten Spielversion passt.</summary>
    public static SymbolIndex? Open(string? gameRoot, out SymbolStatus status)
    {
        if (gameRoot is null) { status = SymbolStatus.NoGame; return null; }
        if (!File.Exists(IndexPath)) { status = SymbolStatus.NotInstalled; return null; }

        var stamp = File.GetLastWriteTimeUtc(IndexPath);
        if (_cache is null || stamp != _cacheTime) { _cache = SymbolIndex.Load(IndexPath); _cacheTime = stamp; }
        if (_cache is null) { status = SymbolStatus.NotInstalled; return null; }

        // Passt der Index zum installierten Spiel? Der Linker-Zeitstempel steht am Anfang der Spieldatei.
        var current = ReadLinkerTimestamp(AddressFile(gameRoot));
        if (current is null || current != _cache.LinkerTimestamp || !_cache.IsTrusted) { status = SymbolStatus.WrongVersion; return _cache; }
        status = SymbolStatus.Ready;
        return _cache;
    }

    public static string? ReadLinkerTimestamp(string addressFile)
    {
        try
        {
            using var fs = new FileStream(addressFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[512];
            int n = fs.Read(buf, 0, buf.Length);
            var m = Regex.Match(Encoding.UTF8.GetString(buf, 0, n), "\"Linker map timestamp\"\\s*:\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ Auflösung für einen Absturz

    /// <summary>Benennt Absturzstelle und Aufrufpfad eines Dumps und leitet das Teilsystem ab.</summary>
    public static CrashSymbols? Describe(DumpAnalysis dump, SymbolIndex index)
    {
        if (dump.Error is not null) return null;

        ResolvedFunction? site = null;
        if (dump.FaultingModule is { } fm && fm.Name.Equals(GameExe, StringComparison.OrdinalIgnoreCase))
            site = index.Resolve(dump.FaultingOffset, returnAddress: false);

        var frames = new List<ResolvedFrame>();
        foreach (var h in dump.StackHits.Where(h => h.Module.Name.Equals(GameExe, StringComparison.OrdinalIgnoreCase)).Take(40))
            if (index.Resolve(h.Offset, returnAddress: true) is { } f) frames.Add(new ResolvedFrame(h.Offset, f));

        // Teilsystem: erste eindeutige Funktion mit erkennbarem Namensraum – zuerst die Absturzstelle, dann der Aufrufpfad nach oben
        SubsystemInfo? sub = null; string? subFn = null;
        // Nur die obersten Frames: weiter unten im Stack liegen oft Reste älterer Aufrufe, die mit dem Absturz nichts zu tun haben
        foreach (var f in (site is null ? [] : new[] { site }).Concat(frames.Take(16).Select(x => x.Function)))
        {
            if ((sub = Subsystems.ForFunction(f.Names)) is not null) { subFn = f.DisplayName; break; }
        }
        return site is null && frames.Count == 0 ? null : new CrashSymbols(site, frames, sub, subFn);
    }

    // ------------------------------------------------------------------ Installation

    /// <summary>Lädt die Namensdatei (nur nach Zustimmung des Nutzers aufrufen), prüft sie und baut den lokalen Index.</summary>
    public static async Task<SymbolIndex> InstallAsync(string gameRoot, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Dir);
        var csv = Path.Combine(Dir, "names_by_address.csv.download");
        try
        {
            await DownloadAsync(csv, progress, ct);
            return await Task.Run(() => BuildFromFile(csv, gameRoot, progress), ct);
        }
        finally { try { File.Delete(csv); } catch { /* egal */ } }
    }

    private static async Task DownloadAsync(string target, IProgress<string>? progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GameDoctor/0.1");
        using var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            var buf = new byte[1 << 16];
            int n; long lastReport = 0;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                hash.AppendData(buf, 0, n);
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                total += n;
                if (total - lastReport > 2_000_000) { lastReport = total; progress?.Report(L.F("Lade Funktionsnamen … {0:F0} von {1:F0} MB", total / 1048576.0, DownloadBytes / 1048576.0)); }
            }
        }
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(target);
            throw new InvalidOperationException(L.T("Die heruntergeladene Namensdatei hat eine unerwartete Prüfsumme und wurde verworfen."));
        }
    }

    /// <summary>Baut den Index aus einer lokalen Namensdatei (CSV) und der Adressdatei des Spiels und speichert ihn.</summary>
    public static SymbolIndex BuildFromFile(string csvPath, string gameRoot, IProgress<string>? progress = null)
    {
        var addressFile = AddressFile(gameRoot);
        if (!File.Exists(addressFile))
            throw new FileNotFoundException(L.F("Die Adressdatei des Spiels fehlt: {0}", addressFile));

        progress?.Report(L.T("Lese Adressdatei des Spiels …"));
        var (stamp, starts, named) = ReadAddressFile(addressFile);
        if (starts.Count == 0) throw new InvalidOperationException(L.T("Die Adressdatei des Spiels enthält keine Funktionen."));
        uint codeStart = starts.Min(), codeEnd = starts.Max() + 0x2000;

        progress?.Report(L.T("Lese Funktionsnamen …"));
        var mangledByRva = new Dictionary<uint, List<string>>();
        foreach (var line in File.ReadLines(csvPath).Skip(1))
        {
            int comma = line.IndexOf(',');
            if (comma < 4 || !line.StartsWith("0x", StringComparison.Ordinal)) continue;
            if (!ulong.TryParse(line.AsSpan(2, comma - 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ea) || ea < ImageBase) continue;
            ulong rva = ea - ImageBase;
            if (rva < codeStart || rva > codeEnd) continue;
            if (!mangledByRva.TryGetValue((uint)rva, out var l)) mangledByRva[(uint)rva] = l = [];
            l.Add(line[(comma + 1)..]);
        }

        progress?.Report(L.T("Übersetze Funktionsnamen in lesbare Form …"));
        var cache = new Dictionary<string, string>();
        var names = new List<(uint, string)>(mangledByRva.Count);
        int done = 0;
        foreach (var (rva, list) in mangledByRva)
        {
            var distinct = new List<string>();
            foreach (var m in list)
            {
                if (!cache.TryGetValue(m, out var d)) cache[m] = d = Demangler.NameOnly(m);
                if (d.Length > 1500) d = d[..1500];
                if (!distinct.Contains(d) && distinct.Count < 4) distinct.Add(d);
            }
            names.Add((rva, string.Join('\u0001', distinct)));
            if (++done % 100000 == 0) progress?.Report(L.F("Übersetze Funktionsnamen … {0} von {1}", done, mangledByRva.Count));
        }

        // Stichprobe: die vom Spiel selbst benannten Funktionen müssen mit den Community-Namen übereinstimmen – sonst gehört die Datei zu einer anderen Version
        var byRva = names.ToDictionary(x => x.Item1, x => x.Item2);
        int ok = 0;
        foreach (var (rva, sym) in named)
            if (byRva.TryGetValue(rva, out var joined) && joined.Split('\u0001').Any(n =>
                    n.Contains(sym, StringComparison.OrdinalIgnoreCase) || sym.Contains(n, StringComparison.OrdinalIgnoreCase))) ok++;

        progress?.Report(L.T("Speichere Index …"));
        var index = SymbolIndex.Create(stamp, starts, names, ok, named.Count);
        index.Save(IndexPath);
        _cache = null;
        return index;
    }

    private static (string Stamp, List<uint> Starts, List<(uint Rva, string Symbol)> Named) ReadAddressFile(string path)
    {
        var reader = new Utf8JsonReader(File.ReadAllBytes(path));
        string stamp = ""; uint constOff = 0x1000;
        var raw = new List<(uint Off, string Sym)>();

        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) continue;
            var prop = reader.GetString();
            reader.Read();
            if (prop == "Linker map timestamp") stamp = reader.GetString() ?? "";
            else if (prop == "Code constant offset") uint.TryParse(reader.GetString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out constOff);
            else if (prop == "Addresses" && reader.TokenType == JsonTokenType.StartArray)
            {
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.StartObject) continue;
                    string? offset = null, sym = null;
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        if (reader.TokenType != JsonTokenType.PropertyName) continue;
                        var key = reader.GetString();
                        reader.Read();
                        if (key == "offset") offset = reader.GetString();
                        else if (key == "symbol") sym = reader.GetString();
                    }
                    // Nur der Code-Abschnitt ("0001:…") – dort liegen die Funktionen
                    if (offset is null || !offset.StartsWith("0001:", StringComparison.Ordinal)) continue;
                    if (uint.TryParse(offset.AsSpan(5), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var off))
                        raw.Add((off, sym ?? ""));
                }
            }
        }
        var starts = raw.Select(x => x.Off + constOff).ToList();
        var named = raw.Where(x => x.Sym.Length > 0).Select(x => (x.Off + constOff, x.Sym)).ToList();
        return (stamp, starts, named);
    }
}

/// <summary>Übersetzt Namen im MSVC-Format (<c>?Name@Klasse@@…</c>) in lesbare Form – mit der Funktion, die Windows (dbghelp.dll) mitbringt.</summary>
internal static class Demangler
{
    [DllImport("dbghelp.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern uint UnDecorateSymbolName(string name, StringBuilder output, uint maxLength, uint flags);

    private const uint UndnameNameOnly = 0x1000; // UNDNAME_NAME_ONLY: ohne Rückgabetyp und Parameter

    public static string NameOnly(string mangled)
    {
        if (!mangled.StartsWith('?')) return mangled;
        try
        {
            var sb = new StringBuilder(2048);
            return UnDecorateSymbolName(mangled, sb, 2048, UndnameNameOnly) > 0 ? sb.ToString() : mangled;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return mangled; }
    }
}
