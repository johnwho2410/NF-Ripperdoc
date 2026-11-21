namespace GameDoctor.Core.Community;

/// <summary>
/// Anonymer Fingerabdruck eines Absturzes – genau das (und nur das), was später mit der Community geteilt werden darf.
///
/// Datenschutz-Grundsätze (siehe docs/DATENSCHUTZ-COMMUNITY.md):
/// - keine Namen, Pfade, IDs, Uhrzeiten, keine vollständige Mod-Liste
/// - alle Werte so grob, dass viele Spieler denselben Wert haben (Klassen statt Messwerte)
/// - kein Nutzer-Kennzeichen; der DedupKey gilt nur für genau eine Absturzstelle
/// </summary>
public sealed record CrashFingerprint
{
    public int Schema { get; init; } = 1;

    /// <summary>Spiel-Build, z. B. "3.0.5813403" – Absturzstellen sind nur innerhalb eines Builds vergleichbar.</summary>
    public required string GameBuild { get; init; }
    public string? Patch { get; init; }

    /// <summary>Modul+Offset der Absturzstelle, z. B. "Cyberpunk2077.exe+0x1ADE00".</summary>
    public required string Signature { get; init; }
    public required string FaultKind { get; init; }
    /// <summary>Fehlerart, z. B. "AccessViolation/Read/Heap".</summary>
    public required string Exception { get; init; }

    /// <summary>Nicht-Vanilla-Module im Aufrufpfad (meist 0–3). Nur Dateiname und – falls vorhanden – Dateiversion.</summary>
    public IReadOnlyList<FingerprintModule> ModulesOnStack { get; init; } = [];

    /// <summary>Laufzeitklasse: "<2m", "2-10m", "10-60m", ">60m".</summary>
    public string? UptimeClass { get; init; }
    public bool DuringLoading { get; init; }
    public bool VramNearlyFull { get; init; }
    public bool OutOfMemory { get; init; }

    /// <summary>Grobes 500-m-Raster der Spielwelt, z. B. "-3,1" – sagt etwas über den Ort im Spiel, nichts über den Spieler.</summary>
    public string? WorldCell { get; init; }

    public IReadOnlyDictionary<string, string> Frameworks { get; init; } = new Dictionary<string, string>();
    public string? GpuVendor { get; init; }
    /// <summary>Grafikkarten-Serie, z. B. "RTX 40" – nie das genaue Modell.</summary>
    public string? GpuSeries { get; init; }
    /// <summary>RAM-Klasse in GB: "8", "16", "32", "64+".</summary>
    public string? RamClass { get; init; }
    /// <summary>Mod-Anzahl als Bereich: "<50", "50-149", "150-299", "300+".</summary>
    public string? ModCountClass { get; init; }

    /// <summary>
    /// Doppelzähl-Schutz: HMAC(geheimer lokaler Schlüssel, Build + Absturzstelle), gekürzt.
    /// Derselbe Spieler mit derselben Absturzstelle zählt so nur einmal – verschiedene Absturzstellen
    /// desselben Spielers erzeugen völlig verschiedene Werte und lassen sich nicht verknüpfen.
    /// </summary>
    public required string DedupKey { get; init; }
}

public sealed record FingerprintModule(string File, string? Version, string Kind);
