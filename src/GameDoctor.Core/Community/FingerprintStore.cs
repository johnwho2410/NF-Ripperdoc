using System.Security.Cryptography;
using System.Text.Json;

namespace GameDoctor.Core.Community;

/// <summary>
/// Lokale Ablage der Fingerabdrücke (%LOCALAPPDATA%\GameDoctor\community) und des geheimen Schlüssels für den
/// Doppelzähl-Schutz. Der Schlüssel verlässt den PC nie und ist zusätzlich per DPAPI an das Windows-Konto gebunden.
/// </summary>
public static class FingerprintStore
{
    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameDoctor", "community");

    private static string SecretPath => Path.Combine(Dir, "dedup.key");
    private static string FingerprintPath => Path.Combine(Dir, "fingerprints.json");

    /// <summary>Liest den lokalen Schlüssel oder legt einen neuen, zufälligen an.</summary>
    public static byte[] GetOrCreateSecret()
    {
        Directory.CreateDirectory(Dir);
        if (File.Exists(SecretPath))
        {
            try { return ProtectedData.Unprotect(File.ReadAllBytes(SecretPath), null, DataProtectionScope.CurrentUser); }
            catch (CryptographicException) { /* defekt oder anderes Konto – neu erzeugen */ }
        }
        var secret = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(SecretPath, ProtectedData.Protect(secret, null, DataProtectionScope.CurrentUser));
        return secret;
    }

    /// <summary>
    /// Schlüssel neu würfeln: Danach sind alle künftigen Beiträge nicht einmal mehr über die Absturzstelle
    /// mit früheren verknüpfbar („Neustart als Unbekannter“).
    /// </summary>
    public static void ResetSecret()
    {
        if (File.Exists(SecretPath)) File.Delete(SecretPath);
        GetOrCreateSecret();
    }

    public static List<CrashFingerprint> Load()
    {
        if (!File.Exists(FingerprintPath)) return [];
        try { return JsonSerializer.Deserialize<List<CrashFingerprint>>(File.ReadAllText(FingerprintPath)) ?? []; }
        catch { return []; }
    }

    /// <summary>Speichert neue Fingerabdrücke; pro DedupKey nur einer (der erste bleibt).</summary>
    public static int Merge(IEnumerable<CrashFingerprint> fingerprints)
    {
        var all = Load();
        var known = all.Select(f => f.DedupKey).ToHashSet();
        int added = 0;
        foreach (var fp in fingerprints)
            if (known.Add(fp.DedupKey)) { all.Add(fp); added++; }
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FingerprintPath, JsonSerializer.Serialize(all, FingerprintBuilder.Json));
        return added;
    }
}
