using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameDoctor.Core.Ai;

/// <summary>Ein vorkonfigurierter KI-Anbieter. Alle sprechen das OpenAI-kompatible Chat-Format.</summary>
public sealed record AiProviderPreset(
    string Id,
    string Name,
    string BaseUrl,
    string DefaultModel,
    bool NeedsKey,
    bool IsLocal,
    string Hint);

public static class AiProviders
{
    public static readonly IReadOnlyList<AiProviderPreset> All =
    [
        new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", "google/gemini-2.5-flash", NeedsKey: true, IsLocal: false,
            "Ein Konto für viele Modelle (Gemini, Claude, GPT …). Key unter openrouter.ai/keys erstellen. Eine Auswertung kostet meist nur Cent-Beträge."),
        new("ollama", "Ollama (lokal)", "http://localhost:11434/v1", "", NeedsKey: false, IsLocal: true,
            "Läuft komplett auf deinem PC, kein Konto nötig. Ollama muss gestartet sein und ein Modell geladen haben (z. B. „ollama pull qwen3“)."),
        new("lmstudio", "LM Studio (lokal)", "http://localhost:1234/v1", "", NeedsKey: false, IsLocal: true,
            "Läuft komplett auf deinem PC, kein Konto nötig. In LM Studio unter „Developer“ den lokalen Server starten."),
        new("custom", "Anderer Dienst", "", "", NeedsKey: true, IsLocal: false,
            "Jeder Dienst mit OpenAI-kompatibler Schnittstelle (…/v1/chat/completions), z. B. OpenAI, Google Gemini, Groq oder ein eigener Server."),
    ];

    public static AiProviderPreset Get(string? id) => All.FirstOrDefault(p => p.Id == id) ?? All[0];
}

/// <summary>
/// KI-Einstellungen des aktuellen Windows-Benutzers. GameDoctor bringt bewusst keinen eigenen Key mit:
/// Jeder nutzt seinen eigenen Anbieter. Der API-Key wird mit Windows-DPAPI verschlüsselt gespeichert
/// und ist nur für diesen Windows-Benutzer auf diesem PC lesbar.
/// </summary>
public sealed class AiSettings
{
    public string ProviderId { get; set; } = "openrouter";
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>DPAPI-verschlüsselt, Base64. Nie im Klartext speichern oder loggen.</summary>
    public string? EncryptedApiKey { get; set; }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GameDoctor.AiSettings.v1");

    [JsonIgnore] public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameDoctor", "ai-settings.json");

    [JsonIgnore] public AiProviderPreset Provider => AiProviders.Get(ProviderId);

    [JsonIgnore] public string EffectiveBaseUrl => string.IsNullOrWhiteSpace(BaseUrl) ? Provider.BaseUrl : BaseUrl.Trim();
    [JsonIgnore] public string EffectiveModel => string.IsNullOrWhiteSpace(Model) ? Provider.DefaultModel : Model.Trim();

    [JsonIgnore] public bool HasApiKey => !string.IsNullOrEmpty(EncryptedApiKey);

    /// <summary>Fertig eingerichtet, um eine Anfrage zu schicken?</summary>
    [JsonIgnore] public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(EffectiveBaseUrl) &&
        !string.IsNullOrWhiteSpace(EffectiveModel) &&
        (!Provider.NeedsKey || HasApiKey);

    public string? GetApiKey()
    {
        if (string.IsNullOrEmpty(EncryptedApiKey)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(Convert.FromBase64String(EncryptedApiKey), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            // Z. B. Datei von einem anderen PC/Benutzer kopiert – dann muss der Key neu eingegeben werden.
            return null;
        }
    }

    public void SetApiKey(string? key)
    {
        key = key?.Trim();
        EncryptedApiKey = string.IsNullOrEmpty(key)
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser));
    }

    public static AiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(FilePath)) ?? new AiSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Kaputte Datei: mit Standardwerten weitermachen statt abzustürzen.
        }
        return new AiSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
