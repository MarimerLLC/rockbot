using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Llm;
using RockBot.McpMeasure.Agent;
using RockBot.McpMeasure.Fixtures;
using RockBot.McpMeasure.Hosting;
using RockBot.Tools;

namespace RockBot.McpMeasure.Measurement;

/// <summary>A model under test: its label (usually the RockBot tier it stands for) and client.</summary>
public sealed record ModelUnderTest(string Label, string Tier, string ModelId, IChatClient Client, ModelBehavior Behavior);

public sealed record RunSettings(int MaxIterations, string SystemPrompt, bool Verbose);

/// <summary>One turn of one run of one task, under one model and mode: the row in results.jsonl.</summary>
public sealed class TurnResult
{
    public required string Model { get; init; }
    public required string ModelId { get; init; }
    public required string Tier { get; init; }
    public required string Mode { get; init; }
    public required string Task { get; init; }
    public required int Run { get; init; }

    public bool FirstCallCorrect { get; set; }
    public bool FirstCallWellFormed { get; set; }
    public bool Completed { get; set; }
    public string? Failure { get; set; }

    public int ToolCalls { get; set; }
    public int DownstreamAttempts { get; set; }
    public int MetaCalls { get; set; }
    public int LlmRequests { get; set; }
    public int ToolsOffered { get; set; }
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public double WallMs { get; set; }

    public List<DownstreamAttempt> Attempts { get; set; } = [];
    public string? Error { get; set; }
    public string? FinalText { get; set; }
}

/// <summary>
/// Runs a task through RockBot's real agent loop (<see cref="AgentLoopRunner"/> over
/// <see cref="RockBotFunctionInvokingChatClient"/>, or the text-based path when the model's
/// behavior says so) with the gateway's tool list built as <c>UserMessageHandler</c> builds it.
/// Follow-up, completion evaluation and reasoning scaffolding are off, so a turn ends when the
/// model answers; the measurement is about the calls.
/// </summary>
public sealed class Runner(FixtureHost fixtures, ILoggerFactory loggers)
{
    private const int MaxFinalTextChars = 600;

    private readonly InMemoryWorkingMemory _workingMemory = new();
    private readonly IOptions<AgentHostOptions> _hostOptions = Options.Create(new AgentHostOptions());
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "rockbot-mcp-measure-agent-" + Guid.NewGuid().ToString("N"));
    private LlmCostEstimator? _costEstimator;

    public async Task<IReadOnlyList<TurnResult>> RunAsync(
        MeasureRig rig, Grader grader, ModelUnderTest model, MeasureTask task, int run, RunSettings settings, CancellationToken ct)
    {
        var capture = new CaptureChatClient(model.Client);
        var loop = CreateLoop(capture, model.Behavior, rig);
        var sessionId = $"measure-{Guid.NewGuid():N}";
        var toolSession = $"session/{sessionId}";

        var history = new List<ChatMessage> { new(ChatRole.System, settings.SystemPrompt) };
        if (!string.IsNullOrEmpty(model.Behavior.AdditionalSystemPrompt))
            history.Add(new ChatMessage(ChatRole.System, model.Behavior.AdditionalSystemPrompt));

        var results = new List<TurnResult>();
        for (var i = 0; i < task.Turns.Count; i++)
        {
            var turn = task.Turns[i];
            var result = new TurnResult
            {
                Model = model.Label,
                ModelId = model.ModelId,
                Tier = model.Tier,
                Mode = rig.Mode.ToString(),
                Task = task.Turns.Count == 1 ? task.Id : $"{task.Id}#{i + 1}",
                Run = run
            };

            history.Add(new ChatMessage(ChatRole.User, turn.Prompt));
            var messages = new List<ChatMessage>(history);
            var options = new ChatOptions
            {
                Tools = [.. rig.Registry.BuildAgentToolFunctions(toolSession, Guid.NewGuid().ToString("N"), ToolProfiles.Main)]
            };

            var requests = capture.BeginTurn();
            var recorded = fixtures.Recorder.Count;
            var sw = Stopwatch.StartNew();
            string? reply = null;
            try
            {
                reply = await loop.RunAsync(messages, options, sessionId,
                    enableFollowUp: false,
                    enableCompletionEval: false,
                    enableReasoningScaffolding: false,
                    maxIterationsOverride: settings.MaxIterations,
                    cancellationToken: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                result.Error = $"{ex.GetType().Name}: {ex.Message}";
            }
            result.WallMs = sw.Elapsed.TotalMilliseconds;

            var calls = requests.SelectMany(r => r.Calls).ToList();
            var grade = grader.Grade(turn, calls, reply ?? requests.LastOrDefault()?.Text);

            result.Attempts = [.. grade.Attempts];
            result.FirstCallCorrect = grade.FirstCallCorrect;
            result.FirstCallWellFormed = grade.FirstCallWellFormed;
            result.Failure = result.Error is not null && grade.Attempts.Count == 0 ? Outcome.Error : grade.FirstCallFailure;
            result.Completed = fixtures.Recorder.Since(recorded).Any(c =>
                c.Valid && c.Tool == turn.Tool && string.Equals(c.Server, turn.Server, StringComparison.OrdinalIgnoreCase));
            result.ToolCalls = calls.Count;
            result.DownstreamAttempts = grade.Attempts.Count;
            result.MetaCalls = grade.MetaCalls;
            result.LlmRequests = requests.Count;
            result.ToolsOffered = requests.FirstOrDefault()?.ToolsOffered ?? 0;
            result.InputTokens = requests.Sum(r => r.InputTokens);
            result.CachedInputTokens = requests.Sum(r => r.CachedInputTokens);
            result.OutputTokens = requests.Sum(r => r.OutputTokens);
            result.FinalText = reply is null ? null : reply.Length <= MaxFinalTextChars ? reply : reply[..MaxFinalTextChars] + "…";
            results.Add(result);

            if (settings.Verbose)
                Log(result, requests);

            // The next turn sees the conversation as conversation memory replays it: the user's
            // messages and the agent's replies, not the tool traffic.
            history.Add(new ChatMessage(ChatRole.Assistant, reply ?? "(no reply)"));
        }

        return results;
    }

    private AgentLoopRunner CreateLoop(IChatClient capture, ModelBehavior behavior, MeasureRig rig)
    {
        Directory.CreateDirectory(_scratch);
        // One estimator per runner: it watches its pricing file.
        _costEstimator ??= new LlmCostEstimator(
            Options.Create(new LlmPricingOptions { ConfigPath = Path.Combine(_scratch, "llm-pricing.json") }),
            loggers.CreateLogger<LlmCostEstimator>());

        IChatClient client = behavior.UseTextBasedToolCalling
            ? capture
            : new RockBotFunctionInvokingChatClient(capture, null, null, behavior, _costEstimator,
                _workingMemory, _hostOptions, loggers.CreateLogger<RockBotFunctionInvokingChatClient>());

        return new AgentLoopRunner(
            new SingleModelLlmClient(client),
            _workingMemory,
            behavior,
            new NullFeedbackStore(),
            new AgentClock(new ConfigurationBuilder().Build(),
                Options.Create(new AgentProfileOptions { BasePath = _scratch }), loggers.CreateLogger<AgentClock>()),
            _hostOptions,
            new NullSkillStore(),
            [],
            new NullConversationMemory(),
            loggers.CreateLogger<AgentLoopRunner>(),
            typedToolSurface: rig.Surface);
    }

    private static void Log(TurnResult result, IReadOnlyList<LlmRequestRecord> requests)
    {
        Console.WriteLine($"    {result.Task} run {result.Run}: correct={result.FirstCallCorrect} completed={result.Completed} " +
                          $"failure={result.Failure ?? "-"} calls={result.ToolCalls} in={result.InputTokens} {result.Error}");
        foreach (var request in requests)
        {
            foreach (var call in request.Calls)
                Console.WriteLine($"      {call.Name}({System.Text.Json.JsonSerializer.Serialize(call.Arguments)})");
        }
    }
}
