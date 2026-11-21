using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameDoctor.Core.Analysis;

namespace GameDoctor.Core.Community;

/// <summary>Erzeugt aus einem analysierten Absturz einen anonymen <see cref="CrashFingerprint"/>.</summary>
public sealed class FingerprintBuilder
{
    private readonly ModuleClassifier _classifier;
    private readonly byte[] _secret;
    private readonly ModInventory? _inventory;

    public FingerprintBuilder(ModuleClassifier classifier, byte[] secret, ModInventory? inventory = null)
    {
        _classifier = classifier;
        _secret = secret;
        _inventory = inventory;
    }

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Baut den Fingerabdruck und prüft ihn. Gibt null zurück, wenn der Absturz nicht verwertbar ist oder die Prüfung anschlägt.</summary>
    public CrashFingerprint? TryBuild(CrashCase c, AnalysisResult r, out IReadOnlyList<PrivacyGuard.Violation> violations)
    {
        violations = [];
        var rep = c.Report;
        var d = c.Dump;
        var build = BuildNumber(rep.InternalVersion) ?? r.GameVersion;
        if (d is null || d.Error is not null || build is null || c.Signature == "?") return null;

        var fp = new CrashFingerprint
        {
            GameBuild = build,
            Patch = rep.Patch,
            Signature = c.Signature,
            FaultKind = c.FaultKind.ToString(),
            Exception = ExceptionClass(d),
            ModulesOnStack = StackModules(d),
            UptimeClass = rep.UptimeSeconds switch
            {
                null => null,
                < 120 => "<2m",
                < 600 => "2-10m",
                < 3600 => "10-60m",
                _ => ">60m",
            },
            DuringLoading = rep.LoadingStage is { } s && s != "Finished",
            VramNearlyFull = rep.VramUsedMb is int u && rep.VramTotalMb is int t && t > 0 && u >= 0.92 * t,
            OutOfMemory = rep.OutOfMemory,
            WorldCell = rep.Position is { } p
                ? $"{(int)Math.Floor(p.X / 500)},{(int)Math.Floor(p.Y / 500)}"
                : null,
            Frameworks = Frameworks(r),
            GpuVendor = GpuVendor(rep.GpuName),
            GpuSeries = GpuSeries(rep.GpuName),
            RamClass = RamClass(rep),
            ModCountClass = ModCountClass(r),
            DedupKey = DedupKey(build, c.Signature),
        };

        // Versionsnummern (z. B. 1.2.3.4) sind erlaubt, obwohl sie formal wie eine IP-Adresse aussehen
        var versions = fp.ModulesOnStack.Select(m => m.Version).Concat(fp.Frameworks.Values).Append(fp.GameBuild).OfType<string>();
        violations = PrivacyGuard.Check(JsonSerializer.Serialize(fp, Json), versions);
        return violations.Count == 0 ? fp : null;
    }

    // ------------------------------------------------------------------ Vergröberung

    private static string? BuildNumber(string? internalVersion) =>
        internalVersion is null ? null : Regex.Match(internalVersion, @"^\s*(\d+\.\d+\.\d+)").Groups[1].Value is { Length: > 0 } v ? v : null;

    private static string ExceptionClass(DumpAnalysis d)
    {
        var name = d.ExceptionCode switch
        {
            0xC0000005 => "AccessViolation",
            0xC00000FD => "StackOverflow",
            0xC0000409 => "FailFast",
            0xC000001D => "IllegalInstruction",
            0xC0000374 => "HeapCorruption",
            0xE06D7363 => "CppException",
            0x80000003 => "Breakpoint",
            _ => $"0x{d.ExceptionCode:X8}",
        };
        if (d.ExceptionCode != 0xC0000005 || d.ExceptionParameters.Length < 2) return name;
        var op = d.ExceptionParameters[0] switch { 0 => "Read", 1 => "Write", 8 => "Execute", _ => "Other" };
        // Nur die Klasse der Adresse, nie die Adresse selbst (Speicheradressen sind pro Sitzung zufällig, aber unnötig)
        var target = d.ExceptionParameters[1] < 0x10000 ? "Null" : "Heap";
        return $"{name}/{op}/{target}";
    }

    private List<FingerprintModule> StackModules(DumpAnalysis d) =>
        d.StackHits.Take(40)
            .Select(h => h.Module)
            .Where(m => _classifier.Classify(m) is ModuleKind.Mod or ModuleKind.ModFramework or ModuleKind.Overlay)
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new FingerprintModule(CleanFileName(g.Key), CleanVersion(g.First().FileVersion), _classifier.Classify(g.First()).ToString())
            {
                // Öffentliche Nexus-ID, falls die DLL aus einem Nexus-Paket stammt
                NexusModId = _inventory?.ForFile(g.Key)?.NexusId,
            })
            .ToList();

    /// <summary>Nur der Dateiname, ohne unsichtbare Steuerzeichen.</summary>
    private static string CleanFileName(string name) =>
        new(Path.GetFileName(name).Where(ch => char.GetUnicodeCategory(ch) != UnicodeCategory.Format).ToArray());

    private static string? CleanVersion(string? v) =>
        v is not null && Regex.IsMatch(v, @"^\d+(\.\d+){0,3}$") && v != "0.0.0.0" ? v : null;

    private static Dictionary<string, string> Frameworks(AnalysisResult r)
    {
        var f = new Dictionary<string, string>();
        if (r.ModLogs?.Red4ExtVersion is { } red) f["RED4ext"] = red;
        if (r.ModLogs?.CetVersion is { } cet) f["CET"] = cet.TrimStart('v');
        return f;
    }

    internal static string? GpuVendor(string? gpu) => gpu switch
    {
        null => null,
        _ when gpu.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || gpu.Contains("GeForce", StringComparison.OrdinalIgnoreCase) => "NVIDIA",
        _ when gpu.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || gpu.Contains("AMD", StringComparison.OrdinalIgnoreCase) => "AMD",
        _ when gpu.Contains("Intel", StringComparison.OrdinalIgnoreCase) || gpu.Contains("Arc", StringComparison.OrdinalIgnoreCase) => "Intel",
        _ => "Andere",
    };

    /// <summary>"NVIDIA GeForce RTX 4060 Ti" → "RTX 40", "Radeon RX 7800 XT" → "RX 7000", "Arc A770" → "Arc A".</summary>
    internal static string? GpuSeries(string? gpu)
    {
        if (gpu is null) return null;
        var m = Regex.Match(gpu, @"\b(RTX|GTX)\s*(\d{2})\d{2}", RegexOptions.IgnoreCase);
        if (m.Success) return $"{m.Groups[1].Value.ToUpperInvariant()} {m.Groups[2].Value}";
        m = Regex.Match(gpu, @"\bRX\s*(\d)\d{3}", RegexOptions.IgnoreCase);
        if (m.Success) return $"RX {m.Groups[1].Value}000";
        m = Regex.Match(gpu, @"\bArc\s*([A-Z])\d", RegexOptions.IgnoreCase);
        if (m.Success) return $"Arc {m.Groups[1].Value.ToUpperInvariant()}";
        return null;
    }

    private static string? RamClass(Games.Cyberpunk.RedCrashReport rep)
    {
        if (rep.SysInfo is not { } sys || !sys.TryGetProperty("TotalPhysicalMemory", out var tm)) return null;
        double gb = tm.GetInt64() / 1024d / 1024 / 1024;
        return gb switch { < 12 => "8", < 24 => "16", < 48 => "32", _ => "64+" };
    }

    private static string? ModCountClass(AnalysisResult r)
    {
        if (r.ModLogs is not { } ml) return null;
        int n = ml.ArchiveModCount + ml.Red4ExtPluginCount + ml.RedscriptModCount + ml.CetModCount;
        return n switch { < 50 => "<50", < 150 => "50-149", < 300 => "150-299", _ => "300+" };
    }

    private string DedupKey(string build, string signature) => CommunityKeys.Derive(_secret, $"{build}|{signature}");
}
