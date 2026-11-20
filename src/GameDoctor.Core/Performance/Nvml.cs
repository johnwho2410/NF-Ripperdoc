using System.Runtime.InteropServices;
using System.Text;

namespace GameDoctor.Core.Performance;

/// <summary>
/// NVIDIA Management Library (nvml.dll, wird mit dem Grafiktreiber installiert).
/// Liefert GPU-Last, VRAM, Temperatur, Takt, Leistungsaufnahme und Drosselungsgründe – ohne Adminrechte.
/// </summary>
public sealed class NvidiaGpu : IDisposable
{
    private const string Lib = "nvml.dll";

    [StructLayout(LayoutKind.Sequential)] private struct NvmlMemory { public ulong Total, Free, Used; }
    [StructLayout(LayoutKind.Sequential)] private struct NvmlUtilization { public uint Gpu, Memory; }

    [DllImport(Lib, EntryPoint = "nvmlInit_v2")] private static extern int Init();
    [DllImport(Lib, EntryPoint = "nvmlShutdown")] private static extern int Shutdown();
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] private static extern int GetHandle(uint index, out IntPtr device);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetName")] private static extern int GetName(IntPtr device, byte[] name, uint length);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetUtilizationRates")] private static extern int GetUtilization(IntPtr device, out NvmlUtilization util);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetMemoryInfo")] private static extern int GetMemory(IntPtr device, out NvmlMemory mem);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetTemperature")] private static extern int GetTemperature(IntPtr device, int sensor, out uint temp);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetClockInfo")] private static extern int GetClock(IntPtr device, int type, out uint mhz);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetPowerUsage")] private static extern int GetPower(IntPtr device, out uint milliwatts);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetEnforcedPowerLimit")] private static extern int GetPowerLimit(IntPtr device, out uint milliwatts);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetCurrentClocksThrottleReasons")] private static extern int GetThrottleReasons(IntPtr device, out ulong reasons);

    // Drosselungsgründe (nvmlClocksThrottleReason*)
    public const ulong ThrottleSwPowerCap = 0x4;
    public const ulong ThrottleHwSlowdown = 0x8;
    public const ulong ThrottleSwThermal = 0x20;
    public const ulong ThrottleHwThermal = 0x40;
    public const ulong ThrottleHwPowerBrake = 0x80;

    private readonly IntPtr _device;
    public string Name { get; }

    private NvidiaGpu(IntPtr device, string name)
    {
        _device = device;
        Name = name;
    }

    /// <summary>Öffnet die erste NVIDIA-GPU; null, wenn keine vorhanden oder NVML fehlt.</summary>
    public static NvidiaGpu? TryOpen()
    {
        try
        {
            if (Init() != 0) return null;
            if (GetHandle(0, out var dev) != 0) { Shutdown(); return null; }
            var buf = new byte[96];
            GetName(dev, buf, (uint)buf.Length);
            var name = Encoding.ASCII.GetString(buf).TrimEnd('\0');
            return new NvidiaGpu(dev, name);
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    public readonly record struct Reading(
        double? UtilPct, double? VramUsedMb, double? VramTotalMb, double? TempC,
        double? ClockMhz, double? PowerW, double? PowerLimitW, ulong ThrottleReasons);

    public Reading Read()
    {
        double? util = GetUtilization(_device, out var u) == 0 ? u.Gpu : null;
        double? used = null, total = null;
        if (GetMemory(_device, out var m) == 0) { used = m.Used / 1048576.0; total = m.Total / 1048576.0; }
        double? temp = GetTemperature(_device, 0, out var t) == 0 ? t : null;
        double? clock = GetClock(_device, 0, out var c) == 0 ? c : null;
        double? power = GetPower(_device, out var p) == 0 ? p / 1000.0 : null;
        double? limit = GetPowerLimit(_device, out var l) == 0 ? l / 1000.0 : null;
        ulong reasons = 0;
        try { if (GetThrottleReasons(_device, out var r) == 0) reasons = r; } catch (EntryPointNotFoundException) { }
        return new Reading(util, used, total, temp, clock, power, limit, reasons);
    }

    public void Dispose()
    {
        try { Shutdown(); } catch { /* egal */ }
    }
}
