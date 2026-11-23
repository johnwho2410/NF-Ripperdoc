using System.Text;

namespace GameDoctor.Core.Dump;

/// <summary>
/// Minimaler, eigenständiger Leser für Windows-Minidumps (.dmp).
/// Liest nur das, was wir für die Absturzanalyse brauchen: Exception, Module, Threads, Speicher.
/// Format-Referenz: minidumpapiset.h (MINIDUMP_HEADER, MINIDUMP_DIRECTORY, ...).
/// </summary>
public static class MinidumpReader
{
    private const uint Signature = 0x504D444D; // "MDMP"
    private const uint ThreadListStream = 3;
    private const uint ModuleListStream = 4;
    private const uint MemoryListStream = 5;
    private const uint ExceptionStream = 6;
    private const uint Memory64ListStream = 9;

    // Offsets im AMD64-CONTEXT
    // Im AMD64-CONTEXT folgen Rax, Rcx, Rdx, Rbx, Rsp, Rbp, Rsi, Rdi und R8 bis R15 im Abstand von 8 Byte.
    private const int ContextRaxOffset = 0x78;
    private const int ContextRspOffset = 0x98;
    private const int ContextRipOffset = 0xF8;
    private static readonly string[] RegisterNames =
        ["Rax", "Rcx", "Rdx", "Rbx", "Rsp", "Rbp", "Rsi", "Rdi", "R8", "R9", "R10", "R11", "R12", "R13", "R14", "R15"];

    private const int MaxStackBytesToScan = 128 * 1024;
    private const int MaxStackHits = 60;

    private readonly record struct MemRange(ulong Start, ulong Size, long FileOffset);

    public static DumpAnalysis Analyze(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            using var br = new BinaryReader(fs);

            if (br.ReadUInt32() != Signature)
                return new DumpAnalysis { Error = Localization.L.T("Keine gültige Minidump-Datei (Signatur fehlt).") };

            br.ReadUInt32(); // Version
            uint streamCount = br.ReadUInt32();
            uint dirRva = br.ReadUInt32();

            var streams = new Dictionary<uint, (uint Size, uint Rva)>();
            for (uint i = 0; i < streamCount; i++)
            {
                fs.Position = dirRva + i * 12;
                uint type = br.ReadUInt32();
                uint size = br.ReadUInt32();
                uint rva = br.ReadUInt32();
                streams.TryAdd(type, (size, rva));
            }

            var modules = streams.TryGetValue(ModuleListStream, out var ml) ? ReadModules(br, ml.Rva) : [];
            var memory = ReadMemoryRanges(br, streams);

            if (!streams.TryGetValue(ExceptionStream, out var ex))
                return new DumpAnalysis { Modules = modules, Error = Localization.L.T("Der Dump enthält keine Exception-Information.") };

            fs.Position = ex.Rva;
            uint threadId = br.ReadUInt32();
            br.ReadUInt32(); // alignment
            uint code = br.ReadUInt32();
            br.ReadUInt32(); // flags
            br.ReadUInt64(); // nested record
            ulong address = br.ReadUInt64();
            uint paramCount = Math.Min(br.ReadUInt32(), 15u);
            br.ReadUInt32(); // alignment
            var parameters = new ulong[15];
            for (int i = 0; i < 15; i++) parameters[i] = br.ReadUInt64();
            uint ctxSize = br.ReadUInt32();
            uint ctxRva = br.ReadUInt32();

            var faulting = modules.FirstOrDefault(m => m.Contains(address));

            // Register und Stackpointer aus dem Exception-Kontext holen und Stack nach Rücksprungadressen absuchen
            var hits = new List<StackHit>();
            var regs = new Dictionary<string, ulong>();
            ulong rsp = 0;
            if (ctxRva != 0 && ctxSize >= ContextRipOffset + 8)
            {
                fs.Position = ctxRva + ContextRaxOffset;
                foreach (var name in RegisterNames) regs[name] = br.ReadUInt64();
                fs.Position = ctxRva + ContextRipOffset;
                regs["Rip"] = br.ReadUInt64();
                rsp = regs["Rsp"];
            }
            else if (ctxRva != 0 && ctxSize > ContextRspOffset + 8)
            {
                fs.Position = ctxRva + ContextRspOffset;
                rsp = br.ReadUInt64();
            }
            if (rsp != 0) hits = ScanStack(br, memory, modules, rsp);

            var exe = modules.FirstOrDefault(m => m.Name.Equals("Cyberpunk2077.exe", StringComparison.OrdinalIgnoreCase)) ?? modules.FirstOrDefault();
            var (objectClasses, registerObjects) = exe is null || regs.Count == 0
                ? ((IReadOnlyList<ObjectClassHit>)[], (IReadOnlyList<RegisterObject>)[])
                : FindObjectClasses(br, memory, modules, exe, regs, rsp);
            var (addressClass, baseRegister, baseValue) = ClassifyFault(code, parameters, (int)paramCount, modules, regs);

            return new DumpAnalysis
            {
                ExceptionCode = code,
                ExceptionAddress = address,
                ExceptionParameters = parameters.Take((int)paramCount).ToArray(),
                CrashingThreadId = threadId,
                FaultingModule = faulting,
                FaultingOffset = faulting is null ? 0 : address - faulting.Base,
                Modules = modules,
                StackHits = hits,
                Registers = regs,
                AddressClass = addressClass,
                BaseRegister = baseRegister,
                BaseRegisterValue = baseValue,
                ObjectClasses = objectClasses,
                RegisterObjects = registerObjects,
            };
        }
        catch (Exception e)
        {
            return new DumpAnalysis { Error = Localization.L.F("Dump konnte nicht gelesen werden: {0}", e.Message) };
        }
    }

    private static List<DumpModule> ReadModules(BinaryReader br, uint rva)
    {
        var fs = br.BaseStream;
        fs.Position = rva;
        uint count = br.ReadUInt32();
        var result = new List<DumpModule>((int)Math.Min(count, 4096));
        for (uint i = 0; i < count && i < 4096; i++)
        {
            fs.Position = rva + 4 + i * 108;
            ulong baseAddr = br.ReadUInt64();
            uint size = br.ReadUInt32();
            br.ReadUInt32(); // checksum
            br.ReadUInt32(); // timestamp
            uint nameRva = br.ReadUInt32();
            // VS_FIXEDFILEINFO: signature, strucVersion, fileVersionMS, fileVersionLS
            uint sig = br.ReadUInt32();
            br.ReadUInt32();
            uint verMs = br.ReadUInt32();
            uint verLs = br.ReadUInt32();
            string? version = sig == 0xFEEF04BD
                ? $"{verMs >> 16}.{verMs & 0xFFFF}.{verLs >> 16}.{verLs & 0xFFFF}"
                : null;

            string name = ReadMinidumpString(br, nameRva);
            result.Add(new DumpModule(name, baseAddr, size, version));
        }
        return result;
    }

    private static string ReadMinidumpString(BinaryReader br, uint rva)
    {
        if (rva == 0) return "?";
        br.BaseStream.Position = rva;
        uint byteLen = br.ReadUInt32();
        if (byteLen > 4096) return "?";
        return Encoding.Unicode.GetString(br.ReadBytes((int)byteLen));
    }

    private static List<MemRange> ReadMemoryRanges(BinaryReader br, Dictionary<uint, (uint Size, uint Rva)> streams)
    {
        var fs = br.BaseStream;
        var ranges = new List<MemRange>();

        if (streams.TryGetValue(MemoryListStream, out var ml))
        {
            fs.Position = ml.Rva;
            uint n = br.ReadUInt32();
            for (uint i = 0; i < n; i++)
            {
                ulong start = br.ReadUInt64();
                uint size = br.ReadUInt32();
                uint rva = br.ReadUInt32();
                ranges.Add(new MemRange(start, size, rva));
            }
        }

        if (streams.TryGetValue(Memory64ListStream, out var m64))
        {
            fs.Position = m64.Rva;
            ulong n = br.ReadUInt64();
            long dataRva = (long)br.ReadUInt64();
            for (ulong i = 0; i < n; i++)
            {
                ulong start = br.ReadUInt64();
                ulong size = br.ReadUInt64();
                ranges.Add(new MemRange(start, size, dataRva));
                dataRva += (long)size;
            }
        }

        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        return ranges;
    }

    /// <summary>Liest <paramref name="length"/> Byte ab <paramref name="address"/>, sofern der Dump den Bereich vollständig enthält.</summary>
    private static byte[]? ReadAt(BinaryReader br, List<MemRange> memory, ulong address, int length)
    {
        // Bereiche sind nach Startadresse sortiert: binäre Suche nach dem letzten Bereich mit Start <= address
        int lo = 0, hi = memory.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (memory[mid].Start <= address) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        if (found < 0) return null;
        var r = memory[found];
        if (address + (ulong)length > r.Start + r.Size) return null;
        br.BaseStream.Position = r.FileOffset + (long)(address - r.Start);
        var buf = br.ReadBytes(length);
        return buf.Length == length ? buf : null;
    }

    private static bool InAnyModule(IReadOnlyList<DumpModule> modules, ulong value)
    {
        foreach (var m in modules) if (m.Contains(value)) return true;
        return false;
    }

    /// <summary>
    /// Ordnet die Zugriffsadresse ein und sucht das „Zeiger-Register“ (das Register, das die Adresse bis auf einen kleinen Versatz enthielt).
    /// Das sind Hinweise aus den Daten des Dumps, keine Gewissheit über die Ursache.
    /// </summary>
    private static (FaultAddressClass Class, string? BaseRegister, ulong BaseValue) ClassifyFault(
        uint code, ulong[] parameters, int paramCount, List<DumpModule> modules, Dictionary<string, ulong> regs)
    {
        if (code != 0xC0000005 || paramCount < 2) return (FaultAddressClass.Unknown, null, 0);
        ulong addr = parameters[1];

        string? baseReg = null; ulong baseVal = 0;
        foreach (var (name, v) in regs)
        {
            if (name is "Rip" or "Rsp" or "Rbp") continue;
            if (v <= addr && addr - v <= 0x1000 && (baseReg is null || v > baseVal)) { baseReg = name; baseVal = v; }
        }

        FaultAddressClass cls;
        if (addr < 0x10000) cls = FaultAddressClass.NullPage;
        else if (addr > 0x7FFFFFFFFFFF) cls = FaultAddressClass.NotAPointer;
        else if (InAnyModule(modules, addr)) cls = FaultAddressClass.InModule;
        else
        {
            // Heap-Adressen eines Prozesses liegen dicht beieinander: ähnelt die Adresse einem Register-Zeiger, ist sie vermutlich ein echter Heap-Zeiger
            bool heapLike = regs.Any(kv => kv.Key is not ("Rip" or "Rsp" or "Rbp")
                && kv.Value > 0x10000 && !InAnyModule(modules, kv.Value)
                && Math.Abs((long)(kv.Value >> 32) - (long)(addr >> 32)) <= 0x40);
            cls = heapLike ? FaultAddressClass.HeapLikeUnmapped : FaultAddressClass.OtherUnmapped;
        }
        return (cls, baseReg, baseVal);
    }

    /// <summary>
    /// Sucht auf dem Stack und in den Registern nach Zeigern auf Objekte, deren erstes Feld in die Spiel-EXE zeigt (meist die Funktionstabelle
    /// der Klasse). Der Offset ist je Spielversion stabil und dient als Klassen-Kennzeichen, auch ohne Namen. Es sind Kandidaten – nicht jedes
    /// erste Feld ist eine Klassentabelle.
    /// </summary>
    private static (IReadOnlyList<ObjectClassHit>, IReadOnlyList<RegisterObject>) FindObjectClasses(
        BinaryReader br, List<MemRange> memory, List<DumpModule> modules, DumpModule exe, Dictionary<string, ulong> regs, ulong rsp)
    {
        // Zeiger in den Stack selbst zeigen auf Rücksprungadressen und lokale Werte, nicht auf Objekte
        var stackRange = memory.FirstOrDefault(r => rsp >= r.Start && rsp < r.Start + r.Size);

        ulong? ClassOf(ulong pointer)
        {
            if (pointer < 0x10000 || InAnyModule(modules, pointer)) return null;
            if (stackRange.Size != 0 && pointer >= stackRange.Start && pointer < stackRange.Start + stackRange.Size) return null;
            var head = ReadAt(br, memory, pointer, 8);
            if (head is null) return null;
            ulong first = BitConverter.ToUInt64(head, 0);
            return exe.Contains(first) && first - exe.Base >= 0x1000 ? first - exe.Base : null;
        }

        var counts = new Dictionary<ulong, int>();
        var range = stackRange;
        if (range.Size != 0)
        {
            int toRead = (int)Math.Min(range.Start + range.Size - rsp, MaxStackBytesToScan);
            br.BaseStream.Position = range.FileOffset + (long)(rsp - range.Start);
            var data = br.ReadBytes(toRead);
            for (int i = 0; i + 8 <= data.Length; i += 8)
                if (ClassOf(BitConverter.ToUInt64(data, i)) is { } cls)
                    counts[cls] = counts.GetValueOrDefault(cls) + 1;
        }

        var regObjects = new List<RegisterObject>();
        foreach (var (name, value) in regs)
            if (name is not ("Rip" or "Rsp" or "Rbp") && ClassOf(value) is { } cls)
                regObjects.Add(new RegisterObject(name, cls));

        var top = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(8)
            .Select(kv => new ObjectClassHit(kv.Key, kv.Value)).ToList();
        return (top, regObjects);
    }

    private static List<StackHit> ScanStack(BinaryReader br, List<MemRange> memory, List<DumpModule> modules, ulong rsp)
    {
        var hits = new List<StackHit>();
        var range = memory.FirstOrDefault(r => rsp >= r.Start && rsp < r.Start + r.Size);
        if (range.Size == 0) return hits;

        ulong available = range.Start + range.Size - rsp;
        int toRead = (int)Math.Min(available, MaxStackBytesToScan);
        br.BaseStream.Position = range.FileOffset + (long)(rsp - range.Start);
        byte[] data = br.ReadBytes(toRead);

        // Module nach Basisadresse sortiert für schnelle Suche
        var sorted = modules.OrderBy(m => m.Base).ToArray();
        var bases = sorted.Select(m => m.Base).ToArray();

        for (int i = 0; i + 8 <= data.Length && hits.Count < MaxStackHits; i += 8)
        {
            ulong value = BitConverter.ToUInt64(data, i);
            if (value < 0x10000) continue;
            int idx = Array.BinarySearch(bases, value);
            if (idx < 0) idx = ~idx - 1;
            if (idx < 0) continue;
            var mod = sorted[idx];
            if (!mod.Contains(value)) continue;
            ulong offset = value - mod.Base;
            if (offset < 0x1000) continue; // PE-Header, kein Code
            hits.Add(new StackHit(mod, offset));
        }
        return hits;
    }
}
