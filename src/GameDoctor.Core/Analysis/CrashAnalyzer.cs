using System.Text.Json;
using GameDoctor.Core.Dump;
using GameDoctor.Core.Games.Cyberpunk;
using GameDoctor.Core.Platform;

namespace GameDoctor.Core.Analysis;

public sealed class CrashCase
{
    public required RedCrashReport Report { get; init; }
    public DumpAnalysis? Dump { get; init; }
    public List<Finding> Findings { get; } = [];
    public ModuleKind FaultKind { get; set; }
    public string Signature { get; set; } = "?";
    public string FaultLabel { get; set; } = "?";
    public List<(string Name, ModuleKind Kind, int Hits)> SuspectModules { get; } = [];
    public List<SystemEvent> NearbyEvents { get; } = [];
}

public sealed class AnalysisResult
{
    public CyberpunkInstall? Install { get; init; }
    public string? GameVersion { get; init; }
    public List<CrashCase> Crashes { get; } = [];
    public List<Finding> PatternFindings { get; } = [];
    public List<Finding> EnvironmentFindings { get; } = [];
    public ModLogScanner.Result? ModLogs { get; set; }
    public List<SystemEvent> SystemEvents { get; set; } = [];
}

public sealed class CrashAnalyzerOptions
{
    public string? GamePath { get; init; }
    public string? QueueDir { get; init; }
    /// <summary>Nur Abstürze der letzten N Tage berücksichtigen.</summary>
    public int Days { get; init; } = 30;
}

/// <summary>Führt alle Datenquellen zusammen und wendet die Diagnoseregeln an.</summary>
public static class CrashAnalyzer
{
    public static AnalysisResult Run(CrashAnalyzerOptions options, Action<string>? progress = null)
    {
        var install = options.GamePath is not null ? CyberpunkInstall.FromPath(options.GamePath) : CyberpunkInstall.Locate();
        var result = new AnalysisResult { Install = install, GameVersion = install?.GameVersion };
        var classifier = new ModuleClassifier(install?.Root);
        var since = DateTime.Now.AddDays(-options.Days);

        progress?.Invoke("Lese Windows-Ereignisanzeige …");
        result.SystemEvents = WindowsEventScanner.Scan("Cyberpunk2077.exe", TimeSpan.FromDays(options.Days));

        progress?.Invoke("Lese Crashreports …");
        var reports = RedCrashReport.LoadAll(options.QueueDir).Where(r => r.Time >= since).ToList();
        int i = 0;
        foreach (var report in reports)
        {
            progress?.Invoke($"Analysiere Absturz {++i}/{reports.Count}: {report.Time:dd.MM. HH:mm}");
            var dump = report.DumpPath is not null ? MinidumpReader.Analyze(report.DumpPath) : null;
            var c = new CrashCase { Report = report, Dump = dump };
            ApplyCrashRules(c, classifier, result.SystemEvents);
            result.Crashes.Add(c);
        }

        if (install is not null)
        {
            progress?.Invoke("Lese Mod-Logs …");
            result.ModLogs = new ModLogScanner(install).Scan();
        }

        ApplyPatternRules(result);
        ApplyEnvironmentRules(result, install);
        return result;
    }

    // ------------------------------------------------------------------
    // Regeln für einen einzelnen Absturz
    // ------------------------------------------------------------------
    private static void ApplyCrashRules(CrashCase c, ModuleClassifier classifier, List<SystemEvent> events)
    {
        var r = c.Report;
        var d = c.Dump;

        // Windows-Ereignisse im Zeitfenster um den Absturz
        c.NearbyEvents.AddRange(events.Where(e => e.Kind != SystemEventKind.AppCrash && Math.Abs((e.Time - r.Time).TotalMinutes) <= 3));

        if (d?.FaultingModule is { } fm)
        {
            c.FaultKind = classifier.Classify(fm);
            c.FaultLabel = classifier.FriendlyName(fm);
            c.Signature = $"{fm.Name}+0x{d.FaultingOffset:X}";
        }
        else if (d is not null && d.Error is null)
        {
            c.FaultKind = ModuleKind.Unknown;
            c.FaultLabel = $"keinem Modul zuordenbar (Adresse 0x{d.ExceptionAddress:X})";
            c.Signature = $"<kein Modul>:{d.ExceptionCode:X8}";
        }

        // Verdächtige Module auf dem Stack (nur Nicht-Vanilla)
        if (d is not null)
        {
            foreach (var g in d.StackHits.Take(40)
                         .Select(h => (Hit: h, Kind: classifier.Classify(h.Module)))
                         .Where(x => x.Kind is ModuleKind.Mod or ModuleKind.ModFramework or ModuleKind.Overlay or ModuleKind.ThirdParty or ModuleKind.GpuDriver)
                         .GroupBy(x => classifier.ResolvePath(x.Hit.Module), StringComparer.OrdinalIgnoreCase))
            {
                var first = g.First();
                c.SuspectModules.Add((classifier.FriendlyName(first.Hit.Module), first.Kind, g.Count()));
            }
        }

        var exceptionText = DescribeException(d, r);

        // R1: Absturz direkt in einer Mod oder einem Framework
        if (c.FaultKind is ModuleKind.Mod or ModuleKind.ModFramework)
        {
            c.Findings.Add(new Finding(Severity.Critical,
                $"Absturz direkt in der Mod „{c.FaultLabel}“",
                $"Der abstürzende Code liegt in {classifier.ResolvePath(d!.FaultingModule!)} (Version {d.FaultingModule!.FileVersion ?? "?"}). {exceptionText}",
                c.FaultKind == ModuleKind.ModFramework
                    ? "Framework (RED4ext/CET/ArchiveXL …) auf die neueste Version für deine Spielversion aktualisieren. Prüfen, ob ein Spiel-Update kürzlich kam – Frameworks brauchen dann meist ein Update."
                    : "Diese Mod aktualisieren oder testweise deaktivieren. Prüfen, ob sie zur aktuellen Spielversion passt und ihre Abhängigkeiten (RED4ext, Codeware, ArchiveXL, TweakXL) aktuell sind.")
            { Category = "Ursache" });
        }
        // R2: Grafiktreiber
        else if (c.FaultKind == ModuleKind.GpuDriver)
        {
            // Wer hat den Treiber aufgerufen? DLSS/Streamline/FSR/XeSS auf dem Stack?
            var upscaler = d!.StackHits.Select(h => h.Module.Name.ToLowerInvariant())
                .FirstOrDefault(n => n.StartsWith("nvngx_dlssg") || n.StartsWith("nvngx_dlss") || n.StartsWith("sl.") || n.StartsWith("amd_fidelityfx") || n.StartsWith("libxess"));
            if (upscaler is not null)
            {
                var tech = upscaler switch
                {
                    var n when n.StartsWith("nvngx_dlssg") || n.StartsWith("sl.dlss_g") => "DLSS Frame Generation",
                    var n when n.StartsWith("nvngx_dlss") || n.StartsWith("sl.") => "DLSS",
                    var n when n.StartsWith("amd_fidelityfx") => "AMD FSR",
                    _ => "Intel XeSS",
                };
                c.Findings.Add(new Finding(Severity.Critical,
                    $"Absturz im Grafiktreiber, ausgelöst durch {tech}",
                    $"Der Treiber ({c.FaultLabel}) stürzte ab, während {tech} ({upscaler}) ihn aufrief. {exceptionText}",
                    $"{tech} testweise ausschalten bzw. auf ein anderes Upscaling umstellen. Wurde die DLSS-DLL manuell ersetzt (z. B. DLSS Swapper), die Originaldatei wiederherstellen. Grafiktreiber aktualisieren oder sauber neu installieren.")
                { Category = "Ursache" });
                goto afterFault;
            }

            c.Findings.Add(new Finding(Severity.Critical,
                $"Absturz im Grafiktreiber ({c.FaultLabel})",
                $"Der Fehler trat im Grafiktreiber auf. {exceptionText} Häufige Ursachen: instabile GPU-Übertaktung/Undervolting, voller VRAM, fehlerhafte Treiberversion oder Overlays.",
                "Übertaktung/Undervolt der GPU testweise zurücksetzen, Treiber sauber neu installieren (oder eine Vorgängerversion testen), Raytracing/Pathtracing testweise ausschalten.")
            { Category = "Ursache" });
        }
        // R3: Overlay
        else if (c.FaultKind == ModuleKind.Overlay)
        {
            var faultPath = classifier.ResolvePath(d!.FaultingModule!);
            var product = ModuleClassifier.OverlayProduct(faultPath) ?? c.FaultLabel;
            c.Findings.Add(new Finding(Severity.Critical,
                $"Absturz in einem Overlay ({product})",
                $"Der Fehler trat in {faultPath} auf – einem Programm, das sich ins Spiel einklinkt. {exceptionText}",
                $"{product} für Cyberpunk deaktivieren und erneut testen.")
            { Category = "Ursache" });
        }
        // R4: Engine/Windows – Indizien vom Stack
        else if (d is not null && d.Error is null)
        {
            var mods = c.SuspectModules.Where(s => s.Kind is ModuleKind.Mod or ModuleKind.ModFramework or ModuleKind.Overlay).ToList();
            var where = c.FaultKind switch
            {
                ModuleKind.GameExecutable => "in der Spiel-Engine (Cyberpunk2077.exe)",
                ModuleKind.GameRuntime => $"in einer Spiel-Bibliothek ({c.FaultLabel})",
                ModuleKind.System => $"in einer Windows-Komponente ({c.FaultLabel})",
                _ => $"in {c.FaultLabel}",
            };
            if (mods.Count > 0)
            {
                c.Findings.Add(new Finding(Severity.Warning,
                    $"Absturz {where} – Mods im Aufrufpfad gefunden",
                    $"{exceptionText} Auf dem Stack des abstürzenden Threads liegen Rücksprungadressen in diesen Mods/Hooks. Das ist ein starkes Indiz, dass sie beteiligt waren (aber kein Beweis):",
                    "Die oben genannten Mods zuerst aktualisieren bzw. testweise deaktivieren.",
                    mods.Select(m => $"{m.Name} ({ModuleClassifier.KindLabel(m.Kind)}, {m.Hits}× auf dem Stack)").ToList())
                { Category = "Ursache" });
            }
            else
            {
                c.Findings.Add(new Finding(Severity.Info,
                    $"Absturz {where}",
                    $"{exceptionText} Auf dem Stack wurden keine Mods gefunden. Ohne Debug-Symbole von CD Projekt lässt sich die genaue Funktion nicht benennen – die Signatur {c.Signature} kann aber mit anderen Abstürzen verglichen werden.",
                    null)
                { Category = "Ursache" });
            }
        }
        else if (d?.Error is { } err)
        {
            c.Findings.Add(new Finding(Severity.Info, "Dump nicht auswertbar", err) { Category = "Ursache" });
        }
        afterFault:

        // R5: Speicher
        if (r.OutOfMemory)
            c.Findings.Add(new Finding(Severity.Critical, "Die Engine meldet „Out of Memory“",
                "Dem Spiel ist der Arbeitsspeicher ausgegangen.",
                "Auslagerungsdatei auf „automatisch“ stellen und genug Platz auf C: lassen (≥ 20 GB). Speicherhungrige Mods (Texturpakete, Crowd-Mods) reduzieren. Hintergrundprogramme schließen.")
            { Category = "Speicher" });

        if (r.VramUsedMb is int used && r.VramTotalMb is int total && total > 0 && used >= total * 0.92)
            c.Findings.Add(new Finding(Severity.Warning, "Grafikspeicher (VRAM) fast voll",
                $"Zum Absturzzeitpunkt waren {used} von {total} MB VRAM belegt ({100.0 * used / total:F0} %).",
                "Texturqualität senken, Raytracing/Pathtracing reduzieren oder hochauflösende Texturmods entfernen.")
            { Category = "Speicher" });

        // R6: Zeitpunkt
        if (r.UptimeSeconds is int up && up < 90)
            c.Findings.Add(new Finding(Severity.Warning, $"Absturz direkt nach dem Start ({up} s)",
                "Abstürze in den ersten Sekunden deuten fast immer auf ein Problem beim Laden von Mods/Plugins hin (falsche Version, fehlende Abhängigkeit).",
                "Mod-Logs (RED4ext, redscript, CET) prüfen – siehe Abschnitt „Mod-Umgebung“.")
            { Category = "Zeitpunkt" });
        else if (r.LoadingStage is { } stage && stage != "Finished")
            c.Findings.Add(new Finding(Severity.Hint, $"Absturz während eines Ladevorgangs ({stage})",
                "Das Spiel war noch beim Laden. Typisch für defekte Spielstände, Archive-Mods mit Fehlern oder Ressourcen-Konflikte.",
                "Einen älteren Spielstand testen; ArchiveXL-Log auf Fehler prüfen.")
            { Category = "Zeitpunkt" });

        // R7: GPU-Timeout um den Absturz herum
        if (c.NearbyEvents.Any(e => e.Kind == SystemEventKind.GpuTimeout))
            c.Findings.Add(new Finding(Severity.Critical, "Grafiktreiber-Timeout (TDR) zum Absturzzeitpunkt",
                "Windows hat den Grafiktreiber neu gestartet, weil die GPU nicht mehr reagierte.",
                "GPU-Übertaktung/Undervolt zurücksetzen, Temperaturen prüfen, Treiber neu installieren, Netzteil als Ursache in Betracht ziehen.")
            { Category = "Hardware" });
        if (c.NearbyEvents.Any(e => e.Kind == SystemEventKind.Whea))
            c.Findings.Add(new Finding(Severity.Critical, "Hardwarefehler (WHEA) zum Absturzzeitpunkt",
                "Windows hat einen Hardwarefehler protokolliert (CPU, RAM oder PCIe).",
                "Übertaktung von CPU/RAM (inkl. XMP/EXPO, Curve Optimizer) testweise deaktivieren und Stabilitätstest durchführen.")
            { Category = "Hardware" });

        // R8: Exception-spezifische Hinweise
        if (d is not null && ExceptionHint(d) is { } hint) c.Findings.Add(hint);
    }

    private static string DescribeException(DumpAnalysis? d, RedCrashReport r)
    {
        if (d is null || d.Error is not null) return r.Expression ?? "";
        var name = ExceptionName(d.ExceptionCode);
        if (d.ExceptionCode == 0xC0000005 && d.ExceptionParameters.Length >= 2)
        {
            var op = d.ExceptionParameters[0] switch { 0 => "lesen", 1 => "schreiben", 8 => "ausführen", _ => "zugreifen" };
            var addr = d.ExceptionParameters[1];
            return addr < 0x10000
                ? $"Speicherzugriffsfehler: Versuch, an Adresse 0x{addr:X} zu {op} – ein Null-Zeiger (ein Objekt, das es nicht gibt)."
                : $"Speicherzugriffsfehler: Versuch, an Adresse 0x{addr:X} zu {op} – auf ungültigen/freigegebenen Speicher.";
        }
        return $"{name} (0x{d.ExceptionCode:X8}).";
    }

    private static string ExceptionName(uint code) => code switch
    {
        0xC0000005 => "Speicherzugriffsfehler (Access Violation)",
        0xC00000FD => "Stapelüberlauf (Stack Overflow)",
        0xC0000409 => "Sicherheitsabbruch (Fail Fast / Stack Buffer Overrun)",
        0xC000001D => "Ungültige CPU-Anweisung (Illegal Instruction)",
        0xC0000096 => "Privilegierte Anweisung",
        0xC0000094 => "Division durch null",
        0xC0000374 => "Heap-Beschädigung",
        0xE06D7363 => "Unbehandelte C++-Ausnahme",
        0x80000003 => "Breakpoint / Assertion",
        0xC0000017 => "Kein Speicher mehr",
        0x887A0005 => "DirectX: Gerät entfernt (DXGI_ERROR_DEVICE_REMOVED)",
        0x887A0006 => "DirectX: Gerät hängt (DXGI_ERROR_DEVICE_HUNG)",
        _ => "Ausnahme",
    };

    private static Finding? ExceptionHint(DumpAnalysis d) => d.ExceptionCode switch
    {
        0xC000001D => new Finding(Severity.Warning, "Ungültige CPU-Anweisung",
            "Dieser Fehler entsteht häufig durch instabile CPU-Übertaktung/Undervolting (z. B. Curve Optimizer) oder sehr selten durch defekte Dateien.",
            "CPU-Übertaktung/Undervolt testweise zurücksetzen, Spieldateien prüfen lassen.") { Category = "Hardware" },
        0xC00000FD => new Finding(Severity.Warning, "Stapelüberlauf",
            "Eine Endlos-Rekursion – sehr oft in Skript-Mods (redscript/CET), die sich gegenseitig aufrufen.",
            "Kürzlich installierte Skript-Mods prüfen; redscript-Log auf Konflikte („method replacement“) prüfen.") { Category = "Ursache" },
        0xC0000374 => new Finding(Severity.Warning, "Heap-Beschädigung",
            "Speicher wurde von fehlerhaftem Code überschrieben. Der eigentliche Verursacher ist oft nicht das abstürzende Modul.",
            "Native Mods (RED4ext-Plugins) einzeln deaktivieren, um den Verursacher einzugrenzen.") { Category = "Ursache" },
        0x887A0005 or 0x887A0006 => new Finding(Severity.Critical, "DirectX-Gerät verloren",
            "Die GPU wurde vom Treiber zurückgesetzt.",
            "GPU-Übertaktung zurücksetzen, Treiber neu installieren, Temperaturen und Netzteil prüfen.") { Category = "Hardware" },
        _ => null,
    };

    // ------------------------------------------------------------------
    // Regeln über alle Abstürze hinweg
    // ------------------------------------------------------------------
    private static void ApplyPatternRules(AnalysisResult result)
    {
        var crashes = result.Crashes;
        if (crashes.Count == 0) return;

        var last7 = crashes.Count(c => c.Report.Time >= DateTime.Now.AddDays(-7));
        if (last7 >= 5)
            result.PatternFindings.Add(new Finding(Severity.Warning, $"{last7} Abstürze in den letzten 7 Tagen",
                "Das Spiel stürzt regelmäßig ab – es handelt sich nicht um einen Einzelfall."));

        // Gleiche Signatur mehrfach
        foreach (var g in crashes.Where(c => c.Signature != "?").GroupBy(c => c.Signature).Where(g => g.Count() >= 2).OrderByDescending(g => g.Count()).Take(5))
        {
            var first = g.First();
            result.PatternFindings.Add(new Finding(
                g.Count() >= 3 ? Severity.Warning : Severity.Hint,
                $"Gleicher Absturz {g.Count()}× wiederholt: {g.Key}",
                $"Immer an derselben Stelle ({ModuleClassifier.KindLabel(first.FaultKind)}: {first.FaultLabel}). Das ist ein reproduzierbarer Fehler, kein Zufall – meist eine bestimmte Mod oder ein bestimmter Ort/Spielstand.",
                null,
                g.Select(c => c.Report.Time.ToString("dd.MM.yyyy HH:mm")).ToList()));
        }

        // Absturzstelle fest an eine Mod gekoppelt: dieselbe Signatur, und fast jedes Mal dieselbe Mod auf dem Stack
        foreach (var g in crashes.Where(c => c.Signature != "?" && c.FaultKind is ModuleKind.GameExecutable or ModuleKind.GameRuntime or ModuleKind.System)
                     .GroupBy(c => c.Signature).Where(g => g.Count() >= 3))
        {
            var coupled = g.SelectMany(c => c.SuspectModules.Where(s => s.Kind is ModuleKind.Mod or ModuleKind.Overlay).Select(s => s.Name).Distinct())
                .GroupBy(n => n).Select(x => (Name: x.Key, Count: x.Count()))
                .Where(x => x.Count >= 3 && x.Count >= 0.8 * g.Count())
                .OrderByDescending(x => x.Count).FirstOrDefault();
            if (coupled.Name is null) continue;
            result.PatternFindings.Insert(0, new Finding(Severity.Critical,
                $"Absturzstelle {g.Key} ist an „{coupled.Name}“ gekoppelt",
                $"Diese Absturzstelle trat {g.Count()}× auf – und {coupled.Count}× davon lag „{coupled.Name}“ im Aufrufpfad. " +
                "Eine so feste Kopplung ist kein Zufall: Die Mod ruft diesen Teil der Engine auf und übergibt dabei offenbar ungültige Daten.",
                $"„{coupled.Name}“ testweise deaktivieren. Bleiben diese Abstürze danach aus, ist die Ursache bestätigt – dann nach einem Update suchen oder die Mod weglassen.",
                g.Select(c => c.Report.Time.ToString("dd.MM.yyyy HH:mm")).ToList())
            { Category = "Ursache" });
        }

        // Abstürze häufen sich an einem Ort
        ApplyLocationRules(result);

        // Nach Kategorie
        var byKind = crashes.Where(c => c.Dump?.Error is null && c.Dump is not null).GroupBy(c => c.FaultKind)
            .Select(g => $"{ModuleClassifier.KindLabel(g.Key)}: {g.Count()}×").ToList();
        if (byKind.Count > 0)
            result.PatternFindings.Add(new Finding(Severity.Info, "Wo die Abstürze passieren",
                "Verteilung nach abstürzendem Modul:", null, byKind));

        // Welche Mods tauchen über viele Abstürze hinweg auf dem Stack auf?
        var modFreq = crashes
            .SelectMany(c => c.SuspectModules.Where(s => s.Kind is ModuleKind.Mod or ModuleKind.Overlay).Select(s => s.Name).Distinct())
            .GroupBy(n => n).Select(g => (Name: g.Key, Count: g.Count()))
            .Where(x => x.Count >= 2).OrderByDescending(x => x.Count).Take(8).ToList();
        if (modFreq.Count > 0)
            result.PatternFindings.Add(new Finding(Severity.Warning, "Mods, die bei mehreren Abstürzen im Aufrufpfad lagen",
                "Diese Mods tauchten bei mehreren Abstürzen auf dem Stack auf. Je öfter, desto verdächtiger:",
                "Mit dem Spitzenreiter anfangen: aktualisieren oder testweise deaktivieren.",
                modFreq.Select(m => $"{m.Name}: bei {m.Count} von {crashes.Count} Abstürzen").ToList()));

        // Versionen
        var versions = crashes.Select(c => c.Report.InternalVersion).Where(v => v is not null).Distinct().ToList();
        if (versions.Count > 1)
            result.PatternFindings.Add(new Finding(Severity.Info, "Spielversion hat sich im Zeitraum geändert",
                "Die Abstürze stammen aus verschiedenen Spielversionen – nach einem Update brauchen viele Mods selbst ein Update.",
                null, versions!));
    }

    // Bekannte Orte (Weltkoordinaten x/y, Radius in Metern) – bewusst nur sicher zugeordnete Einträge
    private static readonly (string Name, double X, double Y, double Radius)[] KnownPlaces =
    [
        ("Megagebäude H10 (Vs Wohnung, Watson)", -1370, 1270, 120),
    ];

    private const double ClusterRadius = 150;

    private static void ApplyLocationRules(AnalysisResult result)
    {
        var located = result.Crashes.Where(c => c.Report.Position is not null).ToList();
        if (located.Count < 3) return;

        static double Dist((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
            Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

        var clusters = new List<List<CrashCase>>();
        foreach (var c in located.OrderBy(c => c.Report.Time))
        {
            var pos = c.Report.Position!.Value;
            var cluster = clusters.FirstOrDefault(k => k.Any(o => Dist(o.Report.Position!.Value, pos) < ClusterRadius));
            if (cluster is null) clusters.Add([c]); else cluster.Add(c);
        }

        foreach (var cl in clusters.Where(k => k.Count >= 3).OrderByDescending(k => k.Count).Take(4))
        {
            double cx = cl.Average(c => c.Report.Position!.Value.X), cy = cl.Average(c => c.Report.Position!.Value.Y);
            var place = KnownPlaces.FirstOrDefault(p => Math.Sqrt(Math.Pow(p.X - cx, 2) + Math.Pow(p.Y - cy, 2)) < p.Radius + ClusterRadius / 2).Name;
            var where = place ?? $"bei Koordinaten [{cx:F0}, {cy:F0}]";

            int sameSpot = cl.Count(c => cl.Count(o => Dist(o.Report.Position!.Value, c.Report.Position!.Value) < 10) >= 2);
            int shortUptime = cl.Count(c => c.Report.UptimeSeconds is < 300);
            var signatures = cl.GroupBy(c => c.Signature).OrderByDescending(g => g.Count()).ToList();

            var detail = $"{cl.Count} von {located.Count} Abstürzen mit bekannter Position passierten {where} (Umkreis {ClusterRadius:F0} m). ";
            string rec;
            if (sameSpot >= 3 && shortUptime >= cl.Count / 2)
            {
                detail += $"Mehrere davon an exakt derselben Stelle und kurz nach dem Laden – typisch für einen Spielstand, der genau dort steht und beim Laden immer wieder abstürzt.";
                rec = "Einen älteren Spielstand an einem anderen Ort laden. Hilft das, liegt es an Objekten/Mods, die an diesem Ort geladen werden.";
            }
            else
            {
                detail += "Ein Ort, an dem es immer wieder kracht, deutet auf Mods hin, die dort Inhalte hinzufügen (Garagen, Wohnungs-Deko, Fahrzeuge, NPCs) – oder auf eine dort geladene Szene, die mit Mods kollidiert.";
                rec = "Prüfen, welche Mods diesen Ort verändern (z. B. Garagen-, Wohnungs- oder Fahrzeug-Mods) und diese testweise deaktivieren. Hinweise auf defekte Mod-Inhalte stehen unter „Mods & System“.";
            }
            result.PatternFindings.Add(new Finding(cl.Count >= 4 ? Severity.Warning : Severity.Hint,
                $"Abstürze häufen sich an einem Ort: {where} ({cl.Count}×)",
                detail, rec,
                cl.Select(c => $"{c.Report.Time:dd.MM. HH:mm}  Höhe {c.Report.Position!.Value.Z,4:F0} m  {c.Signature}  nach {TimeSpan.FromSeconds(c.Report.UptimeSeconds ?? 0):h\\:mm\\:ss}").ToList())
            { Category = "Ort" });
        }
    }

    // ------------------------------------------------------------------
    // Regeln zur Umgebung (Mods, System)
    // ------------------------------------------------------------------
    private static void ApplyEnvironmentRules(AnalysisResult result, CyberpunkInstall? install)
    {
        if (install is null)
        {
            result.EnvironmentFindings.Add(new Finding(Severity.Hint, "Cyberpunk-Installation nicht gefunden",
                "Ohne Spielordner können Mod-Logs nicht geprüft werden.", "Pfad mit --game angeben."));
        }

        if (result.ModLogs is { } logs)
        {
            // Versionen der Frameworks gegen das Spiel
            var gameFile = result.GameVersion;
            if (logs.CetGameVersion is { } cetGame && gameFile is not null && !VersionsMatch(cetGame, gameFile))
                result.EnvironmentFindings.Add(new Finding(Severity.Critical, "Cyber Engine Tweaks passt nicht zur Spielversion",
                    $"CET meldet Spielversion {cetGame}, installiert ist {gameFile}.",
                    "CET auf die neueste Version aktualisieren.") { Category = "Mods" });

            var critical = logs.Issues.Where(i => i.Severity == Severity.Critical).ToList();
            if (critical.Count > 0)
                result.EnvironmentFindings.Add(new Finding(Severity.Critical, $"{critical.Count} schwere Fehler in Mod-Logs",
                    "Diese Fehler verhindern, dass Mods korrekt geladen werden:", "Betroffene Mods aktualisieren oder entfernen.",
                    critical.Take(10).Select(Describe).ToList()) { Category = "Mods" });

            var conflicts = logs.Issues.Where(i => i.Source == "redscript" && i.Message.Contains("method replacement", StringComparison.OrdinalIgnoreCase)).ToList();
            if (conflicts.Count > 0)
                result.EnvironmentFindings.Add(new Finding(Severity.Warning, $"{conflicts.Sum(c => c.Count)} redscript-Konflikte",
                    "Mehrere Mods ersetzen dieselbe Spielfunktion – nur eine davon wirkt. Das führt zu Fehlverhalten und kann Abstürze begünstigen.",
                    "Bei Konflikten prüfen, ob die Mods als kompatibel gelten; ggf. eine davon entfernen.",
                    conflicts.GroupBy(c => c.Mod ?? "?").Select(g => $"{g.Key}: {g.Sum(x => x.Count)} Konflikt(e)").ToList()) { Category = "Mods" });

            // Mod-Inhalte, die das Spiel nicht findet (z. B. Garagen ohne geladene Welt-Sektoren)
            foreach (var u in logs.UnresolvedContent.Where(u => u.Items.Count >= 2).OrderByDescending(u => u.Items.Count))
            {
                var modHint = FindModFiles(install, u.Owner);
                result.EnvironmentFindings.Add(new Finding(Severity.Warning,
                    $"Mod-Inhalte werden nicht gefunden: „{u.Owner}“ ({u.Items.Count} Einträge)",
                    $"{u.Source} meldet, dass {u.Items.Count} Verweise dieser Mod (Orte, Kartenmarkierungen, Journal-Einträge) ins Leere zeigen. " +
                    "Die Mod ist also nur teilweise geladen – meist wegen einer veralteten Version, einer fehlenden Abhängigkeit oder einer unvollständigen Installation. " +
                    "An den Orten, die diese Mod verändert, steigt das Absturzrisiko." +
                    (modHint.Count > 0 ? $" Zugehörige Dateien: {string.Join(", ", modHint.Take(4))}{(modHint.Count > 4 ? " …" : "")}." : ""),
                    "Mod neu installieren bzw. aktualisieren und Abhängigkeiten auf der Mod-Seite prüfen. Bis dahin testweise deaktivieren – besonders, wenn Abstürze an diesen Orten auftreten.",
                    u.Items.Take(12).ToList())
                { Category = "Mods" });
            }

            var warnings = logs.Issues.Where(i => i.Severity == Severity.Warning).OrderByDescending(i => i.Count).ToList();
            if (warnings.Count > 0)
                result.EnvironmentFindings.Add(new Finding(Severity.Hint, $"{warnings.Count} verschiedene Fehlermeldungen in Mod-Logs",
                    "Hintergrundfehler, die nicht zwingend zum Absturz führen (häufigste zuerst):", null,
                    warnings.Take(12).Select(Describe).ToList()) { Category = "Mods" });

            int total = logs.ArchiveModCount + logs.Red4ExtPluginCount + logs.RedscriptModCount + logs.CetModCount;
            result.EnvironmentFindings.Add(new Finding(total > 300 ? Severity.Hint : Severity.Info, "Mod-Umfang",
                $"{logs.ArchiveModCount} Archive-Mods, {logs.Red4ExtPluginCount} RED4ext-Plugins, {logs.RedscriptModCount} redscript-Mods, {logs.CetModCount} CET-Mods.",
                total > 300 ? "Bei so vielen Mods steigt die Wahrscheinlichkeit für Konflikte und Speicherprobleme deutlich. Zum Eingrenzen Mods in Gruppen deaktivieren (Halbierungsmethode)." : null)
            { Category = "Mods" });
        }

        // Systeminformationen aus dem neuesten Crashreport
        var sys = result.Crashes.Select(c => c.Report.SysInfo).FirstOrDefault(s => s is not null);
        if (sys is { } s) ApplySystemRules(result, s, install);

        // Windows-Ereignisse allgemein
        var tdr = result.SystemEvents.Count(e => e.Kind == SystemEventKind.GpuTimeout);
        if (tdr > 0)
            result.EnvironmentFindings.Add(new Finding(Severity.Warning, $"{tdr} Grafiktreiber-Probleme in der Ereignisanzeige",
                "Windows hat Grafiktreiber-Timeouts oder -Fehler protokolliert.", "GPU-Übertaktung, Temperaturen und Treiberversion prüfen.",
                result.SystemEvents.Where(e => e.Kind == SystemEventKind.GpuTimeout).Take(5).Select(e => $"{e.Time:dd.MM. HH:mm} – {e.Summary}").ToList())
            { Category = "Hardware" });
        var whea = result.SystemEvents.Count(e => e.Kind == SystemEventKind.Whea);
        if (whea > 0)
            result.EnvironmentFindings.Add(new Finding(Severity.Warning, $"{whea} Hardwarefehler (WHEA) in der Ereignisanzeige",
                "Hinweis auf instabile Hardware (oft Übertaktung von CPU/RAM).", "XMP/EXPO und CPU-Übertaktung testweise deaktivieren.",
                result.SystemEvents.Where(e => e.Kind == SystemEventKind.Whea).Take(5).Select(e => $"{e.Time:dd.MM. HH:mm} – {e.Summary}").ToList())
            { Category = "Hardware" });
        var kp = result.SystemEvents.Count(e => e.Kind == SystemEventKind.UnexpectedShutdown);
        if (kp > 0)
            result.EnvironmentFindings.Add(new Finding(Severity.Warning, $"{kp}× unerwarteter Neustart des PCs",
                "Der PC ging ohne sauberes Herunterfahren aus. Beim Spielen oft Netzteil, Temperatur oder Übertaktung.", null,
                result.SystemEvents.Where(e => e.Kind == SystemEventKind.UnexpectedShutdown).Take(5).Select(e => e.Time.ToString("dd.MM.yyyy HH:mm")).ToList())
            { Category = "Hardware" });
    }

    /// <summary>Sucht Mod-Dateien/-Ordner, deren Name zum Kürzel passt (z. B. „icm_garages“ → ICM_Garage_*.archive).</summary>
    private static List<string> FindModFiles(CyberpunkInstall? install, string owner)
    {
        if (install is null) return [];
        // Kürzel auf den Kern reduzieren: "icm_garages" → "icm_garage"
        var token = owner.ToLowerInvariant().TrimEnd('s');
        var found = new List<string>();
        foreach (var dir in new[] { install.ArchiveModDir, install.RedscriptDir, Path.Combine(install.CetDir, "mods"), Path.Combine(install.Root, "r6", "tweaks") })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var e in Directory.EnumerateFileSystemEntries(dir))
            {
                var name = Path.GetFileName(e);
                if (name.ToLowerInvariant().Replace(" ", "_").Contains(token)) found.Add(name);
            }
        }
        return found;
    }

    private static string Describe(LogIssue i) =>
        $"[{i.Source}{(i.Mod is not null ? " · " + i.Mod : "")}] {i.Message}{(i.Count > 1 ? $" ({i.Count}×)" : "")}";

    private static bool VersionsMatch(string a, string b)
    {
        // CET meldet z. B. "3.0.88.46235", die EXE-Dateiversion kann anders formatiert sein -> nur vergleichen, wenn gleiche Form
        var pa = a.Split('.');
        var pb = b.Split('.');
        if (pa.Length != pb.Length) return true;
        return a == b;
    }

    private static void ApplySystemRules(AnalysisResult result, JsonElement sys, CyberpunkInstall? install)
    {
        try
        {
            if (sys.TryGetProperty("TotalPhysicalMemory", out var tm))
            {
                double gb = tm.GetInt64() / 1024d / 1024 / 1024;
                if (gb < 15.5)
                    result.EnvironmentFindings.Add(new Finding(Severity.Warning, $"Nur {gb:F0} GB Arbeitsspeicher",
                        "Cyberpunk 2.x empfiehlt mindestens 16 GB, mit vielen Mods eher 32 GB.", null) { Category = "System" });
                else if (gb < 31 && result.ModLogs is { ArchiveModCount: > 200 })
                    result.EnvironmentFindings.Add(new Finding(Severity.Hint, $"{gb:F0} GB RAM bei sehr vielen Mods",
                        "Mit mehreren hundert Mods kann es mit 16 GB eng werden (Nachladeruckler, Out-of-Memory).",
                        "Auslagerungsdatei auf „automatisch“ lassen, Browser/Discord beim Spielen schließen.") { Category = "System" });
            }

            if (sys.TryGetProperty("MemoryBanks", out var banks) && banks.ValueKind == JsonValueKind.Array)
            {
                int count = banks.GetArrayLength();
                if (count == 1)
                    result.EnvironmentFindings.Add(new Finding(Severity.Warning, "RAM läuft im Single-Channel",
                        "Es ist nur ein Speichermodul verbaut. Das kostet in CPU-lastigen Spielen wie Cyberpunk spürbar FPS.",
                        "Zweites, baugleiches Modul ergänzen.") { Category = "Performance" });
                var speeds = banks.EnumerateArray().Select(b => b.TryGetProperty("Speed", out var sp) && sp.ValueKind == JsonValueKind.Number ? sp.GetInt32() : 0).Where(x => x > 0).ToList();
                if (speeds.Count > 0 && speeds.Min() <= 2666)
                    result.EnvironmentFindings.Add(new Finding(Severity.Hint, $"RAM läuft mit {speeds.Min()} MT/s",
                        "Aktuelle CPUs (besonders AMD Ryzen) profitieren deutlich von schnellerem RAM (3200–3600 MT/s bei DDR4).",
                        "Im BIOS prüfen, ob das XMP/DOCP-Profil aktiv ist. Ist der RAM selbst nur für diesen Takt spezifiziert, bringt schnellerer RAM spürbar mehr FPS.") { Category = "Performance" });
            }

            // Freier Platz auf C: (Auslagerungsdatei, Shader-Cache)
            if (sys.TryGetProperty("Drives", out var drives))
            {
                foreach (var dr in drives.EnumerateArray())
                {
                    var name = dr.GetProperty("Name").GetString();
                    long free = dr.GetProperty("AvailableFreeSpace").GetInt64();
                    long size = dr.GetProperty("TotalSize").GetInt64();
                    if (name?.StartsWith("C", StringComparison.OrdinalIgnoreCase) == true && free < 25L * 1024 * 1024 * 1024)
                        result.EnvironmentFindings.Add(new Finding(free < 10L * 1024 * 1024 * 1024 ? Severity.Warning : Severity.Hint,
                            $"Wenig Platz auf C: ({free / 1024d / 1024 / 1024:F0} GB frei von {size / 1024d / 1024 / 1024:F0} GB)",
                            "Die Auslagerungsdatei und der Shader-Cache liegen auf C:. Ist der Platz knapp, kann Windows den virtuellen Speicher nicht vergrößern – das führt zu Abstürzen ohne klare Fehlermeldung.",
                            "Mindestens 25–30 GB auf C: freihalten.") { Category = "System" });
                }

                // Liegt das Spiel auf einer HDD?
                if (install is not null && sys.TryGetProperty("PhysicalDisks", out var disks))
                {
                    var letter = Path.GetPathRoot(install.Root)?.TrimEnd('\\');
                    var drive = drives.EnumerateArray().FirstOrDefault(x => x.GetProperty("Name").GetString()?.TrimEnd('\\').Equals(letter, StringComparison.OrdinalIgnoreCase) == true);
                    if (drive.ValueKind == JsonValueKind.Object && drive.TryGetProperty("PhysicalDiskDeviceId", out var pid))
                    {
                        var idx = pid.GetString()?.Replace(@"\\.\PHYSICALDRIVE", "");
                        var disk = disks.EnumerateArray().FirstOrDefault(x => x.GetProperty("DeviceId").GetString() == idx);
                        if (disk.ValueKind == JsonValueKind.Object && disk.TryGetProperty("MediaType", out var mt) && mt.GetInt32() == 3)
                            result.EnvironmentFindings.Add(new Finding(Severity.Critical, "Spiel liegt auf einer Festplatte (HDD)",
                                "Cyberpunk 2.x setzt eine SSD voraus. Auf einer HDD gibt es Nachladeruckler, fehlende Texturen und Abstürze beim Streaming.",
                                "Spiel auf eine SSD verschieben.") { Category = "Performance" });
                        else if (disk.ValueKind == JsonValueKind.Object)
                            result.EnvironmentFindings.Add(new Finding(Severity.Info, "Spiel liegt auf einer SSD",
                                $"{disk.GetProperty("FriendlyName").GetString()} – gut.", null) { Category = "System" });
                    }
                }
            }
        }
        catch
        {
            // sysinfo.json in unerwartetem Format – Systemregeln überspringen
        }
    }
}
