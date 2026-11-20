using System.Text.RegularExpressions;

namespace GameDoctor.Core.Games.Cyberpunk;

public sealed record LogIssue(string Source, Severity Severity, string Message, int Count, string? Mod, string File);

/// <summary>Inhalte einer Mod (Orte, Kartenmarkierungen, Journal-Einträge), die das Spiel nicht findet.</summary>
public sealed record UnresolvedModContent(string Source, string Owner, IReadOnlyList<string> Items, string File);

/// <summary>Liest die Logs der gängigen Mod-Frameworks (redscript, RED4ext, CET, ArchiveXL, TweakXL) aus.</summary>
public sealed class ModLogScanner(CyberpunkInstall install)
{
    public sealed class Result
    {
        public List<LogIssue> Issues { get; } = [];
        public List<UnresolvedModContent> UnresolvedContent { get; } = [];
        public List<string> ScannedFiles { get; } = [];
        public string? Red4ExtVersion { get; set; }
        public string? Red4ExtGameVersion { get; set; }
        public string? CetVersion { get; set; }
        public string? CetGameVersion { get; set; }
        public int Red4ExtPluginCount { get; set; }
        public int ArchiveModCount { get; set; }
        public int RedscriptModCount { get; set; }
        public int CetModCount { get; set; }
    }

    // Formulierungen, die auf Versions-/Kompatibilitätsprobleme hindeuten
    private static readonly Regex CompatPattern = new(
        @"incompatible|not compatible|unsupported game version|unknown game version|is outdated|requires .* version|was not loaded|could not be loaded|failed to load|version mismatch",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public Result Scan()
    {
        var r = new Result
        {
            Red4ExtPluginCount = CountDirs(Path.Combine(install.Red4ExtDir, "plugins")),
            ArchiveModCount = Directory.Exists(install.ArchiveModDir)
                ? Directory.EnumerateFiles(install.ArchiveModDir, "*.archive", SearchOption.AllDirectories).Count() : 0,
            RedscriptModCount = CountDirs(install.RedscriptDir),
            CetModCount = CountDirs(Path.Combine(install.CetDir, "mods")),
        };

        ScanRedscript(r);
        ScanRed4Ext(r);
        ScanCet(r);
        ScanBracketLog(r, "ArchiveXL", Newest(Path.Combine(install.Red4ExtDir, "plugins", "ArchiveXL"), "ArchiveXL-*.log"));
        ScanBracketLog(r, "TweakXL", Newest(Path.Combine(install.Red4ExtDir, "plugins", "TweakXL"), "TweakXL-*.log"));
        ScanBracketLog(r, "Codeware", Newest(Path.Combine(install.Red4ExtDir, "plugins", "Codeware"), "Codeware-*.log"));
        return r;
    }

    private static int CountDirs(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateDirectories(dir).Count(d => !Path.GetFileName(d).StartsWith("__")) : 0;

    private static string? Newest(string dir, string pattern) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, pattern).Where(f => new FileInfo(f).Length > 0)
                .OrderByDescending(File.GetLastWriteTime).FirstOrDefault()
            : null;

    private static IEnumerable<string> SafeReadLines(string path)
    {
        // Logs können vom laufenden Spiel noch offen sein
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        while (sr.ReadLine() is { } line) yield return line;
    }

    // ---------- redscript ----------
    // [ERROR - Fri, 20 Nov 2026 17:01:23 +0100] message
    private static readonly Regex RedscriptHead = new(@"^\[(?<lvl>ERROR|WARN|INFO)\s*-[^\]]*\]\s*(?<msg>.*)$", RegexOptions.Compiled);
    private static readonly Regex ScriptModPath = new(@"r6\\scripts\\(?<mod>[^\\]+)\\", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private void ScanRedscript(Result r)
    {
        var file = Path.Combine(install.RedscriptLogDir, "redscript_rCURRENT.log");
        if (!File.Exists(file)) return;
        r.ScannedFiles.Add(file);

        var entries = new List<(string Level, List<string> Lines)>();
        foreach (var line in SafeReadLines(file))
        {
            var m = RedscriptHead.Match(line);
            if (m.Success) entries.Add((m.Groups["lvl"].Value, [m.Groups["msg"].Value]));
            else if (entries.Count > 0 && !string.IsNullOrWhiteSpace(line)) entries[^1].Lines.Add(line.Trim());
        }

        var grouped = new Dictionary<(Severity, string, string?), int>();
        foreach (var (level, lines) in entries.Where(e => e.Level != "INFO"))
        {
            var sev = level == "ERROR" ? Severity.Critical : Severity.Hint;
            var mod = lines.Select(l => ScriptModPath.Match(l)).FirstOrDefault(x => x.Success)?.Groups["mod"].Value;
            // Die eigentliche Meldung steht nach dem Code-Ausschnitt (Zeile mit ^^^)
            var caret = lines.FindIndex(l => l.StartsWith('^'));
            var msg = caret >= 0 && caret + 1 < lines.Count ? lines[caret + 1] : string.Join(" ", lines.Take(2));
            msg = Truncate(msg, 220);
            var key = (sev, msg, mod);
            grouped[key] = grouped.GetValueOrDefault(key) + 1;
        }
        foreach (var ((sev, msg, mod), count) in grouped)
            r.Issues.Add(new LogIssue("redscript", sev, msg, count, mod, file));
    }

    // ---------- RED4ext ----------
    // [2026-11-20 17:01:05.010] [info    ] [ 17592] [RED4ext] message
    private static readonly Regex Red4ExtLine = new(@"^\[[^\]]+\]\s*\[(?<lvl>\w+)\s*\]\s*\[[^\]]*\]\s*\[(?<src>[^\]]+)\]\s*(?<msg>.*)$", RegexOptions.Compiled);

    private void ScanRed4Ext(Result r)
    {
        var file = Newest(Path.Combine(install.Red4ExtDir, "logs"), "red4ext-*.log");
        if (file is null) return;
        r.ScannedFiles.Add(file);

        var grouped = new Dictionary<(Severity, string, string), int>();
        foreach (var line in SafeReadLines(file))
        {
            var m = Red4ExtLine.Match(line);
            if (!m.Success) continue;
            var lvl = m.Groups["lvl"].Value.ToLowerInvariant();
            var src = m.Groups["src"].Value;
            var msg = m.Groups["msg"].Value;

            if (msg.StartsWith("RED4ext (v", StringComparison.Ordinal))
                r.Red4ExtVersion = Regex.Match(msg, @"v([\d.]+)").Groups[1].Value;
            if (msg.StartsWith("Product version:", StringComparison.Ordinal))
                r.Red4ExtGameVersion = msg["Product version:".Length..].Trim();

            Severity? sev = lvl switch
            {
                "error" or "critical" => Severity.Critical,
                "warning" or "warn" => Severity.Warning,
                _ => CompatPattern.IsMatch(msg) ? Severity.Warning : null,
            };
            if (sev is null) continue;
            var key = (sev.Value, Truncate(msg, 220), src);
            grouped[key] = grouped.GetValueOrDefault(key) + 1;
        }
        foreach (var ((sev, msg, src), count) in grouped)
            r.Issues.Add(new LogIssue("RED4ext", sev, msg, count, src == "RED4ext" ? null : src, file));
    }

    // ---------- Cyber Engine Tweaks ----------
    // [2026-11-10 19:07:25 UTC+01:00] [info] [function] [13932] message
    private static readonly Regex CetModInMessage = new(@"mods[\\/](?<mod>[^\\/]+)[\\/]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private void ScanCet(Result r)
    {
        var mainLog = Path.Combine(install.CetDir, "cyber_engine_tweaks.log");
        if (File.Exists(mainLog))
        {
            r.ScannedFiles.Add(mainLog);
            // Das Log wird über mehrere Sitzungen fortgeschrieben – nur die letzte Sitzung betrachten
            var lines = SafeReadLines(mainLog).ToList();
            int start = lines.FindLastIndex(l => l.Contains("Cyber Engine Tweaks is starting"));
            var session = start >= 0 ? lines.Skip(start) : lines;
            foreach (var l in session)
            {
                var vm = Regex.Match(l, @"CET version (v[\w.\-]+)");
                if (vm.Success) r.CetVersion = vm.Groups[1].Value;
                var gm = Regex.Match(l, @"Game version ([\d.]+)");
                if (gm.Success) r.CetGameVersion = gm.Groups[1].Value;
            }
            AddCetIssues(r, session, mainLog, "CET");
        }

        var scripting = Path.Combine(install.CetDir, "scripting.log");
        if (File.Exists(scripting))
        {
            r.ScannedFiles.Add(scripting);
            AddCetIssues(r, SafeReadLines(scripting).TakeLast(20000), scripting, "CET-Lua");
        }
    }

    private static void AddCetIssues(Result r, IEnumerable<string> lines, string file, string source)
    {
        var grouped = new Dictionary<(Severity, string, string?), int>();
        foreach (var line in lines)
        {
            if (SplitLevel(line) is not var (lvl, rawMsg)) continue;
            Severity? sev = lvl switch
            {
                "error" or "critical" => Severity.Warning,
                "warning" or "warn" => Severity.Hint,
                _ => null,
            };
            if (sev is null) continue;
            // CET setzt noch [Funktion] [Thread-ID] vor die Meldung
            var msg = Regex.Replace(rawMsg, @"^(\[[^\]]*\]\s*)+", "");
            var mod = CetModInMessage.Match(msg) is { Success: true } mm ? mm.Groups["mod"].Value : null;
            // Zahlen/Adressen entfernen, damit gleiche Fehler zusammengefasst werden
            var normalized = Regex.Replace(Truncate(msg, 220), @"0x[0-9a-fA-F]+|\b\d+\b", "#");
            var key = (sev.Value, normalized, mod);
            grouped[key] = grouped.GetValueOrDefault(key) + 1;
        }
        foreach (var ((sev, msg, mod), count) in grouped)
            r.Issues.Add(new LogIssue(source, sev, msg, count, mod, file));
    }

    // ---------- ArchiveXL / TweakXL / Codeware ----------
    // [2026-11-20 17:01:18.488] [17592] [warning] [QuestPhase] message  – Reihenfolge der Klammern variiert je Tool
    // [Journal] Can't resolve mappin #2466948601 (points_of_interest/icm_garages/icm_garage_littlechinagarage) reference.
    private static readonly Regex UnresolvedRef = new(@"Can't resolve \w+ #\d+ \((?<path>[^)]+)\)", RegexOptions.Compiled);

    private static readonly Regex LeadingBracket = new(@"^\s*\[(?<c>[^\]]*)\]", RegexOptions.Compiled);
    private static readonly HashSet<string> LevelWords = ["trace", "debug", "info", "warning", "warn", "error", "critical", "fatal"];

    /// <summary>Sucht in den führenden [..]-Blöcken die Log-Stufe; liefert Stufe und den Rest der Zeile.</summary>
    internal static (string Level, string Message)? SplitLevel(string line)
    {
        var rest = line;
        for (int i = 0; i < 4; i++)
        {
            var m = LeadingBracket.Match(rest);
            if (!m.Success) return null;
            rest = rest[m.Length..];
            var c = m.Groups["c"].Value.Trim().ToLowerInvariant();
            if (LevelWords.Contains(c)) return (c, rest.Trim());
        }
        return null;
    }

    private static void ScanBracketLog(Result r, string source, string? file)
    {
        if (file is null) return;
        r.ScannedFiles.Add(file);
        // Gleichartige Meldungen (unterscheiden sich nur in Namen in "…") zusammenfassen; erstes Beispiel behalten
        var grouped = new Dictionary<(Severity, string), int>();
        var examples = new Dictionary<(Severity, string), string>();
        // Nicht auflösbare Verweise je Mod-Ordner sammeln (z. B. points_of_interest/icm_garages/...)
        var unresolved = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in SafeReadLines(file))
        {
            if (SplitLevel(line) is not var (lvl, msg)) continue;
            var um = UnresolvedRef.Match(msg);
            if (um.Success && lvl is "error" or "warning" or "warn")
            {
                var segs = um.Groups["path"].Value.Split('/', '\\');
                var owner = segs.Length >= 3 ? segs[1] : segs[0];
                if (!unresolved.TryGetValue(owner, out var list)) unresolved[owner] = list = [];
                list.Add(segs[^1]);
                continue;
            }
            Severity? sev = lvl switch
            {
                "error" or "critical" or "fatal" => Severity.Warning,
                "warning" or "warn" => Severity.Hint,
                _ => null,
            };
            if (sev is null) continue;
            var pattern = Regex.Replace(msg, "\"[^\"]*\"", "\"…\"");
            var key = (sev.Value, pattern);
            grouped[key] = grouped.GetValueOrDefault(key) + 1;
            examples.TryAdd(key, msg);
        }
        foreach (var (key, count) in grouped)
        {
            var text = count > 1 ? $"{key.Item2}  – z. B.: {examples[key]}" : examples[key];
            r.Issues.Add(new LogIssue(source, key.Item1, Truncate(text, 260), count, null, file));
        }
        foreach (var (owner, items) in unresolved)
        {
            var distinct = items.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            r.UnresolvedContent.Add(new UnresolvedModContent(source, owner, distinct, file));
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
