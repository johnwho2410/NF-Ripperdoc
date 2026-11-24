using System.Text.RegularExpressions;

namespace GameDoctor.Core.Games.Cyberpunk;

public sealed record LogIssue(string Source, Severity Severity, string Message, int Count, string? Mod, string File);

/// <summary>Zwei oder mehr Mods ersetzen dieselbe Spielfunktion (@replaceMethod). Mod = die zuletzt geladene, Others = die überschriebenen.</summary>
public sealed record RedscriptConflict(string Mod, string ClassName, string? Method, IReadOnlyList<string> Others);

/// <summary>Inhalte einer Mod (Orte, Kartenmarkierungen, Journal-Einträge), die das Spiel nicht findet.</summary>
public sealed record UnresolvedModContent(string Source, string Owner, IReadOnlyList<string> Items, string File);

/// <summary>
/// Liest die Logs der gängigen Mod-Frameworks (redscript, RED4ext, CET, ArchiveXL, TweakXL) aus.
/// Sucht im Spielordner und in zusätzlichen Ebenen (Mod Organizer 2: aktive Mods + overwrite), weil Logs dort landen,
/// wenn das Spiel über MO2 gestartet wird.
/// </summary>
public sealed class ModLogScanner(CyberpunkInstall install, IReadOnlyList<string>? extraRoots = null)
{
    private IEnumerable<string> Roots => new[] { install.Root }.Concat(extraRoots ?? []);

    /// <summary>Denselben relativen Pfad in allen Ebenen.</summary>
    private IEnumerable<string> InAllRoots(params string[] relative) =>
        Roots.Select(r => Path.Combine([r, .. relative]));

    /// <summary>Neueste nicht-leere Datei mit diesem Muster in diesem relativen Ordner – über alle Ebenen.</summary>
    private string? NewestAcross(string pattern, params string[] relativeDir) =>
        InAllRoots(relativeDir).Select(d => Newest(d, pattern)).OfType<string>()
            .OrderByDescending(File.GetLastWriteTime).FirstOrDefault();

    /// <summary>Neueste vorhandene Datei unter diesem relativen Pfad – über alle Ebenen.</summary>
    private string? FileAcross(params string[] relative) =>
        InAllRoots(relative).Where(File.Exists).OrderByDescending(File.GetLastWriteTime).FirstOrDefault();

    /// <summary>Unterordner dieses relativen Ordners über alle Ebenen, ohne doppelte Namen.</summary>
    private int CountDirsAcross(params string[] relative) =>
        InAllRoots(relative).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateDirectories(d).Select(Path.GetFileName))
            .Where(n => n is not null && !n.StartsWith("__")).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    public sealed class Result
    {
        public List<LogIssue> Issues { get; } = [];
        public List<UnresolvedModContent> UnresolvedContent { get; } = [];
        public List<string> ScannedFiles { get; } = [];
        public List<RedscriptConflict> RedscriptConflicts { get; } = [];
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
            Red4ExtPluginCount = CountDirsAcross("red4ext", "plugins"),
            ArchiveModCount = InAllRoots("archive", "pc", "mod").Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d, "*.archive", SearchOption.AllDirectories).Select(Path.GetFileName))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            RedscriptModCount = CountDirsAcross("r6", "scripts"),
            CetModCount = CountDirsAcross("bin", "x64", "plugins", "cyber_engine_tweaks", "mods"),
        };

        ScanRedscript(r);
        ScanRed4Ext(r);
        ScanCet(r);
        ScanBracketLog(r, "ArchiveXL", NewestAcross("ArchiveXL-*.log", "red4ext", "plugins", "ArchiveXL"));
        ScanBracketLog(r, "TweakXL", NewestAcross("TweakXL-*.log", "red4ext", "plugins", "TweakXL"));
        ScanBracketLog(r, "Codeware", NewestAcross("Codeware-*.log", "red4ext", "plugins", "Codeware"));
        return r;
    }

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
        var file = FileAcross("r6", "logs", "redscript_rCURRENT.log");
        if (file is null) return;
        r.ScannedFiles.Add(file);

        var entries = new List<(string Level, List<string> Lines)>();
        foreach (var line in SafeReadLines(file))
        {
            var m = RedscriptHead.Match(line);
            if (m.Success) entries.Add((m.Groups["lvl"].Value, [m.Groups["msg"].Value]));
            else if (entries.Count > 0 && !string.IsNullOrWhiteSpace(line)) entries[^1].Lines.Add(line.Trim());
        }

        ResolveReplaceConflicts(r, entries);

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

    // Beispiel eines Log-Eintrags:
    //   At …\r6\scripts\modA\File.reds:2370:1:
    //   @replaceMethod(VehicleComponent)
    //   ^^^
    //   this method replacement overwrites a previous annotation targeting the same method …
    private static readonly Regex ConflictAt = new(@"^At\s+(?<file>.+?):(?<line>\d+):\d+:?\s*$", RegexOptions.Compiled);
    private static readonly Regex ReplaceAnnotation = new(@"@replaceMethod\(\s*(?<cls>[^)\s]+)\s*\)", RegexOptions.Compiled);
    private static readonly Regex FuncName = new(@"\bfunc\s+(?<name>\w+)", RegexOptions.Compiled);

    /// <summary>Findet zu jedem „method replacement“-Konflikt die Mod(s), die dieselbe Funktion ebenfalls ersetzen.</summary>
    private void ResolveReplaceConflicts(Result r, List<(string Level, List<string> Lines)> entries)
    {
        var found = new List<(string File, string Cls, string? Method)>();
        foreach (var (_, lines) in entries)
        {
            if (!lines.Any(l => l.Contains("method replacement overwrites", StringComparison.OrdinalIgnoreCase))) continue;
            var at = ConflictAt.Match(lines[0]);
            var ann = lines.Select(l => ReplaceAnnotation.Match(l)).FirstOrDefault(m => m.Success);
            if (!at.Success || ann is null) continue;

            string? method = null;
            try
            {
                if (int.TryParse(at.Groups["line"].Value, out var ln) && File.Exists(at.Groups["file"].Value))
                    method = File.ReadLines(at.Groups["file"].Value).Skip(ln).Take(4)
                        .Select(l => FuncName.Match(l)).FirstOrDefault(m => m.Success)?.Groups["name"].Value;
            }
            catch { /* Datei nicht lesbar */ }
            found.Add((at.Groups["file"].Value, ann.Groups["cls"].Value, method));
        }
        if (found.Count == 0) return;

        // Alle @replaceMethod-Stellen aller Script-Mods einmal einlesen
        var all = new List<(string File, string Cls, string? Method)>();
        foreach (var dir in InAllRoots("r6", "scripts").Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.reds", SearchOption.AllDirectories).ToList(); }
            catch { continue; }
            foreach (var f in files)
            {
                try
                {
                    var text = File.ReadAllLines(f);
                    for (int i = 0; i < text.Length; i++)
                    {
                        var m = ReplaceAnnotation.Match(text[i]);
                        if (!m.Success) continue;
                        var fn = text.Skip(i + 1).Take(4).Select(l => FuncName.Match(l)).FirstOrDefault(x => x.Success)?.Groups["name"].Value;
                        all.Add((f, m.Groups["cls"].Value, fn));
                    }
                }
                catch { /* Datei nicht lesbar */ }
            }
        }

        foreach (var g in found.GroupBy(x => (x.File.ToLowerInvariant(), x.Cls, x.Method)))
        {
            var (file, cls, method) = g.Key;
            var self = ModNameOf(g.First().File);
            var others = all
                .Where(a => a.Cls == cls && a.Method == method && !string.Equals(a.File, g.First().File, StringComparison.OrdinalIgnoreCase))
                .Select(a => ModNameOf(a.File)).Where(n => !string.Equals(n, self, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
            r.RedscriptConflicts.Add(new RedscriptConflict(self, cls, method, others));
        }
    }

    /// <summary>Mod-Name zu einer .reds-Datei: Ordner unter r6\scripts (oder die Datei selbst, wenn sie direkt dort liegt).</summary>
    private string ModNameOf(string reds)
    {
        var m = Regex.Match(reds, @"r6[\\/]scripts[\\/](?<rest>.+)$", RegexOptions.IgnoreCase);
        if (!m.Success) return Path.GetFileNameWithoutExtension(reds);
        var parts = m.Groups["rest"].Value.Split('\\', '/');
        var name = parts.Length > 1 ? parts[0] : Path.GetFileNameWithoutExtension(parts[0]);
        // Mod Organizer 2: der Mod-Ordner liegt eine Ebene über r6\scripts
        foreach (var root in extraRoots ?? [])
        {
            if (reds.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !reds.StartsWith(install.Root, StringComparison.OrdinalIgnoreCase))
            {
                var rel = reds[root.Length..].TrimStart('\\', '/').Split('\\', '/');
                if (rel.Length > 0 && rel[0].Length > 0 && !rel[0].Equals("r6", StringComparison.OrdinalIgnoreCase)) return rel[0];
            }
        }
        return name;
    }

    // ---------- RED4ext ----------
    // [2026-11-20 17:01:05.010] [info    ] [ 17592] [RED4ext] message
    private static readonly Regex Red4ExtLine = new(@"^\[[^\]]+\]\s*\[(?<lvl>\w+)\s*\]\s*\[[^\]]*\]\s*\[(?<src>[^\]]+)\]\s*(?<msg>.*)$", RegexOptions.Compiled);

    private void ScanRed4Ext(Result r)
    {
        var file = NewestAcross("red4ext-*.log", "red4ext", "logs");
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
        var mainLog = FileAcross("bin", "x64", "plugins", "cyber_engine_tweaks", "cyber_engine_tweaks.log");
        if (mainLog is not null)
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

        var scripting = FileAcross("bin", "x64", "plugins", "cyber_engine_tweaks", "scripting.log");
        if (scripting is not null)
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
