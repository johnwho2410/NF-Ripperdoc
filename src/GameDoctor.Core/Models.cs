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
    public string? Error { get; init; }
}
