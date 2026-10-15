using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace RockBot.Host;

/// <summary>
/// Measures every request actually sent to a provider (#670). It sits directly above the raw
/// client, below the function-invoking loop, so each inner iteration is one request here — unlike
/// the usage on the loop's own response, which M.E.AI sums across every iteration.
/// </summary>
/// <remarks>
/// Logs one Information line per request: the provider's input/cached/output tokens, the message
/// list's size, the tool count and schema size, and the six largest <see cref="ContextCategory"/>
/// buckets. At Debug it adds every bucket and the largest messages. Each request is also recorded
/// into <see cref="RequestAccountingContext"/> for the per-turn summary. Behaviour-neutral.
/// </remarks>
public sealed class RequestAccountingChatClient(
    IChatClient inner,
    ILogger<RequestAccountingChatClient> logger,
    string tierLabel) : DelegatingChatClient(inner)
{
    private const int TopCategories = 6;
    private const int TopMessages = 10;

    private static readonly ConditionalWeakTable<AITool, StrongBox<int>> SchemaChars = new();

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var response = await base.GetResponseAsync(list, options, cancellationToken);
        try
        {
            Account(list, options, response.Usage, response.ModelId);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Request accounting threw; ignoring");
        }
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

    private void Account(IReadOnlyList<ChatMessage> messages, ChatOptions? options, UsageDetails? usage, string? modelId)
    {
        var categories = ContextCategorizer.Classify(messages);
        var sizes = new int[messages.Count];
        var byCategory = new Dictionary<string, long>(StringComparer.Ordinal);
        long messageChars = 0;
        for (var i = 0; i < messages.Count; i++)
        {
            sizes[i] = AgentLoopRunner.EstimateMessageChars(messages[i]);
            messageChars += sizes[i];
            byCategory[categories[i]] = byCategory.GetValueOrDefault(categories[i]) + sizes[i];
        }

        var tools = options?.Tools;
        var toolCount = tools?.Count ?? 0;
        long toolSchemaChars = tools?.Sum(MeasureSchema) ?? 0;

        var input = usage?.InputTokenCount ?? 0;
        var cached = usage is null ? 0 : UsageReader.GetCachedInputTokens(usage);
        var output = usage?.OutputTokenCount ?? 0;

        RequestAccountingContext.Value?.Record(
            new LlmRequestMeasurement(input, cached, output, toolCount, messageChars, toolSchemaChars));

        var sessionId = RequestAccountingContext.Value?.SessionId ?? ToolCallSessionContext.SessionId;
        var kind = AgentLoopRunner.ClassifySessionKind(sessionId);
        var tierTag = new KeyValuePair<string, object?>("rockbot.llm.tier", tierLabel);
        var kindTag = new KeyValuePair<string, object?>("rockbot.session.kind", kind);
        if (input > 0)
            HostDiagnostics.LlmRequestTokensInput.Record(input, tierTag, kindTag);
        HostDiagnostics.LlmRequestTools.Record(toolCount, tierTag, kindTag);
        ObserveToolListChange(sessionId, tools, tierTag, kindTag);

        if (!logger.IsEnabled(LogLevel.Information)) return;

        var ordered = byCategory.OrderByDescending(static kv => kv.Value).ToList();
        logger.LogInformation(
            "LLM request: session={Session} tier={Tier} model={Model} input={Input} cached={Cached} ({CachePct:F0}%) " +
            "output={Output} msgs={Msgs} chars={Chars} tools={Tools} toolSchemaChars={SchemaChars} top=[{Top}]",
            sessionId, tierLabel, modelId, input, cached, input > 0 ? cached * 100.0 / input : 0, output,
            messages.Count, messageChars, toolCount, toolSchemaChars,
            FormatCategories(ordered.Take(TopCategories)));

        if (!logger.IsEnabled(LogLevel.Debug)) return;

        logger.LogDebug("LLM request categories: [{Categories}]", FormatCategories(ordered));
        foreach (var i in Enumerable.Range(0, messages.Count).OrderByDescending(i => sizes[i]).Take(TopMessages))
        {
            logger.LogDebug("  Msg[{Idx,3}] {Category,-22} {Chars,8:N0} chars  {Preview}",
                i, categories[i], sizes[i], Preview(messages[i]));
        }
    }

    private const int MaxNamesLogged = 10;

    private void ObserveToolListChange(
        string? sessionId, IList<AITool>? tools,
        KeyValuePair<string, object?> tierTag, KeyValuePair<string, object?> kindTag)
    {
        if (RequestAccountingContext.Value?.ObserveTools(tierLabel, tools) is not { } change) return;

        HostDiagnostics.LlmRequestToolListChanges.Add(1, tierTag, kindTag,
            new KeyValuePair<string, object?>("rockbot.tool_list.change", change.Requested ? "requested" : "unrequested"));

        if (change.Requested)
        {
            logger.LogDebug(
                "Tool list changed after a tool call: session={Session} tier={Tier} tools {Before}->{After} added=[{Added}] removed=[{Removed}]",
                sessionId, tierLabel, change.PreviousCount, change.Count, Names(change.Added), Names(change.Removed));
            return;
        }

        logger.LogWarning(
            "Tool list changed without a tool call, so this request misses the prompt cache (#712): " +
            "session={Session} tier={Tier} tools {Before}->{After} added=[{Added}] removed=[{Removed}]",
            sessionId, tierLabel, change.PreviousCount, change.Count, Names(change.Added), Names(change.Removed));
    }

    private static string Names(IReadOnlyList<string> names) =>
        names.Count <= MaxNamesLogged
            ? string.Join(", ", names)
            : string.Join(", ", names.Take(MaxNamesLogged)) + $", … +{names.Count - MaxNamesLogged}";

    /// <summary>Characters a tool contributes to the request: its name, description and parameter schema.</summary>
    internal static int MeasureSchema(AITool tool) =>
        SchemaChars.GetValue(tool, static t =>
        {
            var chars = t.Name.Length + (t.Description?.Length ?? 0);
            if (t is AIFunctionDeclaration f)
                chars += f.JsonSchema.GetRawText().Length;
            return new StrongBox<int>(chars);
        }).Value;

    private static string FormatCategories(IEnumerable<KeyValuePair<string, long>> categories)
    {
        var sb = new StringBuilder();
        foreach (var (name, chars) in categories)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(name).Append(':').Append(chars);
        }
        return sb.ToString();
    }

    private static string Preview(ChatMessage m)
    {
        if (m.Contents.OfType<FunctionCallContent>().FirstOrDefault() is { } fc)
            return $"call:{fc.Name}({fc.CallId})";
        if (m.Contents.OfType<FunctionResultContent>().FirstOrDefault() is { } fr)
            return $"result:{fr.CallId}";
        var text = (m.Text ?? string.Empty).ReplaceLineEndings(" ");
        return text.Length > 100 ? text[..100] : text;
    }
}
