using System.ClientModel.Primitives;

namespace RockBot.Host;

/// <summary>
/// Stamps outgoing requests with the OpenRouter app-attribution headers so the account's
/// activity dashboard and public rankings show this agent by name instead of "unknown".
/// </summary>
/// <remarks>
/// <para>
/// OpenRouter attributes a request to an app purely from two self-reported headers:
/// <c>HTTP-Referer</c> (the identity key it groups activity by) and <c>X-Title</c> (the
/// display name). Neither is authenticated and neither has an equivalent on
/// <see cref="Microsoft.Extensions.AI.ChatOptions"/> or <c>OpenAIClientOptions</c>, so — as
/// with <see cref="RepetitionPenaltyPolicy"/> — the pipeline is the only place to add them.
/// Note the header is OpenRouter's custom <c>HTTP-Referer</c> spelling, not the standard
/// <c>Referer</c>; sending the latter attributes nothing.
/// </para>
/// <para>
/// Registration is gated on <see cref="IsOpenRouterEndpoint"/> at the call site so the app
/// name is only disclosed to the provider that asked for it. Every other OpenAI-compatible
/// endpoint — Ollama, Foundry, a local llama.cpp — sees a byte-identical request to before.
/// </para>
/// </remarks>
public sealed class OpenRouterAttributionPolicy(string appName, string appUrl) : PipelinePolicy
{
    /// <summary>Name reported when <c>LLM:AppName</c> is unset.</summary>
    public const string DefaultAppName = "rockbot";

    /// <summary>Referer reported when <c>LLM:AppUrl</c> is unset.</summary>
    public const string DefaultAppUrl = "https://rockbot.dev";

    /// <summary>OpenRouter's identity key. Grouping on the dashboard is by this value.</summary>
    private const string RefererHeader = "HTTP-Referer";

    /// <summary>Display name shown alongside the referer in the OpenRouter UI.</summary>
    private const string TitleHeader = "X-Title";

    public override void Process(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }

    private void Apply(PipelineMessage message)
    {
        var request = message.Request;
        if (request is null) return;

        // Set, not Add: a retried message runs the per-call pipeline again, and Add would
        // append a second value rather than replace the existing one.
        request.Headers.Set(RefererHeader, appUrl);
        request.Headers.Set(TitleHeader, appName);
    }

    /// <summary>
    /// True when <paramref name="endpoint"/> is served by OpenRouter, i.e. the host is
    /// <c>openrouter.ai</c> or a subdomain of it.
    /// </summary>
    /// <remarks>
    /// Matched on the host rather than a substring of the whole URL so that a path or query
    /// mentioning openrouter — a proxy at <c>https://gateway.internal/openrouter/v1</c> — is
    /// not mistaken for the real thing. A deliberate proxy in front of OpenRouter therefore
    /// needs the headers forwarded by the proxy, or this predicate widened.
    /// </remarks>
    public static bool IsOpenRouterEndpoint(string? endpoint)
        => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && IsOpenRouterEndpoint(uri);

    /// <inheritdoc cref="IsOpenRouterEndpoint(string?)"/>
    public static bool IsOpenRouterEndpoint(Uri uri)
    {
        var host = uri.Host;
        return host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".openrouter.ai", StringComparison.OrdinalIgnoreCase);
    }
}
