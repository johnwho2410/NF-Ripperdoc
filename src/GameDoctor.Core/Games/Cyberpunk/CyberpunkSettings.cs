using System.Text.Json;
using GameDoctor.Core.Localization;

namespace GameDoctor.Core.Games.Cyberpunk;

/// <summary>Liest die Grafikeinstellungen aus %LOCALAPPDATA%\CD Projekt Red\Cyberpunk 2077\UserSettings.json.</summary>
public sealed class CyberpunkSettings
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CD Projekt Red", "Cyberpunk 2077", "UserSettings.json");

    public static CyberpunkSettings? TryLoad(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var s = new CyberpunkSettings();
            foreach (var group in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                var g = group.GetProperty("group_name").GetString() ?? "";
                foreach (var opt in group.GetProperty("options").EnumerateArray())
                {
                    if (!opt.TryGetProperty("name", out var n) || !opt.TryGetProperty("value", out var v)) continue;
                    var value = v.ValueKind switch
                    {
                        JsonValueKind.String => v.GetString() ?? "",
                        JsonValueKind.True => "True",
                        JsonValueKind.False => "False",
                        _ => v.GetRawText(),
                    };
                    s._values[$"{g}/{n.GetString()}"] = value;
                }
            }
            return s;
        }
        catch
        {
            return null;
        }
    }

    public string? Get(string key) => _values.GetValueOrDefault(key);
    public bool IsTrue(string key) => Get(key) == "True";

    public string? Resolution => Get("/video/display/Resolution");
    public string? Upscaler => Get("/graphics/presets/ResolutionScaling");
    public string? DlssMode => Get("/graphics/presets/DLSS");
    public bool RayTracing => IsTrue("/graphics/raytracing/RayTracing");
    public bool PathTracing => IsTrue("/graphics/raytracing/RayTracedPathTracing");
    public string? RtLighting => Get("/graphics/raytracing/RayTracedLighting");
    public bool RtReflections => IsTrue("/graphics/raytracing/RayTracedReflections");
    public string? TextureQuality => Get("/graphics/presets/TextureQuality");
    public string? CrowdDensity => Get("/gameplay/performance/CrowdDensity") ?? Get("/graphics/performance/CrowdDensity");
    public string? Reflex => Get("/video/display/ReflexMode");
    public string? FrameGeneration => Get("/graphics/presets/FrameGeneration");
    public bool DlssFrameGen => IsTrue("/graphics/presets/DLSSFrameGen");
    public string? WindowMode => Get("/video/display/WindowMode");
    public bool VSync => Get("/video/display/VSync") is { } v && !v.EndsWith("Off", StringComparison.OrdinalIgnoreCase);
    public bool FpsCapOn => IsTrue("/video/display/MaximumFPS_OnOff");
    public string? FpsCap => Get("/video/display/MaximumFPS_Value");

    public string Summary()
    {
        var parts = new List<string> { $"{Resolution ?? "?"}", $"{Upscaler ?? "?"} {DlssMode}".Trim() };
        parts.Add(PathTracing ? L.T("Pathtracing") : RayTracing
            ? L.F("Raytracing (Licht: {0}, Spiegelungen: {1})", RtLighting ?? "?", RtReflections ? L.T("an") : L.T("aus"))
            : L.T("kein Raytracing"));
        parts.Add(L.F("Texturen {0}", TextureQuality ?? "?"));
        parts.Add($"Crowd {CrowdDensity ?? "?"}");
        parts.Add($"Reflex {Reflex ?? "?"}");
        if (DlssFrameGen || FrameGeneration is { } fg && fg != "Off") parts.Add(L.T("Frame Generation an"));
        return string.Join(" · ", parts);
    }
}
