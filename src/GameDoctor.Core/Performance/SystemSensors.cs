using System.Runtime.InteropServices;

namespace GameDoctor.Core.Performance;

/// <summary>CPU-Last pro Kern und Arbeitsspeicher – direkt über die Windows-API, unabhängig von der Systemsprache.</summary>
public sealed class CpuSampler
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorPerformanceInfo
    {
        public long IdleTime;
        public long KernelTime; // enthält IdleTime
        public long UserTime;
        public long DpcTime;
        public long InterruptTime;
        public uint InterruptCount;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);

    private const int SystemProcessorPerformanceInformation = 8;
    private ProcessorPerformanceInfo[]? _last;

    public int CoreCount { get; } = Environment.ProcessorCount;

    /// <summary>Liefert die Auslastung je logischem Kern (0–100) seit dem letzten Aufruf.</summary>
    public double[] Sample()
    {
        var now = Query();
        var result = new double[now.Length];
        if (_last is { } last && last.Length == now.Length)
        {
            for (int i = 0; i < now.Length; i++)
            {
                long idle = now[i].IdleTime - last[i].IdleTime;
                long total = (now[i].KernelTime - last[i].KernelTime) + (now[i].UserTime - last[i].UserTime);
                result[i] = total > 0 ? Math.Clamp(100.0 * (total - idle) / total, 0, 100) : 0;
            }
        }
        _last = now;
        return result;
    }

    private ProcessorPerformanceInfo[] Query()
    {
        int size = Marshal.SizeOf<ProcessorPerformanceInfo>();
        int count = CoreCount;
        IntPtr buffer = Marshal.AllocHGlobal(size * count);
        try
        {
            NtQuerySystemInformation(SystemProcessorPerformanceInformation, buffer, size * count, out _);
            var arr = new ProcessorPerformanceInfo[count];
            for (int i = 0; i < count; i++)
                arr[i] = Marshal.PtrToStructure<ProcessorPerformanceInfo>(buffer + i * size);
            return arr;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

public static class MemoryInfo
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile; // Commit-Limit
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    public readonly record struct Snapshot(double UsedPhysGb, double TotalPhysGb, double CommitUsedGb, double CommitLimitGb);

    public static Snapshot Read()
    {
        var s = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        GlobalMemoryStatusEx(ref s);
        const double gb = 1024d * 1024 * 1024;
        return new Snapshot(
            (s.TotalPhys - s.AvailPhys) / gb,
            s.TotalPhys / gb,
            (s.TotalPageFile - s.AvailPageFile) / gb,
            s.TotalPageFile / gb);
    }
}
