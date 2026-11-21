using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameDoctor.Core.Community;

public enum FixAction
{
    DisableMod,
    UpdateMod,
    ChangeSetting,
    Other,
}

public enum UserVerdict
{
    None,
    Helped,
    NotHelped,
}

/// <summary>
/// Ein Lösungsversuch des Spielers, z. B. „Boneware deaktiviert, um die Abstürze bei 0x1ADE00 loszuwerden“.
/// Wird lokal gespeichert; geteilt wird höchstens das anonyme Ergebnis (siehe <see cref="FixOutcomeRecord"/>).
/// </summary>
public sealed record FixAttempt
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTime StartedAt { get; init; } = DateTime.Now;
    public FixAction Action { get; init; }
    /// <summary>Anzeigename, z. B. „Boneware“ – nur lokal.</summary>
    public required string TargetLabel { get; init; }
    /// <summary>Öffentliche Nexus-ID, falls bekannt – nur damit ist das Ergebnis teilbar.</summary>
    public int? NexusModId { get; init; }
    /// <summary>DLL-Name, falls die Mod im Aufrufpfad lag (z. B. „Boneware.dll“) – teilbar auch ohne Nexus-ID.</summary>
    public string? ModuleFile { get; init; }
    /// <summary>Dateien der Mod (Dateinamen, keine Pfade) – zur Prüfung, ob sie wirklich deaktiviert ist.</summary>
    public List<string> TargetFiles { get; init; } = [];
    /// <summary>Absturzstellen, die dieser Versuch beheben soll.</summary>
    public List<string> Signatures { get; init; } = [];
    public string? GameBuild { get; init; }
    /// <summary>Freitext-Notiz – bleibt immer lokal.</summary>
    public string? Note { get; init; }
    public UserVerdict Verdict { get; init; }
    public DateTime? VerdictAt { get; init; }
}

public static class FixAttemptStore
{
    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameDoctor", "fix-attempts.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static List<FixAttempt> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<FixAttempt>>(File.ReadAllText(FilePath), Json) ?? [];
        }
        catch { /* defekt */ }
        return [];
    }

    public static void Save(List<FixAttempt> attempts)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(attempts.OrderByDescending(a => a.StartedAt), Json));
    }

    public static void Upsert(FixAttempt attempt)
    {
        var all = Load();
        all.RemoveAll(a => a.Id == attempt.Id);
        all.Add(attempt);
        Save(all);
    }

    public static void Remove(Guid id)
    {
        var all = Load();
        all.RemoveAll(a => a.Id == id);
        Save(all);
    }
}
