using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GameDoctor.Core.Ai;

public sealed record AiMessage(string Role, string Content)
{
    public static AiMessage User(string text) => new("user", text);
    public static AiMessage Assistant(string text) => new("assistant", text);
    public static AiMessage System(string text) => new("system", text);
}

/// <summary>Fehler mit einer verständlichen deutschen Meldung für die Oberfläche.</summary>
public sealed class AiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Gemeinsame Schnittstelle aller KI-Anbieter. <see cref="Create"/> wählt die passende Umsetzung.
/// </summary>
public abstract class AiClient : IDisposable
{
    protected readonly AiSettings _settings;

    protected AiClient(AiSettings settings) => _settings = settings;

    public static AiClient Create(AiSettings settings) => settings.Provider.Api switch
    {
        AiApi.Anthropic => new AnthropicAiClient(settings),
        _ => new OpenAiCompatibleClient(settings),
    };

    public string ProviderName => _settings.Provider.Name;
    public string Model => _settings.EffectiveModel;

    /// <summary>Listet die Modelle, die der Dienst anbietet (für die Auswahl in den Einstellungen).</summary>
    public abstract Task<List<string>> ListModelsAsync(CancellationToken ct = default);

    /// <summary>Schickt die Unterhaltung und liefert die Antwort Stück für Stück.</summary>
    public abstract IAsyncEnumerable<string> StreamChatAsync(IReadOnlyList<AiMessage> messages, CancellationToken ct = default);

    public virtual void Dispose() { }
}

/// <summary>
/// Client für jede OpenAI-kompatible Chat-Schnittstelle (OpenRouter, Ollama, LM Studio, OpenAI, Gemini, Groq …).
/// Die Antwort wird gestreamt, damit man sie schon beim Entstehen lesen kann.
/// </summary>
public sealed class OpenAiCompatibleClient : AiClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public OpenAiCompatibleClient(AiSettings settings) : base(settings)
    {
        _baseUrl = settings.EffectiveBaseUrl.TrimEnd('/');
        // Kein Gesamt-Timeout: lange Antworten lokaler Modelle dürfen dauern. Abbrechen geht über das CancellationToken.
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var key = settings.GetApiKey();
        if (!string.IsNullOrEmpty(key))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (settings.ProviderId == "openrouter")
        {
            // Optionale App-Kennung bei OpenRouter, enthält keine persönlichen Daten.
            _http.DefaultRequestHeaders.Add("X-Title", "GameDoctor");
        }
    }

    /// <summary>Listet die Modelle, die der Dienst anbietet (für die Auswahl in den Einstellungen).</summary>
    public override async Task<List<string>> ListModelsAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var resp = await _http.GetAsync($"{_baseUrl}/models", cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode) throw ErrorFor(resp.StatusCode, body);
            var root = JsonNode.Parse(body);
            return (root?["data"] as JsonArray ?? [])
                .Select(m => m?["id"]?.GetValue<string>())
                .OfType<string>()
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception e) when (e is not AiException && !ct.IsCancellationRequested)
        {
            throw Translate(e);
        }
    }

    /// <summary>Schickt die Unterhaltung und liefert die Antwort Stück für Stück.</summary>
    public override async IAsyncEnumerable<string> StreamChatAsync(
        IReadOnlyList<AiMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["model"] = Model,
            ["stream"] = true,
            ["messages"] = new JsonArray(messages
                .Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content })
                .ToArray()),
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            throw Translate(e);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
                throw ErrorFor(resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            bool any = false;
            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(ct);
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    throw Translate(e);
                }
                if (line is null) break;
                // SSE: nur "data:"-Zeilen zählen; Kommentare wie ": OPENROUTER PROCESSING" ignorieren.
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") break;
                if (data.Length == 0) continue;

                JsonNode? node;
                try { node = JsonNode.Parse(data); }
                catch (JsonException) { continue; }

                if (node?["error"] is { } err)
                    throw new AiException("Der Dienst hat die Antwort mit einem Fehler abgebrochen: " + ErrorText(err));

                var delta = node?["choices"]?[0]?["delta"]?["content"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(delta))
                {
                    any = true;
                    yield return delta;
                }
            }
            if (!any)
                throw new AiException("Der Dienst hat eine leere Antwort geschickt. Eventuell unterstützt das gewählte Modell kein Streaming oder ist überlastet – ein anderes Modell versuchen.");
        }
    }

    private AiException Translate(Exception e) => e switch
    {
        AiException a => a,
        HttpRequestException { InnerException: SocketException } or HttpRequestException { StatusCode: null } when _settings.Provider.IsLocal =>
            new AiException($"{ProviderName} ist unter {_baseUrl} nicht erreichbar. Läuft das Programm und ist der lokale Server gestartet?", e),
        HttpRequestException =>
            new AiException($"Keine Verbindung zu {_baseUrl}. Internetverbindung und Adresse prüfen.", e),
        TaskCanceledException or OperationCanceledException =>
            new AiException("Der Dienst hat nicht rechtzeitig geantwortet.", e),
        IOException =>
            new AiException("Die Verbindung ist während der Antwort abgebrochen.", e),
        _ => new AiException("Unerwarteter Fehler: " + e.Message, e),
    };

    private AiException ErrorFor(HttpStatusCode code, string body)
    {
        string detail = "";
        try
        {
            if (JsonNode.Parse(body)?["error"] is { } err) detail = ErrorText(err);
        }
        catch (JsonException) { /* kein JSON */ }
        if (detail.Length == 0) detail = body.Length > 300 ? body[..300] + " …" : body;

        string msg = (int)code switch
        {
            401 or 403 => "Der API-Key wurde abgelehnt. Bitte in den KI-Einstellungen prüfen.",
            402 => "Beim Anbieter ist nicht genug Guthaben vorhanden.",
            404 => $"Adresse oder Modell „{Model}“ nicht gefunden. Modellnamen in den KI-Einstellungen prüfen.",
            408 or 504 => "Der Dienst hat nicht rechtzeitig geantwortet.",
            413 => "Der Prompt ist für dieses Modell zu lang. Weniger Daten auswählen oder ein Modell mit größerem Kontext nehmen.",
            429 => "Zu viele Anfragen oder Gratis-Kontingent aufgebraucht. Kurz warten oder anderes Modell wählen.",
            >= 500 => "Der Dienst hat gerade ein Problem. Später nochmal versuchen.",
            _ => $"Anfrage fehlgeschlagen (HTTP {(int)code}).",
        };
        return new AiException($"{msg}\n\nAntwort des Dienstes: {detail}".TrimEnd());
    }

    private static string ErrorText(JsonNode err) =>
        err is JsonValue v ? v.ToString() : err["message"]?.ToString() ?? err.ToJsonString();

    public override void Dispose() => _http.Dispose();
}
