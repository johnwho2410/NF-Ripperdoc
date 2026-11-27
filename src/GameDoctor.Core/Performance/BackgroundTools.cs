using System.Diagnostics;

namespace GameDoctor.Core.Performance;

/// <summary>
/// Erkennt bekannte Hintergrundprogramme, die beim Spielen kurze Hänger verursachen können. Es werden nur die Namen
/// aus dieser Liste gemeldet – andere laufende Programme werden weder gespeichert noch angezeigt.
/// </summary>
public static class BackgroundTools
{
    // Reihenfolge = Test-Reihenfolge: zuerst Programme, die laufend Hardware-Werte auslesen
    private static readonly (string Name, bool ReadsSensors, string[] Prefixes)[] Known =
    [
        ("HWiNFO", true, ["hwinfo"]),
        ("Stream Dock", true, ["stream dock", "streamdock"]),
        ("Elgato Stream Deck", true, ["streamdeck"]),
        ("MSI Afterburner", true, ["msiafterburner"]),
        ("RivaTuner Statistics Server", true, ["rtss"]),
        ("AIDA64", true, ["aida64"]),
        ("NZXT CAM", true, ["nzxt cam"]),
        ("Corsair iCUE", true, ["icue"]),
        ("ASUS Armoury Crate / Aura", true, ["armourycrate", "armourysocket", "lightingservice"]),
        ("MSI Center", true, ["msi.centralserver", "msi center", "mysticlight"]),
        ("SignalRGB", true, ["signalrgb"]),
        ("OpenRGB", true, ["openrgb"]),
        ("Fan Control", true, ["fancontrol"]),
        ("SpeedFan", true, ["speedfan"]),
        ("LibreHardwareMonitor / OpenHardwareMonitor", true, ["librehardwaremonitor", "openhardwaremonitor"]),
        ("GPU-Z", true, ["gpu-z", "gpuz"]),
        ("Razer Synapse", false, ["razerappengine", "razer synapse"]),
        ("Logitech G HUB", false, ["lghub"]),
        ("SteelSeries GG", false, ["steelseries"]),
        ("Wallpaper Engine", false, ["wallpaper32", "wallpaper64"]),
        ("Lively Wallpaper", false, ["lively"]),
        ("Rainmeter", false, ["rainmeter"]),
        ("Process Lasso", false, ["processlasso", "processgovernor", "bitsumsessionagent"]),
        ("Overwolf", false, ["overwolf"]),
        ("OBS Studio", false, ["obs64"]),
        ("Medal.tv", false, ["medal"]),
        ("Discord", false, ["discord"]),
    ];

    /// <summary>Namen der gerade laufenden bekannten Programme, Sensor-Programme zuerst.</summary>
    public static List<string> Detect()
    {
        try { return Match(Process.GetProcesses().Select(p => { using (p) return p.ProcessName; }).ToList()); }
        catch { return []; }
    }

    public static List<string> Match(IReadOnlyCollection<string> processNames) =>
        Known.Where(k => processNames.Any(n => k.Prefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase))))
            .Select(k => k.Name).ToList();

    public static bool ReadsSensors(string name) => Known.Any(k => k.ReadsSensors && k.Name == name);
}
