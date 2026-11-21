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
    private const int ContextRspOffset = 0x98;

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

            // Stackpointer aus dem Exception-Kontext holen und Stack nach Rücksprungadressen absuchen
            var hits = new List<StackHit>();
            if (ctxRva != 0 && ctxSize > ContextRspOffset + 8)
            {
                fs.Position = ctxRva + ContextRspOffset;
                ulong rsp = br.ReadUInt64();
                hits = ScanStack(br, memory, modules, rsp);
            }

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
