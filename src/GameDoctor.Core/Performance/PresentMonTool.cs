using System.Security.Cryptography;

namespace GameDoctor.Core.Performance;

/// <summary>
/// Findet bzw. lädt PresentMon (Intel, MIT-Lizenz) – das Werkzeug, das per ETW die Frame-Daten aufzeichnet.
/// Der Download ist auf eine feste Version mit bekanntem SHA-256 festgelegt.
/// </summary>
public static class PresentMonTool
{
    public const string Version = "2.6.0";
    public const string FileName = "PresentMon-2.6.0-x64.exe";
    public const string DownloadUrl = "https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0-x64.exe";
    private const string Sha256 = "B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF";

    public static string ToolsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameDoctor", "tools");

    public static string? Find()
    {
        foreach (var dir in new[] { ToolsDir, Path.Combine(AppContext.BaseDirectory, "tools"), AppContext.BaseDirectory })
        {
            var exact = Path.Combine(dir, FileName);
            if (File.Exists(exact)) return exact;
            if (Directory.Exists(dir))
            {
                var any = Directory.EnumerateFiles(dir, "PresentMon*x64.exe").OrderDescending().FirstOrDefault();
                if (any is not null) return any;
            }
        }
        return null;
    }

    /// <summary>Lädt PresentMon herunter und prüft die Prüfsumme. Nur nach Zustimmung des Nutzers aufrufen.</summary>
    public static async Task<string> DownloadAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(ToolsDir);
        var target = Path.Combine(ToolsDir, FileName);
        var temp = target + ".download";

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GameDoctor/0.1");
        var bytes = await http.GetByteArrayAsync(DownloadUrl, ct);

        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!hash.Equals(Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Localization.L.T("Die heruntergeladene PresentMon-Datei hat eine unerwartete Prüfsumme und wurde verworfen."));

        await File.WriteAllBytesAsync(temp, bytes, ct);
        File.Move(temp, target, overwrite: true);
        return target;
    }
}
