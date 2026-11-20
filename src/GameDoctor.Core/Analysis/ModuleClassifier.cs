namespace GameDoctor.Core.Analysis;

/// <summary>
/// Ordnet ein geladenes Modul einer Kategorie zu (Spiel, Mod, Treiber, Overlay, Windows …).
/// Minidumps des REDEngine-Reporters enthalten oft nur Dateinamen ohne Pfad – deshalb wird der
/// Name gegen die echten Dateien im Spielordner aufgelöst (bei Namensgleichheit über die Image-Größe).
/// </summary>
public sealed class ModuleClassifier
{
    private readonly string? _gameRoot;
    private readonly string _windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private readonly string _system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
    private readonly Dictionary<string, List<string>> _gameFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, uint?> _imageSizeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string, uint), string> _resolved = new();

    public ModuleClassifier(string? gameRoot)
    {
        _gameRoot = gameRoot is null ? null : gameRoot.TrimEnd('\\') + "\\";
        if (gameRoot is not null) IndexGameFiles(gameRoot);
    }

    private void IndexGameFiles(string root)
    {
        void Add(string dir, SearchOption opt, params string[] patterns)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var pattern in patterns)
            foreach (var f in Directory.EnumerateFiles(dir, pattern, opt))
            {
                var name = Path.GetFileName(f);
                if (!_gameFiles.TryGetValue(name, out var list)) _gameFiles[name] = list = [];
                list.Add(f);
            }
        }

        var bin = Path.Combine(root, "bin", "x64");
        Add(bin, SearchOption.TopDirectoryOnly, "*.dll", "*.asi");
        Add(Path.Combine(bin, "plugins"), SearchOption.AllDirectories, "*.dll", "*.asi");
        Add(Path.Combine(root, "red4ext"), SearchOption.AllDirectories, "*.dll");
    }

    // Mit dem Spiel ausgelieferte Bibliotheken (Vanilla)
    private static readonly string[] GameRuntimePrefixes =
    [
        "libxell", "libxess", "sl.", "nvngx", "redlexer", "bink2", "icu", "redgalaxy", "galaxy", "physx", "pxfoundation", "pxpvdsdk",
        "amd_fidelityfx", "amd_ags", "oo2ext", "gfsdk", "ffx_", "nvlowlatency", "d3dcompiler", "xinput", "libcurl",
        "dstorage", "nrd", "redprelauncher", "api-ms-win", "cchromaeditor", "dbgcore", "dbghelp", "dxilconv", "freetype",
        "gameservices", "gfnruntimesdk", "libeay32", "ssleay32", "nvtoolsext", "steam_api", "symsrv", "winpixeventruntime",
    ];

    // Grafiktreiber
    private static readonly string[] GpuDriverPrefixes =
    [
        "nvwgf2um", "nvldumd", "nvd3dum", "nvoglv", "nvgpucomp", "nvapi", "nvcuda", "nvrtc", "nvptxjit",
        "amdxc", "atidxx", "aticfx", "amdihk", "amdenc", "atiumd", "amdxx",
        "igd12um", "igd10iumd", "igdumdim", "igxelpicd", "igc64",
    ];

    // Overlays & Hooks, die sich in Spiele einklinken
    private static readonly (string Prefix, string Product)[] OverlayPrefixes =
    [
        ("gameoverlayrenderer", "Steam-Overlay"),
        ("discordhook", "Discord-Overlay"),
        ("rtsshooks", "RivaTuner Statistics Server"),
        ("nvspcap", "NVIDIA-Overlay / ShadowPlay"),
        ("nvcamera", "NVIDIA Ansel/Freestyle"),
        ("owclient", "Overwolf"),
        ("ow-graphics", "Overwolf"),
        ("overwolf", "Overwolf"),
        ("medal", "Medal.tv"),
        ("graphics-hook", "OBS Game Capture"),
        ("reshade", "ReShade"),
        ("specialk", "Special K"),
        ("xsplit", "XSplit"),
        ("msi_afterburner", "MSI Afterburner"),
    ];

    private static readonly string[] ModFrameworkNames =
    [
        "red4ext.dll", "cyber_engine_tweaks.asi", "archivexl.dll", "tweakxl.dll", "codeware.dll",
        "input_loader.dll", "mod_settings.dll", "redscript",
    ];

    /// <summary>Liefert den vollständigen Pfad des Moduls (aufgelöst, falls der Dump nur den Namen enthält).</summary>
    public string ResolvePath(DumpModule module)
    {
        if (Path.IsPathRooted(module.Path)) return module.Path;
        var key = (module.Name.ToLowerInvariant(), module.Size);
        if (_resolved.TryGetValue(key, out var cached)) return cached;

        string result = module.Path;
        if (_gameFiles.TryGetValue(module.Name, out var candidates))
        {
            // Gleicher Name wie eine Windows-DLL (version.dll, winmm.dll …)? Dann über die Image-Größe unterscheiden.
            var sizeMatch = candidates.FirstOrDefault(c => ImageSize(c) == module.Size);
            var sysFile = Path.Combine(_system32, module.Name);
            bool sysMatches = File.Exists(sysFile) && ImageSize(sysFile) == module.Size;

            if (sizeMatch is not null) result = sizeMatch;
            else if (sysMatches) result = sysFile;
            else if (!File.Exists(sysFile)) result = candidates[0];
            else result = sysFile;
        }
        else
        {
            var sysFile = Path.Combine(_system32, module.Name);
            if (File.Exists(sysFile)) result = sysFile;
        }

        _resolved[key] = result;
        return result;
    }

    private uint? ImageSize(string file)
    {
        if (_imageSizeCache.TryGetValue(file, out var s)) return s;
        uint? size = null;
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);
            fs.Position = 0x3C;
            int peOffset = br.ReadInt32();
            fs.Position = peOffset + 24 + 56; // PE-Signatur(4) + COFF-Header(20) -> OptionalHeader.SizeOfImage (Offset 56)
            size = br.ReadUInt32();
        }
        catch
        {
            // nicht lesbar – unbekannt
        }
        _imageSizeCache[file] = size;
        return size;
    }

    public ModuleKind Classify(DumpModule module) => Classify(ResolvePath(module));

    public ModuleKind Classify(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        var full = path.ToLowerInvariant();

        if (name == "cyberpunk2077.exe") return ModuleKind.GameExecutable;
        if (OverlayPrefixes.Any(o => name.StartsWith(o.Prefix, StringComparison.Ordinal))) return ModuleKind.Overlay;
        if (GpuDriverPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal))) return ModuleKind.GpuDriver;
        if (full.Contains(@"\driverstore\filerepository\")) return ModuleKind.GpuDriver;

        if (_gameRoot is not null && full.StartsWith(_gameRoot.ToLowerInvariant(), StringComparison.Ordinal))
        {
            if (ModFrameworkNames.Any(n => name.StartsWith(n, StringComparison.Ordinal))) return ModuleKind.ModFramework;
            if (full.Contains(@"\red4ext\plugins\") || full.Contains(@"\cyber_engine_tweaks\") || name.EndsWith(".asi"))
                return ModuleKind.Mod;
            // Proxy-DLLs im Spielordner = Mod-Loader (version.dll = ASI-Loader für CET, winmm.dll = RED4ext-Loader) oder ReShade
            if (name is "version.dll" or "winmm.dll" or "dinput8.dll" or "winhttp.dll") return ModuleKind.ModFramework;
            if (name is "dxgi.dll" or "d3d11.dll" or "d3d12.dll") return ModuleKind.Overlay;
            if (GameRuntimePrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal))) return ModuleKind.GameRuntime;
            return ModuleKind.Mod;
        }

        if (ModFrameworkNames.Any(n => name.StartsWith(n, StringComparison.Ordinal))) return ModuleKind.ModFramework;
        if (full.StartsWith(_windowsDir.ToLowerInvariant(), StringComparison.Ordinal)) return ModuleKind.System;
        if (!Path.IsPathRooted(path))
            return GameRuntimePrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)) ? ModuleKind.GameRuntime : ModuleKind.Unknown;
        return ModuleKind.ThirdParty;
    }

    public static string? OverlayProduct(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        return OverlayPrefixes.FirstOrDefault(o => name.StartsWith(o.Prefix, StringComparison.Ordinal)).Product
               ?? (name is "dxgi.dll" or "d3d11.dll" or "d3d12.dll" ? "ReShade o. ä. (Proxy-DLL im Spielordner)" : null);
    }

    /// <summary>Kurzer, lesbarer Name für Mods (z. B. Plugin-Ordner statt DLL-Name).</summary>
    public string FriendlyName(DumpModule module)
    {
        var p = ResolvePath(module);
        foreach (var marker in new[] { @"\red4ext\plugins\", @"\cyber_engine_tweaks\mods\" })
        {
            int idx = p.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var folder = p[(idx + marker.Length)..].Split('\\')[0];
            return folder.Equals(module.Name, StringComparison.OrdinalIgnoreCase) ? folder : $"{folder} ({module.Name})";
        }
        var name = module.Name.ToLowerInvariant();
        if (_gameRoot is not null && p.StartsWith(_gameRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (name == "version.dll") return "Ultimate ASI Loader (version.dll, lädt CET)";
            if (name == "winmm.dll") return "RED4ext-Loader (winmm.dll)";
        }
        return module.Name;
    }

    public static string KindLabel(ModuleKind kind) => kind switch
    {
        ModuleKind.GameExecutable => "Spiel (Engine)",
        ModuleKind.GameRuntime => "Spiel-Bibliothek",
        ModuleKind.ModFramework => "Mod-Framework",
        ModuleKind.Mod => "Mod",
        ModuleKind.GpuDriver => "Grafiktreiber",
        ModuleKind.Overlay => "Overlay/Hook",
        ModuleKind.System => "Windows",
        ModuleKind.ThirdParty => "Fremdsoftware",
        _ => "unbekannt",
    };
}
