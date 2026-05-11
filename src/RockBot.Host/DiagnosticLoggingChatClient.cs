using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace RockBot.Host;

/// <summary>
/// Diagnostic wrapper that inspects assistant messages returned by the inner
/// <see cref="IChatClient"/> and logs a warning when the response shows the
/// fingerprint of duplicated content reported by users — either multiple
/// <see cref="TextContent"/> items in one assistant message, or a single
/// <see cref="TextContent"/> whose text contains a long substring repeated
/// across a single newline boundary. Behaviour-neutral; only logs.
/// </summary>
public sealed class DiagnosticLoggingChatClient : DelegatingChatClient
{
    private readonly ILogger<DiagnosticLoggingChatClient> _logger;
    private readonly string _tierLabel;

    public DiagnosticLoggingChatClient(
        IChatClient inner,
        ILogger<DiagnosticLoggingChatClient> logger,
        string tierLabel)
        : base(inner)
    {
        _logger = logger;
        _tierLabel = tierLabel;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        Inspect(response);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            yield return update;
    }

    private void Inspect(ChatResponse response)
    {
        try
        {
            for (var i = 0; i < response.Messages.Count; i++)
            {
                var msg = response.Messages[i];
                if (msg.Role != ChatRole.Assistant) continue;

                var textContents = msg.Contents.OfType<TextContent>().ToList();

                if (textContents.Count > 1)
                {
                    _logger.LogWarning(
                        "Duplication-watch [{Tier}]: assistant message[{Idx}] has {Count} TextContent items " +
                        "(lengths=[{Lengths}]); ChatMessage.Text concatenates without separators. " +
                        "Contents types=[{Types}]",
                        _tierLabel, i, textContents.Count,
                        string.Join(",", textContents.Select(t => t.Text?.Length ?? 0)),
                        string.Join(",", msg.Contents.Select(c => c.GetType().Name)));
                }

                if (textContents.Count >= 1
                    && LooksDuplicated(textContents[textContents.Count - 1].Text, out var prefixLen, out var sample))
                {
                    _logger.LogWarning(
                        "Duplication-watch [{Tier}]: assistant message[{Idx}] TextContent contains a repeated " +
                        "{PrefixLen}-char prefix after a newline (totalLen={TotalLen}, contentsCount={ContentsCount}, " +
                        "sample40=\"{Sample}\"). Source is the upstream provider, not RockBot middleware.",
                        _tierLabel, i, prefixLen, textContents[textContents.Count - 1].Text!.Length,
                        msg.Contents.Count, sample);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Duplication-watch inspection threw; ignoring");
        }
    }

    /// <summary>
    /// True when <paramref name="text"/> contains a substring of at least 40 characters
    /// that appears once at the beginning and again immediately after a single newline.
    /// Catches both exact "X\nX" duplication and partial "X.Y\nX" truncated repeats.
    /// </summary>
    internal static bool LooksDuplicated(string? text, out int prefixLen, out string sample)
    {
        prefixLen = 0;
        sample = string.Empty;
        if (string.IsNullOrEmpty(text) || text.Length < 80) return false;

        const int probeLen = 40;
        var probe = text.AsSpan(0, Math.Min(probeLen, text.Length));
        var probeStr = probe.ToString();
        var needle = "\n" + probeStr;
        var idx = text.IndexOf(needle, StringComparison.Ordinal);
        if (idx <= 0) return false;

        prefixLen = probe.Length;
        sample = probeStr;
        return true;
    }
}
