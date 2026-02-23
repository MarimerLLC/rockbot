using System.ClientModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Llm;

namespace RockBot.Host;

/// <summary>
/// Subclass of <see cref="FunctionInvokingChatClient"/> that preserves the infrastructure
/// <see cref="AgentLoopRunner"/> previously provided for native tool-calling models:
/// progress notifications, consecutive timeout detection, and context overflow recovery.
/// </summary>
public class RockBotFunctionInvokingChatClient : FunctionInvokingChatClient
{
    private const int MaxConsecutiveTimeoutIterations = 2;

    private readonly IToolProgressNotifier? _progressNotifier;
    private readonly ModelBehavior _modelBehavior;
    private readonly ILogger _logger;

    private int _consecutiveTimeoutIterations;
    private int? _knownContextLimit;

    public RockBotFunctionInvokingChatClient(
        IChatClient innerClient,
        IToolProgressNotifier? progressNotifier,
        ModelBehavior modelBehavior,
        ILogger logger) : base(innerClient)
    {
        _progressNotifier = progressNotifier;
        _modelBehavior = modelBehavior;
        _logger = logger;

        MaximumIterationsPerRequest = modelBehavior.MaxToolIterationsOverride ?? 12;
    }

    protected override async ValueTask<object?> InvokeFunctionAsync(
        FunctionInvocationContext context,
        CancellationToken cancellationToken)
    {
        var callContent = context.CallContent;
        var argsSummary = callContent.Arguments is { Count: > 0 }
            ? string.Join(", ", callContent.Arguments.Select(a => $"{a.Key}={a.Value}"))
            : null;

        _logger.LogInformation("Executing tool {Name}(callId={CallId}, args={Args})",
            callContent.Name, callContent.CallId, argsSummary ?? "(none)");

        if (_progressNotifier is not null)
        {
            var desc = DescribeToolCall(callContent.Name, argsSummary);
            await _progressNotifier.OnToolInvokingAsync(callContent.Name, desc, cancellationToken);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await base.InvokeFunctionAsync(context, cancellationToken);
        sw.Stop();

        var resultStr = result?.ToString();
        _logger.LogInformation("Tool {Name} returned in {ElapsedMs}ms: {Result}",
            callContent.Name, sw.ElapsedMilliseconds,
            resultStr is { Length: > 200 } ? resultStr[..200] + "..." : resultStr);

        // Track consecutive timeouts
        if (resultStr is not null && IsTimeoutResult(resultStr))
        {
            _consecutiveTimeoutIterations++;
            if (_consecutiveTimeoutIterations >= MaxConsecutiveTimeoutIterations)
            {
                _logger.LogWarning(
                    "Aborting: {N} consecutive iterations with tool timeouts",
                    _consecutiveTimeoutIterations);
            }
        }
        else
        {
            _consecutiveTimeoutIterations = 0;
        }

        if (_progressNotifier is not null)
        {
            var summary = resultStr is { Length: > 100 } ? resultStr[..100] + "..." : resultStr;
            await _progressNotifier.OnToolInvokedAsync(callContent.Name, summary, cancellationToken);
        }

        return result;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _consecutiveTimeoutIterations = 0;
        _logger.LogInformation(
            "RockBotFunctionInvokingChatClient handling request (maxIterations={MaxIter})",
            MaximumIterationsPerRequest);
        var messageList = messages as List<ChatMessage> ?? [.. messages];

        if (_knownContextLimit is int preLimit)
            TrimLargeToolResults(messageList, preLimit);

        ChatResponse response;
        try
        {
            response = await base.GetResponseAsync(messageList, options, cancellationToken);
        }
        catch (ClientResultException ex)
            when (ex.Status == 400 && TryParseContextOverflow(ex.Message, out var max, out var used))
        {
            _knownContextLimit = max;
            _logger.LogWarning(
                "Context overflow ({Used:N0}/{Max:N0} tokens); trimming tool results and retrying once",
                used, max);
            TrimLargeToolResults(messageList, max);
            response = await base.GetResponseAsync(messageList, options, cancellationToken);
        }

        // If the response looks like max-iterations was hit (incomplete setup phrase),
        // make one follow-up call asking for a backward-looking summary.
        var text = ExtractAssistantText(response);
        if (AgentLoopRunner.IsIncompleteSetupPhrase(text) || string.IsNullOrWhiteSpace(text))
        {
            var hasToolCalls = response.Messages
                .Any(m => m.Contents.OfType<FunctionCallContent>().Any());
            if (hasToolCalls || string.IsNullOrWhiteSpace(text))
            {
                _logger.LogInformation(
                    "Response looks incomplete after tool loop; requesting backward-looking summary");

                var summaryMessages = new List<ChatMessage>(messageList);
                summaryMessages.AddRange(response.Messages);
                summaryMessages.Add(new ChatMessage(ChatRole.User,
                    "The task loop has ended. Write a concise summary of what was accomplished. " +
                    "Report only what was completed — do not describe intentions or future actions."));

                var summaryResponse = await InnerClient.GetResponseAsync(
                    summaryMessages, new ChatOptions(), cancellationToken);

                var summaryText = ExtractAssistantText(summaryResponse);
                if (!string.IsNullOrWhiteSpace(summaryText))
                {
                    foreach (var msg in summaryResponse.Messages)
                        response.Messages.Add(msg);
                }
            }
        }

        return response;
    }

    private void TrimLargeToolResults(List<ChatMessage> messages, int maxTokens)
    {
        const int CharsPerToken = 4;
        var charBudget = (int)(maxTokens * CharsPerToken * 0.9);

        while (true)
        {
            var totalChars = messages.Sum(EstimateMessageChars);
            if (totalChars <= charBudget)
                break;

            int bestMsg = -1, bestContent = -1, bestLen = 0;
            for (var i = 0; i < messages.Count; i++)
            {
                if (messages[i].Role != ChatRole.Tool) continue;
                for (var j = 0; j < messages[i].Contents.Count; j++)
                {
                    if (messages[i].Contents[j] is FunctionResultContent frc)
                    {
                        var len = frc.Result?.ToString()?.Length ?? 0;
                        if (len > bestLen) { bestMsg = i; bestContent = j; bestLen = len; }
                    }
                }
            }

            if (bestMsg < 0)
                break;

            var old = (FunctionResultContent)messages[bestMsg].Contents[bestContent];
            var oldStr = old.Result?.ToString() ?? string.Empty;
            var excess = totalChars - charBudget;
            var targetLen = Math.Max(200, oldStr.Length - excess - 60);
            var trimmed = oldStr[..targetLen] + "\n[truncated to fit context window]";

            messages[bestMsg].Contents[bestContent] = new FunctionResultContent(old.CallId, trimmed);

            _logger.LogInformation(
                "Trimmed tool result for call {CallId}: {Before:N0} → {After:N0} chars",
                old.CallId, bestLen, trimmed.Length);
        }
    }

    private static int EstimateMessageChars(ChatMessage m) =>
        m.Contents.Sum(static c => c switch
        {
            TextContent tc => tc.Text?.Length ?? 0,
            FunctionResultContent frc => frc.Result?.ToString()?.Length ?? 0,
            _ => 50
        });

    private static bool TryParseContextOverflow(string message, out int maxTokens, out int usedTokens)
    {
        maxTokens = 0;
        usedTokens = 0;

        var maxMatch = Regex.Match(message, @"maximum context length is (\d+)");
        var usedMatch = Regex.Match(message, @"resulted in (\d+) tokens");

        if (!maxMatch.Success || !usedMatch.Success)
            return false;

        maxTokens = int.Parse(maxMatch.Groups[1].Value);
        usedTokens = int.Parse(usedMatch.Groups[1].Value);
        return true;
    }

    private static string ExtractAssistantText(ChatResponse response)
    {
        for (var i = response.Messages.Count - 1; i >= 0; i--)
        {
            var msg = response.Messages[i];
            if (msg.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(msg.Text))
                return msg.Text.Trim();
        }

        return response.Text?.Trim() ?? string.Empty;
    }

    private static bool IsTimeoutResult(string result) =>
        result.Contains("timed out", StringComparison.OrdinalIgnoreCase);

    private static string DescribeToolCall(string name, string? argsSummary)
    {
        if (string.IsNullOrEmpty(argsSummary)) return name;
        return $"{name}({argsSummary})";
    }
}
