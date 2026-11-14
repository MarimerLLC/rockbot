using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AGUI.Abstractions;

namespace RockBot.AgUi.Client;

/// <summary>One server-sent event: its <c>id</c> (the notify stream has one, a run has none) and its AG-UI event.</summary>
public readonly record struct AgUiFrame(string? Id, BaseEvent Event);

/// <summary>
/// Reads a proxy event stream: server-sent events whose <c>data</c> is one AG-UI event in JSON.
/// A run (<c>POST /agui</c>) and the notify stream (<c>GET /agui/notify</c>) are the same format,
/// so this one reader serves both.
/// </summary>
public static class AgUiSse
{
    /// <summary>
    /// The stream's events, as they arrive, until it ends. Comments (the proxy's keepalives) are
    /// skipped by the parser. An event this SDK version doesn't know, or one that isn't valid
    /// JSON, is skipped, as AG-UI asks of a client.
    /// </summary>
    public static async IAsyncEnumerable<AgUiFrame> ReadAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var parser = SseParser.Create(stream, static (_, data) => Parse(data));
        await foreach (var item in parser.EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            if (item.Data is { } e)
                yield return new AgUiFrame(item.EventId, e);
        }
    }

    private static BaseEvent? Parse(ReadOnlySpan<byte> data)
    {
        try
        {
            return JsonSerializer.Deserialize(data, AGUIJsonSerializerContext.Default.BaseEvent);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }
}
