namespace GameDoctor.Core;

public enum Severity
{
    Info = 0,
    Hint = 1,
    Warning = 2,
    Critical = 3,
}

/// <summary>Eine einzelne Erkenntnis der Analyse – das, was der Spieler am Ende liest.</summary>
public sealed record Finding(
    Severity Severity,
    string Title,
    string Detail,
    string? Recommendation = null,
    IReadOnlyList<string>? Evidence = null)
{
    public string Category { get; init; } = "Allgemein";

    /// <summary>Verständliche Überschrift ohne Fachbegriffe, wie ein Spieler sie sagen würde (null = <see cref="Title"/> verwenden).</summary>
    public string? PlainTitle { get; init; }
    /// <summary>Verständliche Erklärung in ein bis zwei Sätzen, ohne Adressen und Fachbegriffe (null = <see cref="Detail"/> verwenden).</summary>
    public string? Plain { get; init; }
    /// <summary>Nur für die Experten-Ansicht: Technik oder Statistik, aus der ein Spieler nichts machen kann.</summary>
    public bool ExpertOnly { get; init; }
    /// <summary>Absturzstelle, auf die sich der Befund bezieht (damit „Lösung ausprobieren“ den richtigen Absturz öffnen kann).</summary>
    public string? Signature { get; init; }
}

/// <summary>Wohin ein geladenes Modul (DLL) gehört.</summary>
public enum ModuleKind
{
    Unknown,
    GameExecutable,
    GameRuntime,
    ModFramework,
    Mod,
    GpuDriver,
    Overlay,
    System,
    ThirdParty,
}

public sealed record DumpModule(string Path, ulong Base, uint Size, string? FileVersion)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public bool Contains(ulong address) => address >= Base && address < Base + Size;
}

public sealed record StackHit(DumpModule Module, ulong Offset);

/// <summary>Wie die Adresse eines Speicherzugriffsfehlers einzuordnen ist (aus der Adresse und den Registern abgeleitet).</summary>
public enum FaultAddressClass
{
    /// <summary>Nicht bestimmbar (kein Speicherzugriffsfehler oder keine Daten).</summary>
    Unknown,
    /// <summary>Unterhalb von 0x10000: Null-Zeiger, evtl. plus Versatz.</summary>
    NullPage,
    /// <summary>Kein gültiger Zeigerwert (außerhalb des 48-Bit-Adressraums).</summary>
    NotAPointer,
    /// <summary>Ähnelt den Heap-Adressen dieses Prozesses, der Speicher war aber nicht mehr vorhanden.</summary>
    HeapLikeUnmapped,
    /// <summary>Gültig aussehende Adresse ohne Bezug zu bekannten Adressen des Prozesses.</summary>
    OtherUnmapped,
    /// <summary>Adresse liegt im Abbild eines geladenen Moduls (z. B. Schreiben auf Code).</summary>
    InModule,
}

/// <summary>Klassen-Kennzeichen: Adresse (Offset in der Spiel-EXE), auf die das erste Feld mehrerer Objekte zeigt (meist die Funktionstabelle der Klasse).</summary>
public sealed record ObjectClassHit(ulong ClassOffset, int Count);

/// <summary>Ein Register des abstürzenden Threads, das auf ein Objekt mit Klassen-Kennzeichen zeigt.</summary>
public sealed record RegisterObject(string Register, ulong ClassOffset);

public sealed class DumpAnalysis
{
    public uint ExceptionCode { get; init; }
    public ulong ExceptionAddress { get; init; }
    public ulong[] ExceptionParameters { get; init; } = [];
    public uint CrashingThreadId { get; init; }
    public DumpModule? FaultingModule { get; init; }
    public ulong FaultingOffset { get; init; }
    public IReadOnlyList<DumpModule> Modules { get; init; } = [];
    /// <summary>Rücksprungadressen-Kandidaten vom Stack des abstürzenden Threads (heuristisch, oberste zuerst).</summary>
    public IReadOnlyList<StackHit> StackHits { get; init; } = [];
    /// <summary>Register des abstürzenden Threads (Rax … R15, Rip).</summary>
    public IReadOnlyDictionary<string, ulong> Registers { get; init; } = new Dictionary<string, ulong>();
    public FaultAddressClass AddressClass { get; init; }
    /// <summary>Register, das die Zugriffsadresse (abzüglich eines kleinen Versatzes) enthielt – das „Zeiger-Register“.</summary>
    public string? BaseRegister { get; init; }
    public ulong BaseRegisterValue { get; init; }
    /// <summary>Häufigste Klassen-Kennzeichen der Objekte, auf die der Stack zeigt (Kandidaten, heuristisch).</summary>
    public IReadOnlyList<ObjectClassHit> ObjectClasses { get; init; } = [];
    public IReadOnlyList<RegisterObject> RegisterObjects { get; init; } = [];
    public string? Error { get; init; }
}
