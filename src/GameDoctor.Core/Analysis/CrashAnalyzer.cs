using System.Text.Json;
using GameDoctor.Core.Dump;
using GameDoctor.Core.Games.Cyberpunk;
using GameDoctor.Core.Localization;
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
    /// <summary>Installed = false: die Mod-Datei liegt nicht mehr im Spiel (alte, inzwischen entfernte Mod).</summary>
    public List<(string Name, ModuleKind Kind, int Hits, bool Installed)> SuspectModules { get; } = [];
    public List<SystemEvent> NearbyEvents { get; } = [];
    /// <summary>Funktionsnamen der Absturzstelle und des Aufrufpfads – nur wenn die Namen geladen sind und zur Spielversion passen.</summary>
    public Symbols.CrashSymbols? Symbols { get; set; }
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
    /// <summary>Installierte Mods laut Mod-Manager (Vortex, Mod Organizer 2) und REDmod.</summary>
    public Community.ModInventory Inventory { get; set; } = new();
    /// <summary>Modul-Einordnung inkl. virtueller Mod-Ordner – zur Wiederverwendung (App, CLI).</summary>
    public ModuleClassifier Classifier { get; set; } = new(null);
    /// <summary>Welche Fundorte für das Spiel geprüft wurden.</summary>
    public List<LocateProbe> LocateProbes { get; set; } = [];
    /// <summary>Welche Datenquellen gefunden wurden und welche fehlen.</summary>
    public List<DataSource> DataSources { get; set; } = [];
    /// <summary>Ob die optionalen Funktionsnamen geladen sind und zur Spielversion passen.</summary>
    public Symbols.SymbolStatus SymbolStatus { get; set; }
    public Symbols.SymbolIndex? SymbolIndex { get; set; }
}

public sealed class CrashAnalyzerOptions
{
    public string? GamePath { get; init; }
    public string? QueueDir { get; init; }
    /// <summary>Nur Abstürze der letzten N Tage berücksichtigen.</summary>
    public int Days { get; init; } = 30;
}

/// <summary>Führt alle Datenquellen zusammen und wendet die Diagnoseregeln an. Alle Texte laufen über <see cref="L"/>.</summary>
public static class CrashAnalyzer
{
    public static AnalysisResult Run(CrashAnalyzerOptions options, Action<string>? progress = null)
    {
        progress?.Invoke(L.T("Suche Spiel und Mod-Manager …"));
        var paths = PathSettings.Load();
        var probes = new List<LocateProbe>();
        var install = options.GamePath is not null
            ? CyberpunkInstall.FromPath(options.GamePath, "Festgelegt (Kommandozeile)")
            : CyberpunkInstall.Locate(paths, out probes);
        var inventory = install is null ? new Community.ModInventory() : Community.ModInventory.Detect(install, paths);
        var classifier = new ModuleClassifier(install?.Root, inventory.ExtraRoots);
        var result = new AnalysisResult
        {
            Install = install,
            GameVersion = install?.GameVersion,
            Inventory = inventory,
            Classifier = classifier,
            LocateProbes = probes,
        };
        // Days = 0: nur der heutige Tag (ab Mitternacht)
        var since = options.Days <= 0 ? DateTime.Today : DateTime.Now.AddDays(-options.Days);
        // Welche Mod-Dateien sind gerade wirklich im Spiel? Damit lassen sich Module alter, entfernter Mods kennzeichnen
        var installedFiles = install is null ? null : Community.FixEvaluator.IndexModFiles(install, inventory.ExtraRoots);

        progress?.Invoke(L.T("Lese Windows-Ereignisanzeige …"));
        result.SystemEvents = WindowsEventScanner.Scan("Cyberpunk2077.exe", DateTime.Now - since);

        // Optionale Funktionsnamen (Community-Daten): benennen Absturzstellen und ordnen sie Teilsystemen zu
        var symbols = Symbols.SymbolDatabase.Open(install?.Root, out var symbolStatus);
        result.SymbolStatus = symbolStatus;
        result.SymbolIndex = symbols;
        bool haveSymbols = symbolStatus == Symbols.SymbolStatus.Ready && symbols is not null;

        progress?.Invoke(L.T("Lese Crashreports …"));
        var reports = RedCrashReport.LoadAll(options.QueueDir).Where(r => r.Time >= since).ToList();
        int i = 0;
        foreach (var report in reports)
        {
            progress?.Invoke(L.F("Analysiere Absturz {0}/{1}: {2:dd.MM. HH:mm}", ++i, reports.Count, report.Time));
            var dump = report.DumpPath is not null ? MinidumpReader.Analyze(report.DumpPath) : null;
            var c = new CrashCase { Report = report, Dump = dump };
            if (haveSymbols && dump is not null) c.Symbols = Symbols.SymbolDatabase.Describe(dump, symbols!);
            ApplyCrashRules(c, classifier, result.SystemEvents, installedFiles);
            result.Crashes.Add(c);
        }

        if (install is not null)
        {
            progress?.Invoke(L.T("Lese Mod-Logs …"));
            result.ModLogs = new ModLogScanner(install, inventory.ExtraRoots).Scan();
        }

        ApplyPatternRules(result);
        ApplySubsystemPattern(result);
        ApplyEnvironmentRules(result, install);
        result.DataSources = DataSourceReport.Build(result);
        return result;
    }

    // ------------------------------------------------------------------
    // Regeln für einen einzelnen Absturz
    // ------------------------------------------------------------------
    private static bool IsArchiveXl(string name) => name.Contains("archivexl", StringComparison.OrdinalIgnoreCase);

    private static void ApplyCrashRules(CrashCase c, ModuleClassifier classifier, List<SystemEvent> events, HashSet<string>? installedFiles)
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
            c.FaultLabel = L.F("keinem Modul zuordenbar (Adresse 0x{0:X})", d.ExceptionAddress);
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
                // Nur Mod-DLLs können „nicht mehr installiert“ sein (Treiber, Overlays usw. liegen außerhalb des Spielordners)
                bool installed = installedFiles is null || first.Kind is not (ModuleKind.Mod or ModuleKind.ModFramework)
                    || installedFiles.Contains(first.Hit.Module.Name);
                c.SuspectModules.Add((classifier.FriendlyName(first.Hit.Module), first.Kind, g.Count(), installed));
            }
        }

        var exceptionText = DescribeException(d, r);

        // R1: Absturz direkt in einer Mod oder einem Framework
        if (c.FaultKind is ModuleKind.Mod or ModuleKind.ModFramework && IsArchiveXl(c.FaultLabel ?? ""))
        {
            // ArchiveXL lädt die Inhalte anderer Mods – ein Absturz dort liegt meist an deren Dateien, nicht an ArchiveXL selbst
            c.Findings.Add(new Finding(Severity.Warning,
                L.T("Absturz in ArchiveXL – vermutlich wegen einer Mod, die ArchiveXL nutzt"),
                L.F("Der abstürzende Code liegt in {0}. {1} ArchiveXL lädt die Inhalte anderer Mods; ein Absturz dort wird meist durch fehlerhafte oder veraltete Mod-Dateien ausgelöst und selten durch ArchiveXL selbst.", classifier.ResolvePath(d!.FaultingModule!), exceptionText),
                L.T("ArchiveXL auf die neueste Version bringen und kürzlich installierte oder aktualisierte Mods mit eigenen Inhalten (Kleidung, Fahrzeuge, Frisuren …) testweise deaktivieren. Die ArchiveXL-Logdatei nennt oft die betroffene Mod."))
            {
                Category = "Ursache",
                PlainTitle = L.T("Der Absturz passierte beim Laden von Mod-Inhalten"),
                Plain = L.T("ArchiveXL lädt Inhalte anderer Mods. Meist liegt der Fehler nicht an ArchiveXL, sondern an einer Mod, die es nutzt – zum Beispiel an veralteten Kleidungs- oder Fahrzeug-Mods. ArchiveXL selbst musst du in der Regel nicht entfernen."),
            });
        }
        else if (c.FaultKind is ModuleKind.Mod or ModuleKind.ModFramework)
        {
            c.Findings.Add(new Finding(Severity.Critical,
                L.F("Absturz direkt in der Mod „{0}“", c.FaultLabel),
                L.F("Der abstürzende Code liegt in {0} (Version {1}). {2}", classifier.ResolvePath(d!.FaultingModule!), d.FaultingModule!.FileVersion ?? "?", exceptionText),
                c.FaultKind == ModuleKind.ModFramework
                    ? L.T("Framework (RED4ext/CET/ArchiveXL …) auf die neueste Version für deine Spielversion aktualisieren. Prüfen, ob ein Spiel-Update kürzlich kam – Frameworks brauchen dann meist ein Update.")
                    : L.T("Diese Mod aktualisieren oder testweise deaktivieren. Prüfen, ob sie zur aktuellen Spielversion passt und ihre Abhängigkeiten (RED4ext, Codeware, ArchiveXL, TweakXL) aktuell sind."))
            {
                Category = "Ursache",
                PlainTitle = L.F("Der Absturz passierte in der Mod „{0}“", c.FaultLabel),
                Plain = L.F("Der Absturz geschah direkt im Programmcode von „{0}“ – sehr wahrscheinlich ist diese Mod die Ursache.", c.FaultLabel),
            });
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
                    L.F("Absturz im Grafiktreiber, ausgelöst durch {0}", tech),
                    L.F("Der Treiber ({0}) stürzte ab, während {1} ({2}) ihn aufrief. {3}", c.FaultLabel, tech, upscaler, exceptionText),
                    L.F("{0} testweise ausschalten bzw. auf ein anderes Upscaling umstellen. Wurde die DLSS-DLL manuell ersetzt (z. B. DLSS Swapper), die Originaldatei wiederherstellen. Grafiktreiber aktualisieren oder sauber neu installieren.", tech))
                {
                    Category = "Ursache",
                    PlainTitle = L.F("Absturz durch die Grafiktechnik {0}", tech),
                    Plain = L.F("Der Grafiktreiber ist abgestürzt, während {0} gearbeitet hat. Meist hilft es, {0} auszuschalten und den Grafiktreiber neu zu installieren.", tech),
                });
                goto afterFault;
            }

            c.Findings.Add(new Finding(Severity.Critical,
                L.F("Absturz im Grafiktreiber ({0})", c.FaultLabel),
                L.F("Der Fehler trat im Grafiktreiber auf. {0} Häufige Ursachen: instabile GPU-Übertaktung/Undervolting, voller VRAM, fehlerhafte Treiberversion oder Overlays.", exceptionText),
                L.T("Übertaktung/Undervolt der GPU testweise zurücksetzen, Treiber sauber neu installieren (oder eine Vorgängerversion testen), Raytracing/Pathtracing testweise ausschalten."))
            {
                Category = "Ursache",
                PlainTitle = L.T("Absturz im Grafiktreiber"),
                Plain = L.T("Der Grafiktreiber ist abgestürzt. Häufige Gründe: eine übertaktete Grafikkarte, voller Grafikspeicher oder ein fehlerhafter Treiber."),
            });
        }
        // R3: Overlay
        else if (c.FaultKind == ModuleKind.Overlay)
        {
            var faultPath = classifier.ResolvePath(d!.FaultingModule!);
            var product = ModuleClassifier.OverlayProduct(faultPath) ?? c.FaultLabel;
            c.Findings.Add(new Finding(Severity.Critical,
                L.F("Absturz in einem Overlay ({0})", product),
                L.F("Der Fehler trat in {0} auf – einem Programm, das sich ins Spiel einklinkt. {1}", faultPath, exceptionText),
                L.F("{0} für Cyberpunk deaktivieren und erneut testen.", product))
            {
                Category = "Ursache",
                PlainTitle = L.F("Absturz durch ein Zusatzprogramm ({0})", product),
                Plain = L.F("„{0}“ klinkt sich ins Spiel ein und hat den Absturz ausgelöst. Schalte es für Cyberpunk testweise aus.", product),
            });
        }
        // R4: Engine/Windows – Indizien vom Stack
        else if (d is not null && d.Error is null)
        {
            // ArchiveXL steckt im Aufrufpfad fast jeder Mod, die Inhalte nachlädt – das ist kein Indiz und wird nicht aufgeführt
            var mods = c.SuspectModules.Where(s => s.Kind is ModuleKind.Mod or ModuleKind.ModFramework or ModuleKind.Overlay)
                .Where(s => !IsArchiveXl(s.Name)).ToList();
            var where = c.FaultKind switch
            {
                ModuleKind.GameExecutable => L.T("in der Spiel-Engine (Cyberpunk2077.exe)"),
                ModuleKind.GameRuntime => L.F("in einer Spiel-Bibliothek ({0})", c.FaultLabel),
                ModuleKind.System => L.F("in einer Windows-Komponente ({0})", c.FaultLabel),
                _ => L.F("in {0}", c.FaultLabel),
            };
            if (mods.Count > 0)
            {
                c.Findings.Add(new Finding(Severity.Warning,
                    L.F("Absturz {0} – Mods im Aufrufpfad gefunden", where),
                    L.F("{0} Auf dem Stack des abstürzenden Threads liegen Rücksprungadressen in diesen Mods/Hooks. Das ist ein starkes Indiz, dass sie beteiligt waren (aber kein Beweis):", exceptionText),
                    L.T("Die oben genannten Mods zuerst aktualisieren bzw. testweise deaktivieren."),
                    mods.Select(m => L.F("{0} ({1}, {2}× auf dem Stack)", m.Name, ModuleClassifier.KindLabel(m.Kind), m.Hits)
                        + (m.Installed ? "" : " – " + L.T("nicht mehr installiert"))).ToList())
                {
                    Category = "Ursache",
                    PlainTitle = L.T("Beim Absturz waren Mods beteiligt"),
                    Plain = L.F("Diese Mods waren beim Absturz aktiv beteiligt: {0}. Das ist ein Hinweis, kein Beweis.",
                        string.Join(", ", mods.Take(5).Select(m => m.Name)) + (mods.Count > 5 ? " …" : "")),
                });
            }
            else
            {
                c.Findings.Add(new Finding(Severity.Info,
                    L.F("Absturz {0}", where),
                    c.Symbols?.Site is not null
                        ? L.F("{0} Auf dem Stack wurden keine Mods gefunden. Die Funktionsnamen (Community-Daten) stehen bei den technischen Daten; die Signatur {1} kann mit anderen Abstürzen verglichen werden.", exceptionText, c.Signature)
                        : L.F("{0} Auf dem Stack wurden keine Mods gefunden. Ohne Debug-Symbole von CD Projekt lässt sich die genaue Funktion nicht benennen – die Signatur {1} kann aber mit anderen Abstürzen verglichen werden.", exceptionText, c.Signature),
                    null)
                {
                    Category = "Ursache",
                    PlainTitle = L.T("Dieser Absturz liegt im Spiel selbst"),
                    Plain = L.T("Es wurde keine Mod als Auslöser gefunden. Solche Abstürze lassen sich oft nicht direkt beheben – Mods können sie aber manchmal begünstigen."),
                });
            }
        }
        else if (d?.Error is { } err)
        {
            c.Findings.Add(new Finding(Severity.Info, L.T("Dump nicht auswertbar"), err) { Category = "Ursache" });
        }
        afterFault:

        // R5: Speicher
        if (r.OutOfMemory)
            c.Findings.Add(new Finding(Severity.Critical, L.T("Die Engine meldet „Out of Memory“"),
                L.T("Dem Spiel ist der Arbeitsspeicher ausgegangen."),
                L.T("Auslagerungsdatei auf „automatisch“ stellen und genug Platz auf C: lassen (≥ 20 GB). Speicherhungrige Mods (Texturpakete, Crowd-Mods) reduzieren. Hintergrundprogramme schließen."))
            { Category = "Speicher" });

        if (r.VramUsedMb is int used && r.VramTotalMb is int total && total > 0 && used >= total * 0.92)
            c.Findings.Add(new Finding(Severity.Warning, L.T("Grafikspeicher (VRAM) fast voll"),
                L.F("Zum Absturzzeitpunkt waren {0} von {1} MB VRAM belegt ({2:F0} %).", used, total, 100.0 * used / total),
                L.T("Texturqualität senken, Raytracing/Pathtracing reduzieren oder hochauflösende Texturmods entfernen."))
            { Category = "Speicher" });

        // R6: Zeitpunkt
        if (r.UptimeSeconds is int up && up < 90)
            c.Findings.Add(new Finding(Severity.Warning, L.F("Absturz direkt nach dem Start ({0} s)", up),
                L.T("Abstürze in den ersten Sekunden deuten fast immer auf ein Problem beim Laden von Mods/Plugins hin (falsche Version, fehlende Abhängigkeit)."),
                L.T("Mod-Logs (RED4ext, redscript, CET) prüfen – siehe Abschnitt „Mod-Umgebung“."))
            { Category = "Zeitpunkt" });
        else if (r.LoadingStage is { } stage && stage != "Finished")
            c.Findings.Add(new Finding(Severity.Hint, L.F("Absturz während eines Ladevorgangs ({0})", stage),
                L.T("Das Spiel war noch beim Laden. Typisch für defekte Spielstände, Archive-Mods mit Fehlern oder Ressourcen-Konflikte."),
                L.T("Einen älteren Spielstand testen; ArchiveXL-Log auf Fehler prüfen."))
            { Category = "Zeitpunkt" });

        // R7: GPU-Timeout um den Absturz herum
        if (c.NearbyEvents.Any(e => e.Kind == SystemEventKind.GpuTimeout))
            c.Findings.Add(new Finding(Severity.Critical, L.T("Grafiktreiber-Timeout (TDR) zum Absturzzeitpunkt"),
                L.T("Windows hat den Grafiktreiber neu gestartet, weil die GPU nicht mehr reagierte."),
                L.T("GPU-Übertaktung/Undervolt zurücksetzen, Temperaturen prüfen, Treiber neu installieren, Netzteil als Ursache in Betracht ziehen."))
            { Category = "Hardware" });
        if (c.NearbyEvents.Any(e => e.Kind == SystemEventKind.Whea))
            c.Findings.Add(new Finding(Severity.Critical, L.T("Hardwarefehler (WHEA) zum Absturzzeitpunkt"),
                L.T("Windows hat einen Hardwarefehler protokolliert (CPU, RAM oder PCIe)."),
                L.T("Übertaktung von CPU/RAM (inkl. XMP/EXPO, Curve Optimizer) testweise deaktivieren und Stabilitätstest durchführen."))
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
            var op = d.ExceptionParameters[0] switch { 0 => L.T("lesen"), 1 => L.T("schreiben"), 8 => L.T("ausführen"), _ => L.T("zugreifen") };
            var addr = d.ExceptionParameters[1];
            var text = addr < 0x10000
                ? L.F("Speicherzugriffsfehler: Versuch, an Adresse 0x{0:X} zu {1} – ein Null-Zeiger (ein Objekt, das es nicht gibt).", addr, op)
                : L.F("Speicherzugriffsfehler: Versuch, an Adresse 0x{0:X} zu {1} – auf ungültigen/freigegebenen Speicher.", addr, op);
            return AddressHint(d) is { } extra ? text + " " + extra : text;
        }
        return $"{name} (0x{d.ExceptionCode:X8}).";
    }

    /// <summary>
    /// Kurze Einordnung der Zugriffsadresse und des Zeiger-Registers. Beschreibt nur, wie der Zeigerwert aussah – ob er vorher überschrieben
    /// oder freigegeben wurde, lässt sich aus einem einzelnen Dump nicht beweisen.
    /// </summary>
    public static string? AddressHint(DumpAnalysis d)
    {
        var reg = d.BaseRegister is { } r ? $"{r} = 0x{d.BaseRegisterValue:X}" : null;
        return d.AddressClass switch
        {
            FaultAddressClass.NullPage when d.BaseRegister is not null && d.BaseRegisterValue == 0 =>
                L.F("Das Zeiger-Register ({0}) war 0: ein nicht gesetzter Zeiger.", reg),
            FaultAddressClass.NullPage when d.BaseRegister is not null && d.BaseRegisterValue < 0x10000 =>
                L.F("Das Zeiger-Register ({0}) enthielt eine kleine Zahl statt eines Zeigers – das Objekt wurde vermutlich überschrieben oder falsch gelesen (kein Beweis).", reg),
            FaultAddressClass.NotAPointer =>
                L.T("Die Adresse ist kein gültiger Zeigerwert – der Zeiger wurde vermutlich überschrieben."),
            FaultAddressClass.HeapLikeUnmapped =>
                reg is null
                    ? L.T("Die Adresse sieht wie ein normaler Heap-Zeiger dieses Prozesses aus, der Speicher war aber nicht mehr vorhanden – typisch für einen Zeiger auf ein bereits freigegebenes Objekt (kein Beweis).")
                    : L.F("Die Adresse sieht wie ein normaler Heap-Zeiger dieses Prozesses aus ({0}), der Speicher war aber nicht mehr vorhanden – typisch für einen Zeiger auf ein bereits freigegebenes Objekt (kein Beweis).", reg),
            FaultAddressClass.InModule =>
                L.T("Die Adresse liegt im Abbild eines geladenen Moduls (z. B. Schreiben auf Code oder schreibgeschützte Daten)."),
            _ => null,
        };
    }

    // Übersetzt über L.T(code switch …) – für die Vollständigkeitsprüfung hier aufgeführt:
    // i18n: "Speicherzugriffsfehler (Access Violation)"
    // i18n: "Stapelüberlauf (Stack Overflow)"
    // i18n: "Sicherheitsabbruch (Fail Fast / Stack Buffer Overrun)"
    // i18n: "Ungültige CPU-Anweisung (Illegal Instruction)"
    // i18n: "Privilegierte Anweisung"
    // i18n: "Division durch null"
    // i18n: "Heap-Beschädigung"
    // i18n: "Unbehandelte C++-Ausnahme"
    // i18n: "Breakpoint / Assertion"
    // i18n: "Kein Speicher mehr"
    // i18n: "DirectX: Gerät entfernt (DXGI_ERROR_DEVICE_REMOVED)"
    // i18n: "DirectX: Gerät hängt (DXGI_ERROR_DEVICE_HUNG)"
    // i18n: "Ausnahme"
    private static string ExceptionName(uint code) => L.T(code switch
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
    });

    private static Finding? ExceptionHint(DumpAnalysis d) => d.ExceptionCode switch
    {
        0xC000001D => new Finding(Severity.Warning, L.T("Ungültige CPU-Anweisung"),
            L.T("Dieser Fehler entsteht häufig durch instabile CPU-Übertaktung/Undervolting (z. B. Curve Optimizer) oder sehr selten durch defekte Dateien."),
            L.T("CPU-Übertaktung/Undervolt testweise zurücksetzen, Spieldateien prüfen lassen.")) { Category = "Hardware" },
        0xC00000FD => new Finding(Severity.Warning, L.T("Stapelüberlauf"),
            L.T("Eine Endlos-Rekursion – sehr oft in Skript-Mods (redscript/CET), die sich gegenseitig aufrufen."),
            L.T("Kürzlich installierte Skript-Mods prüfen; redscript-Log auf Konflikte („method replacement“) prüfen.")) { Category = "Ursache" },
        0xC0000374 => new Finding(Severity.Warning, L.T("Heap-Beschädigung"),
            L.T("Speicher wurde von fehlerhaftem Code überschrieben. Der eigentliche Verursacher ist oft nicht das abstürzende Modul."),
            L.T("Native Mods (RED4ext-Plugins) einzeln deaktivieren, um den Verursacher einzugrenzen.")) { Category = "Ursache" },
        0x887A0005 or 0x887A0006 => new Finding(Severity.Critical, L.T("DirectX-Gerät verloren"),
            L.T("Die GPU wurde vom Treiber zurückgesetzt."),
            L.T("GPU-Übertaktung zurücksetzen, Treiber neu installieren, Temperaturen und Netzteil prüfen.")) { Category = "Hardware" },
        _ => null,
    };

    // ------------------------------------------------------------------
    // Regeln über alle Abstürze hinweg
    // ------------------------------------------------------------------

    /// <summary>Gruppiert Abstürze nach Teilsystem. Je mehr Abstürze einem Teilsystem zugeordnet sind, desto aussagekräftiger – einzelne Absturzstellen sind meist Einzelfälle.</summary>
    private static void ApplySubsystemPattern(AnalysisResult result)
    {
        var groups = result.Crashes.Where(c => c.Symbols?.Subsystem is not null)
            .GroupBy(c => c.Symbols!.Subsystem!.Label).OrderByDescending(g => g.Count()).ToList();
        int classified = groups.Sum(g => g.Count());
        if (classified < 3) return;
        result.PatternFindings.Add(new Finding(Severity.Info, L.T("Abstürze nach Teilsystem"),
            L.F("Bei {0} von {1} Abstürzen ließ sich aus den Funktionsnamen im Aufrufpfad ein Teilsystem des Spiels ableiten. Die Namen stammen aus Community-Daten und können falsch sein – es ist ein Hinweis, wo im Spiel es passiert, keine Ursache.", classified, result.Crashes.Count),
            null,
            groups.Select(g => $"{g.Key}: {g.Count()}×").ToList()) { ExpertOnly = true });
    }

    private static void ApplyPatternRules(AnalysisResult result)
    {
        var crashes = result.Crashes;
        if (crashes.Count == 0) return;

        var last7 = crashes.Count(c => c.Report.Time >= DateTime.Now.AddDays(-7));
        if (last7 >= 5)
            result.PatternFindings.Add(new Finding(Severity.Warning, L.F("{0} Abstürze in den letzten 7 Tagen", last7),
                L.T("Das Spiel stürzt regelmäßig ab – es handelt sich nicht um einen Einzelfall.")));

        // Gleiche Signatur mehrfach
        foreach (var g in crashes.Where(c => c.Signature != "?").GroupBy(c => c.Signature).Where(g => g.Count() >= 2).OrderByDescending(g => g.Count()).Take(5))
        {
            var first = g.First();
            result.PatternFindings.Add(new Finding(
                g.Count() >= 3 ? Severity.Warning : Severity.Hint,
                L.F("Gleicher Absturz {0}× wiederholt: {1}", g.Count(), g.Key),
                L.F("Immer an derselben Stelle ({0}: {1}). Das ist ein reproduzierbarer Fehler, kein Zufall – meist eine bestimmte Mod oder ein bestimmter Ort/Spielstand.", ModuleClassifier.KindLabel(first.FaultKind), first.FaultLabel),
                null,
                g.Select(c => L.F("{0:g}", c.Report.Time)).ToList())
            {
                Signature = g.Key,
                PlainTitle = L.F("Derselbe Absturz ist {0}× passiert", g.Count()),
                Plain = L.T("Immer an der gleichen Stelle. Das ist kein Zufall und lässt sich meist beheben – oft ist es eine bestimmte Mod oder ein bestimmter Ort in der Spielwelt."),
            });
        }

        // Absturzstelle fest an eine Mod gekoppelt: dieselbe Signatur, und fast jedes Mal dieselbe Mod auf dem Stack
        foreach (var g in crashes.Where(c => c.Signature != "?" && c.FaultKind is ModuleKind.GameExecutable or ModuleKind.GameRuntime or ModuleKind.System)
                     .GroupBy(c => c.Signature).Where(g => g.Count() >= 3))
        {
            var coupled = g.SelectMany(c => c.SuspectModules.Where(s => s.Kind is ModuleKind.Mod or ModuleKind.Overlay && s.Installed).Select(s => s.Name).Distinct())
                .GroupBy(n => n).Select(x => (Name: x.Key, Count: x.Count()))
                .Where(x => x.Count >= 3 && x.Count >= 0.8 * g.Count())
                .OrderByDescending(x => x.Count).FirstOrDefault();
            if (coupled.Name is null) continue;
            result.PatternFindings.Insert(0, new Finding(Severity.Critical,
                L.F("Absturzstelle {0} ist an „{1}“ gekoppelt", g.Key, coupled.Name),
                L.F("Diese Absturzstelle trat {0}× auf – und {1}× davon lag „{2}“ im Aufrufpfad. Eine so feste Kopplung ist kein Zufall: Die Mod ruft diesen Teil der Engine auf und übergibt dabei offenbar ungültige Daten.", g.Count(), coupled.Count, coupled.Name),
                L.F("„{0}“ testweise deaktivieren. Bleiben diese Abstürze danach aus, ist die Ursache bestätigt – dann nach einem Update suchen oder die Mod weglassen.", coupled.Name),
                g.Select(c => L.F("{0:g}", c.Report.Time)).ToList())
            {
                Category = "Ursache",
                Signature = g.Key,
                PlainTitle = L.F("„{0}“ steckt bei diesen Abstürzen fast immer mit drin", coupled.Name),
                Plain = L.F("Derselbe Absturz ist {0}× passiert, und {1}× davon war „{2}“ beteiligt. Das ist sehr wahrscheinlich kein Zufall.", g.Count(), coupled.Count, coupled.Name),
            });
        }

        // Abstürze häufen sich an einem Ort
        ApplyLocationRules(result);

        // Nach Kategorie
        var byKind = crashes.Where(c => c.Dump?.Error is null && c.Dump is not null).GroupBy(c => c.FaultKind)
            .Select(g => $"{ModuleClassifier.KindLabel(g.Key)}: {g.Count()}×").ToList();
        if (byKind.Count > 0)
            result.PatternFindings.Add(new Finding(Severity.Info, L.T("Wo die Abstürze passieren"),
                L.T("Verteilung nach abstürzendem Modul:"), null, byKind) { ExpertOnly = true });

        // Welche Mods tauchen über viele Abstürze hinweg auf dem Stack auf?
        var modFreq = crashes
            .SelectMany(c => c.SuspectModules.Where(s => s.Kind is ModuleKind.Mod or ModuleKind.Overlay && s.Installed).Select(s => s.Name).Distinct())
            .GroupBy(n => n).Select(g => (Name: g.Key, Count: g.Count()))
            .Where(x => x.Count >= 2).OrderByDescending(x => x.Count).Take(8).ToList();
        if (modFreq.Count > 0)
            result.PatternFindings.Add(new Finding(Severity.Warning, L.T("Mods, die bei mehreren Abstürzen im Aufrufpfad lagen"),
                L.T("Diese Mods tauchten bei mehreren Abstürzen auf dem Stack auf. Je öfter, desto verdächtiger:"),
                L.T("Mit dem Spitzenreiter anfangen: aktualisieren oder testweise deaktivieren."),
                modFreq.Select(m => L.F("{0}: bei {1} von {2} Abstürzen", m.Name, m.Count, crashes.Count)).ToList()));

        // Versionen
        var versions = crashes.Select(c => c.Report.InternalVersion).Where(v => v is not null).Distinct().ToList();
        if (versions.Count > 1)
            result.PatternFindings.Add(new Finding(Severity.Info, L.T("Spielversion hat sich im Zeitraum geändert"),
                L.T("Die Abstürze stammen aus verschiedenen Spielversionen – nach einem Update brauchen viele Mods selbst ein Update."),
                null, versions!));
    }

    // Bekannte Orte (Weltkoordinaten x/y, Radius in Metern) – bewusst nur sicher zugeordnete Einträge.
    // Der Name wird beim Anzeigen übersetzt.
    // i18n: "Megagebäude H10 (Vs Wohnung, Watson)"
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
            var where = place is not null ? L.T(place) : L.F("bei Koordinaten [{0:F0}, {1:F0}]", cx, cy);

            int sameSpot = cl.Count(c => cl.Count(o => Dist(o.Report.Position!.Value, c.Report.Position!.Value) < 10) >= 2);
            int shortUptime = cl.Count(c => c.Report.UptimeSeconds is < 300);

            var detail = L.F("{0} von {1} Abstürzen mit bekannter Position passierten {2} (Umkreis {3:F0} m). ", cl.Count, located.Count, where, ClusterRadius);
            string rec, plain;
            if (sameSpot >= 3 && shortUptime >= cl.Count / 2)
            {
                plain = L.T("Mehrere Abstürze passierten an exakt derselben Stelle, kurz nach dem Laden. Das ist typisch für einen Spielstand, der genau dort steht.");
                detail += L.T("Mehrere davon an exakt derselben Stelle und kurz nach dem Laden – typisch für einen Spielstand, der genau dort steht und beim Laden immer wieder abstürzt.");
                rec = L.T("Einen älteren Spielstand an einem anderen Ort laden. Hilft das, liegt es an Objekten/Mods, die an diesem Ort geladen werden.");
            }
            else
            {
                plain = L.T("Immer wieder stürzt das Spiel an derselben Stelle der Spielwelt ab. Das deutet meist auf Mods hin, die dort Inhalte hinzufügen (Garagen, Wohnungen, Fahrzeuge, NPCs).");
                detail += L.T("Ein Ort, an dem es immer wieder kracht, deutet auf Mods hin, die dort Inhalte hinzufügen (Garagen, Wohnungs-Deko, Fahrzeuge, NPCs) – oder auf eine dort geladene Szene, die mit Mods kollidiert.");
                rec = L.T("Prüfen, welche Mods diesen Ort verändern (z. B. Garagen-, Wohnungs- oder Fahrzeug-Mods) und diese testweise deaktivieren. Hinweise auf defekte Mod-Inhalte stehen unter „Mods & System“.");
            }
            result.PatternFindings.Add(new Finding(cl.Count >= 4 ? Severity.Warning : Severity.Hint,
                L.F("Abstürze häufen sich an einem Ort: {0} ({1}×)", where, cl.Count),
                detail, rec,
                cl.Select(c => L.F("{0:dd.MM. HH:mm}  Höhe {1,4:F0} m  {2}  nach {3}", c.Report.Time, c.Report.Position!.Value.Z, c.Signature,
                    TimeSpan.FromSeconds(c.Report.UptimeSeconds ?? 0).ToString(@"h\:mm\:ss"))).ToList())
            {
                Category = "Ort",
                PlainTitle = L.F("{0}× Absturz an derselben Stelle in der Spielwelt", cl.Count),
                Plain = plain,
            });
        }
    }

    // ------------------------------------------------------------------
    // Regeln zur Umgebung (Mods, System)
    // ------------------------------------------------------------------
    private static void ApplyEnvironmentRules(AnalysisResult result, CyberpunkInstall? install)
    {
        if (install is null)
        {
            result.EnvironmentFindings.Add(new Finding(Severity.Hint, L.T("Cyberpunk-Installation nicht gefunden"),
                L.T("Ohne Spielordner können Mod-Logs nicht geprüft werden."), L.T("Spielordner unter „Mods & System“ → „Datenquellen“ festlegen.")));
        }

        if (result.ModLogs is { } logs)
        {
            // Versionen der Frameworks gegen das Spiel
            var gameFile = result.GameVersion;
            if (logs.CetGameVersion is { } cetGame && gameFile is not null && !VersionsMatch(cetGame, gameFile))
                result.EnvironmentFindings.Add(new Finding(Severity.Critical, L.T("Cyber Engine Tweaks passt nicht zur Spielversion"),
                    L.F("CET meldet Spielversion {0}, installiert ist {1}.", cetGame, gameFile),
                    L.T("CET auf die neueste Version aktualisieren.")) { Category = "Mods" });

            var critical = logs.Issues.Where(i => i.Severity == Severity.Critical).ToList();
            if (critical.Count > 0)
                result.EnvironmentFindings.Add(new Finding(Severity.Critical, L.F("{0} schwere Fehler in Mod-Logs", critical.Count),
                    L.T("Diese Fehler verhindern, dass Mods korrekt geladen werden:"), L.T("Betroffene Mods aktualisieren oder entfernen."),
                    critical.Take(10).Select(Describe).ToList()) { Category = "Mods" });

            var conflicts = logs.Issues.Where(i => i.Source == "redscript" && i.Message.Contains("method replacement", StringComparison.OrdinalIgnoreCase)).ToList();
            if (conflicts.Count > 0)
                result.EnvironmentFindings.Add(new Finding(Severity.Warning, L.F("{0} redscript-Konflikte", conflicts.Sum(c => c.Count)),
                    L.T("Mehrere Mods ersetzen dieselbe Spielfunktion – nur eine davon wirkt. Das führt zu Fehlverhalten und kann Abstürze begünstigen."),
                    L.T("Bei Konflikten prüfen, ob die Mods als kompatibel gelten; ggf. eine davon entfernen."),
                    conflicts.GroupBy(c => c.Mod ?? "?").Select(g => L.F("{0}: {1} Konflikt(e)", g.Key, g.Sum(x => x.Count))).ToList()) { Category = "Mods" });

            // Mod-Inhalte, die das Spiel nicht findet (z. B. Garagen ohne geladene Welt-Sektoren)
            foreach (var u in logs.UnresolvedContent.Where(u => u.Items.Count >= 2).OrderByDescending(u => u.Items.Count))
            {
                var modHint = FindModFiles(install, u.Owner);
                result.EnvironmentFindings.Add(new Finding(Severity.Warning,
                    L.F("Mod-Inhalte werden nicht gefunden: „{0}“ ({1} Einträge)", u.Owner, u.Items.Count),
                    L.F("{0} meldet, dass {1} Verweise dieser Mod (Orte, Kartenmarkierungen, Journal-Einträge) ins Leere zeigen. Die Mod ist also nur teilweise geladen – meist wegen einer veralteten Version, einer fehlenden Abhängigkeit oder einer unvollständigen Installation. An den Orten, die diese Mod verändert, steigt das Absturzrisiko.", u.Source, u.Items.Count) +
                    (modHint.Count > 0 ? " " + L.F("Zugehörige Dateien: {0}.", string.Join(", ", modHint.Take(4)) + (modHint.Count > 4 ? " …" : "")) : ""),
                    L.T("Mod neu installieren bzw. aktualisieren und Abhängigkeiten auf der Mod-Seite prüfen. Bis dahin testweise deaktivieren – besonders, wenn Abstürze an diesen Orten auftreten."),
                    u.Items.Take(12).ToList())
                { Category = "Mods" });
            }

            var warnings = logs.Issues.Where(i => i.Severity == Severity.Warning).OrderByDescending(i => i.Count).ToList();
            if (warnings.Count > 0)
                result.EnvironmentFindings.Add(new Finding(Severity.Hint, L.F("{0} verschiedene Fehlermeldungen in Mod-Logs", warnings.Count),
                    L.T("Hintergrundfehler, die nicht zwingend zum Absturz führen (häufigste zuerst):"), null,
                    warnings.Take(12).Select(Describe).ToList()) { Category = "Mods" });

            int total = logs.ArchiveModCount + logs.Red4ExtPluginCount + logs.RedscriptModCount + logs.CetModCount;
            result.EnvironmentFindings.Add(new Finding(total > 300 ? Severity.Hint : Severity.Info, L.T("Mod-Umfang"),
                L.F("{0} Archive-Mods, {1} RED4ext-Plugins, {2} redscript-Mods, {3} CET-Mods.", logs.ArchiveModCount, logs.Red4ExtPluginCount, logs.RedscriptModCount, logs.CetModCount),
                total > 300 ? L.T("Bei so vielen Mods steigt die Wahrscheinlichkeit für Konflikte und Speicherprobleme deutlich. Zum Eingrenzen Mods in Gruppen deaktivieren (Halbierungsmethode).") : null)
            { Category = "Mods" });
        }

        // Systeminformationen aus dem neuesten Crashreport
        var sys = result.Crashes.Select(c => c.Report.SysInfo).FirstOrDefault(s => s is not null);
        if (sys is { } s) ApplySystemRules(result, s, install);

        // Windows-Ereignisse allgemein
        var tdr = result.SystemEvents.Count(e => e.Kind == SystemEventKind.GpuTimeout);
        if (tdr > 0)
            result.EnvironmentFindings.Add(new Finding(Severity.Warning, L.F("{0} Grafiktreiber-Probleme in der Ereignisanzeige", tdr),
                L.T("Windows hat Grafiktreiber-Timeouts oder -Fehler protokolliert."), L.T("GPU-Übertaktung, Temperaturen und Treiberversion prüfen."),
                result.SystemEvents.Where(e => e.Kind == SystemEventKind.GpuTimeout).Take(5).Select(e => L.F("{0:dd.MM. HH:mm} – {1}", e.Time, e.Summary)).ToList())
            { Category = "Hardware" });
        var whea = result.SystemEvents.Count(e => e.Kind == SystemEventKind.Whea);
        if (whea > 0)
            result.EnvironmentFindings.Add(new Finding(Severity.Warning, L.F("{0} Hardwarefehler (WHEA) in der Ereignisanzeige", whea),
                L.T("Hinweis auf instabile Hardware (oft Übertaktung von CPU/RAM)."), L.T("XMP/EXPO und CPU-Übertaktung testweise deaktivieren."),
                result.SystemEvents.Where(e => e.Kind == SystemEventKind.Whea).Take(5).Select(e => L.F("{0:dd.MM. HH:mm} – {1}", e.Time, e.Summary)).ToList())
            { Category = "Hardware" });
        var kp = result.SystemEvents.Count(e => e.Kind == SystemEventKind.UnexpectedShutdown);
        if (kp > 0)
            result.EnvironmentFindings.Add(new Finding(Severity.Warning, L.F("{0}× unerwarteter Neustart des PCs", kp),
                L.T("Der PC ging ohne sauberes Herunterfahren aus. Beim Spielen oft Netzteil, Temperatur oder Übertaktung."), null,
                result.SystemEvents.Where(e => e.Kind == SystemEventKind.UnexpectedShutdown).Take(5).Select(e => L.F("{0:g}", e.Time)).ToList())
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
                    result.EnvironmentFindings.Add(new Finding(Severity.Warning, L.F("Nur {0:F0} GB Arbeitsspeicher", gb),
                        L.T("Cyberpunk 2.x empfiehlt mindestens 16 GB, mit vielen Mods eher 32 GB."), null) { Category = "System" });
                else if (gb < 31 && result.ModLogs is { ArchiveModCount: > 200 })
                    result.EnvironmentFindings.Add(new Finding(Severity.Hint, L.F("{0:F0} GB RAM bei sehr vielen Mods", gb),
                        L.T("Mit mehreren hundert Mods kann es mit 16 GB eng werden (Nachladeruckler, Out-of-Memory)."),
                        L.T("Auslagerungsdatei auf „automatisch“ lassen, Browser/Discord beim Spielen schließen.")) { Category = "System" });
            }

            if (sys.TryGetProperty("MemoryBanks", out var banks) && banks.ValueKind == JsonValueKind.Array)
            {
                int count = banks.GetArrayLength();
                if (count == 1)
                    result.EnvironmentFindings.Add(new Finding(Severity.Warning, L.T("RAM läuft im Single-Channel"),
                        L.T("Es ist nur ein Speichermodul verbaut. Das kostet in CPU-lastigen Spielen wie Cyberpunk spürbar FPS."),
                        L.T("Zweites, baugleiches Modul ergänzen.")) { Category = "Performance" });
                var speeds = banks.EnumerateArray().Select(b => b.TryGetProperty("Speed", out var sp) && sp.ValueKind == JsonValueKind.Number ? sp.GetInt32() : 0).Where(x => x > 0).ToList();
                if (speeds.Count > 0 && speeds.Min() <= 2666)
                    result.EnvironmentFindings.Add(new Finding(Severity.Hint, L.F("RAM läuft mit {0} MT/s", speeds.Min()),
                        L.T("Aktuelle CPUs (besonders AMD Ryzen) profitieren deutlich von schnellerem RAM (3200–3600 MT/s bei DDR4)."),
                        L.T("Im BIOS prüfen, ob das XMP/DOCP-Profil aktiv ist. Ist der RAM selbst nur für diesen Takt spezifiziert, bringt schnellerer RAM spürbar mehr FPS.")) { Category = "Performance" });
            }

            // Freier Platz auf C: (Shader-Cache immer; Auslagerungsdatei nur, wenn sie dort liegt und wachsen kann)
            if (sys.TryGetProperty("Drives", out var drives))
            {
                foreach (var dr in drives.EnumerateArray())
                {
                    var name = dr.GetProperty("Name").GetString();
                    long free = dr.GetProperty("AvailableFreeSpace").GetInt64();
                    long size = dr.GetProperty("TotalSize").GetInt64();
                    if (name?.StartsWith("C", StringComparison.OrdinalIgnoreCase) == true && free < 25L * 1024 * 1024 * 1024)
                        result.EnvironmentFindings.Add(LowSpaceOnC(free, size, PageFileInfo.Read()));
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
                            result.EnvironmentFindings.Add(new Finding(Severity.Critical, L.T("Spiel liegt auf einer Festplatte (HDD)"),
                                L.T("Cyberpunk 2.x setzt eine SSD voraus. Auf einer HDD gibt es Nachladeruckler, fehlende Texturen und Abstürze beim Streaming."),
                                L.T("Spiel auf eine SSD verschieben.")) { Category = "Performance" });
                        else if (disk.ValueKind == JsonValueKind.Object)
                            result.EnvironmentFindings.Add(new Finding(Severity.Info, L.T("Spiel liegt auf einer SSD"),
                                L.F("{0} – gut.", disk.GetProperty("FriendlyName").GetString()), null) { Category = "System" });
                    }
                }
            }
        }
        catch
        {
            // sysinfo.json in unerwartetem Format – Systemregeln überspringen
        }
    }

    /// <summary>
    /// Hinweis „wenig Platz auf C:“. Was dort knapp wird, hängt davon ab, wo die Auslagerungsdatei liegt:
    /// Nur wenn sie auf C: liegt UND wachsen darf, droht ein Absturz durch fehlenden virtuellen Speicher.
    /// Der Shader-Cache des Grafiktreibers liegt dagegen immer auf C:.
    /// </summary>
    public static Finding LowSpaceOnC(long freeBytes, long sizeBytes, PageFileInfo pageFile)
    {
        const double Gb = 1024d * 1024 * 1024;
        string title = L.F("Wenig Platz auf C: ({0:F0} GB frei von {1:F0} GB)", freeBytes / Gb, sizeBytes / Gb);

        if (pageFile.CanGrowOn("C:"))
            return new Finding(freeBytes < 10 * Gb ? Severity.Warning : Severity.Hint, title,
                L.T("Die Auslagerungsdatei und der Shader-Cache liegen auf C:. Ist der Platz knapp, kann Windows den virtuellen Speicher nicht vergrößern – das führt zu Abstürzen ohne klare Fehlermeldung."),
                L.T("Mindestens 25–30 GB auf C: freihalten.")) { Category = "System" };

        var elsewhere = pageFile.Drives().Where(d => d != "C:").ToList();
        string pageNote = pageFile.Disabled ? L.T("Die Auslagerungsdatei ist deaktiviert.")
            : pageFile.Drives().Contains("C:") ? L.T("Die Auslagerungsdatei auf C: hat eine feste Größe und ist davon nicht betroffen.")
            : L.F("Die Auslagerungsdatei liegt auf {0} und ist davon nicht betroffen.", string.Join(", ", elsewhere));
        return new Finding(freeBytes < 5 * Gb ? Severity.Warning : Severity.Hint, title,
            L.T("Der Shader-Cache des Grafiktreibers und temporäre Dateien liegen auf C:. Ist der Platz knapp, können neue Shader nicht gespeichert werden – das führt zu Nachladerucklern, und Windows-Updates oder Absturzberichte können scheitern.") + " " + pageNote,
            L.T("Mindestens 15–20 GB auf C: freihalten.")) { Category = "System" };
    }
}
