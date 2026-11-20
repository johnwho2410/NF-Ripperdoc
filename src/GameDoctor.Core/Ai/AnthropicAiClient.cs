using System.Runtime.CompilerServices;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;

namespace GameDoctor.Core.Ai;

/// <summary>
/// Client für Claude direkt bei Anthropic, über das offizielle Anthropic-SDK.
/// Die Antwort wird gestreamt. Bei Modellen, die es unterstützen, springt bei einer Ablehnung durch die
/// Sicherheitsfilter serverseitig automatisch ein anderes Modell ein (Crash-Analysen mit Stacks und
/// Speicheradressen können solche Filter gelegentlich fälschlich auslösen).
/// </summary>
public sealed class AnthropicAiClient : AiClient
{
    private readonly AnthropicClient _client;

    /// <summary>Modelle, die serverseitige Ersatzmodelle bei Ablehnungen unterstützen.</summary>
    private static readonly HashSet<string> FallbackModels =
        ["claude-fable-5-2", "claude-fable-5-1", "claude-opus-5-5", "claude-opus-5", "claude-sonnet-5-5"];

    public AnthropicAiClient(AiSettings settings) : base(settings)
    {
        // Adresse immer ausdrücklich setzen: Sonst nimmt das SDK eine evtl. gesetzte Umgebungsvariable
        // ANTHROPIC_BASE_URL, und der Key ginge an einen fremden Server. Standard ist api.anthropic.com.
        // Das SDK schickt den Key als "Authorization: Bearer" – so ist es dort vorgesehen.
        _client = new AnthropicClient
        {
            ApiKey = settings.GetApiKey() ?? "",
            BaseUrl = settings.EffectiveBaseUrl.TrimEnd('/'),
        };
    }

    public override async Task<List<string>> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            var page = await _client.Models.List(cancellationToken: ct);
            return page.Items.Select(m => m.ID).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            throw Translate(e);
        }
    }

    public override async IAsyncEnumerable<string> StreamChatAsync(
        IReadOnlyList<AiMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var system = string.Join("\n\n", messages.Where(m => m.Role == "system").Select(m => m.Content));
        var turns = messages
            .Where(m => m.Role != "system")
            .Select(m => m.Role == "assistant"
                ? (BetaMessageParam)new BetaAssistantMessageParam { Content = m.Content }
                : new BetaUserMessageParam { Content = m.Content })
            .ToList();

        var parameters = new MessageCreateParams
        {
            Model = Model,
            MaxTokens = 64000,
            Messages = turns,
        };
        if (system.Length > 0) parameters = parameters with { System = system };
        if (FallbackModels.Contains(Model))
            parameters = parameters with { Betas = ["server-side-fallback-2026-07-01"], Fallbacks = new Default() };

        var stream = _client.Beta.Messages.CreateStreaming(parameters, ct).GetAsyncEnumerator(ct);
        bool any = false;
        try
        {
            while (true)
            {
                BetaRawMessageStreamEvent ev;
                try
                {
                    if (!await stream.MoveNextAsync()) break;
                    ev = stream.Current;
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    throw Translate(e);
                }

                if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                {
                    any = true;
                    yield return text.Text;
                }
                else if (ev.TryPickDelta(out var md) && md.Delta.StopReason == "refusal")
                {
                    throw new AiException("Claude hat die Auswertung abgelehnt (Sicherheitsfilter). " +
                                          "Versuch es mit weniger Daten (z. B. ohne Mod-Logs) oder über einen anderen Anbieter.");
                }
            }
        }
        finally
        {
            await stream.DisposeAsync();
        }
        if (!any)
            throw new AiException("Claude hat eine leere Antwort geschickt. Bitte nochmal versuchen.");
    }

    private AiException Translate(Exception e) => e switch
    {
        AiException a => a,
        AnthropicUnauthorizedException or AnthropicForbiddenException =>
            new AiException("Der API-Key wurde von Anthropic abgelehnt. Bitte in den KI-Einstellungen prüfen.", e),
        AnthropicNotFoundException =>
            new AiException($"Modell „{Model}“ nicht gefunden. Über „Modelle laden“ ein verfügbares Modell auswählen.", e),
        AnthropicRateLimitException =>
            new AiException("Zu viele Anfragen an Anthropic. Kurz warten und nochmal versuchen.", e),
        AnthropicBadRequestException when e.Message.Contains("credit", StringComparison.OrdinalIgnoreCase) =>
            new AiException("Dein Anthropic-Guthaben reicht nicht. Unter platform.claude.com → Billing aufladen.", e),
        AnthropicBadRequestException =>
            new AiException("Anthropic hat die Anfrage abgelehnt: " + e.Message, e),
        Anthropic5xxException =>
            new AiException("Anthropic hat gerade ein Problem. Später nochmal versuchen.", e),
        AnthropicIOException or HttpRequestException =>
            new AiException("Keine Verbindung zu Anthropic. Internetverbindung prüfen.", e),
        AnthropicApiException =>
            new AiException("Anfrage an Anthropic fehlgeschlagen: " + e.Message, e),
        _ => new AiException("Unerwarteter Fehler: " + e.Message, e),
    };
}
