using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// What started an <see cref="AgentLoopRunner.RunAsync"/> run, for the consequential-action gate
/// (#685). Only runs that trace back to a user's chat message need that message to have asked for
/// an external change; automation the user configured keeps acting on its own.
/// </summary>
public enum RunOrigin
{
    /// <summary>Not stated by the caller and not nested in another run. External changes are allowed (pre-#685 behaviour).</summary>
    Unknown,
    /// <summary>A user's chat message (<c>UserMessageHandler</c>), or the synthesis turn relaying work spawned from one.</summary>
    UserTurn,
    /// <summary>A subagent spawned from a user turn. It inherits that turn's <see cref="UserRequestKind"/>.</summary>
    SubagentOfUserTurn,
    /// <summary>A scheduled task or patrol the user configured. External changes are allowed.</summary>
    Scheduled,
    /// <summary>A task another agent sent in over A2A. External changes are allowed.</summary>
    A2A,
    /// <summary>Internal background work (dream passes and similar). External changes are allowed.</summary>
    Background,
}

/// <summary>
/// The run's origin and, for runs that trace back to a user message, whether that message asked
/// for anything (#685). Bound to the async flow by <see cref="AgentLoopRunner.RunAsync"/> through
/// <see cref="ActionGateContext"/>, so wisps, workers and subagents started from the run inherit it.
/// </summary>
/// <param name="Origin">What started the run.</param>
/// <param name="UserAskedFor">For <see cref="RunOrigin.UserTurn"/> / <see cref="RunOrigin.SubagentOfUserTurn"/>:
/// the classification of the originating user request. Null on a scope a caller passes in means
/// "classify the run's own user message" (<see cref="CompletionEvalTriggers.ClassifyUserRequest"/>).</param>
public sealed record ActionGateScope(RunOrigin Origin, UserRequestKind? UserAskedFor = null)
{
    /// <summary>A user chat turn; <see cref="AgentLoopRunner.RunAsync"/> classifies its latest user message.</summary>
    public static ActionGateScope ForUserTurn { get; } = new(RunOrigin.UserTurn);

    /// <summary>A scheduled task or patrol run.</summary>
    public static ActionGateScope ForScheduledTask { get; } = new(RunOrigin.Scheduled);

    /// <summary>A task received from another agent over A2A.</summary>
    public static ActionGateScope ForA2AInbound { get; } = new(RunOrigin.A2A);

    /// <summary>Neither stated nor inherited: external changes are allowed.</summary>
    public static ActionGateScope Unknown { get; } = new(RunOrigin.Unknown);

    /// <summary>True when an external change needs the originating user message to have asked for it.</summary>
    public bool RequiresUserInstruction => Origin is RunOrigin.UserTurn or RunOrigin.SubagentOfUserTurn;

    /// <summary>True when the run may change external systems.</summary>
    public bool AllowsExternalChanges => !RequiresUserInstruction || UserAskedFor != UserRequestKind.InformationOnly;

    /// <summary>The scope a subagent spawned from this run runs under.</summary>
    public ActionGateScope ForSubagent() =>
        Origin == RunOrigin.UserTurn ? this with { Origin = RunOrigin.SubagentOfUserTurn } : this;

    /// <summary>The <c>origin=</c> value logged and carried on <see cref="SubagentResultMessage.RunOrigin"/>.</summary>
    public static string LogName(RunOrigin origin) => origin switch
    {
        RunOrigin.UserTurn => "user-turn",
        RunOrigin.SubagentOfUserTurn => "subagent-of-user-turn",
        RunOrigin.Scheduled => "scheduled",
        RunOrigin.A2A => "a2a",
        RunOrigin.Background => "background",
        _ => "unknown",
    };

    /// <summary>Parses a value written by <see cref="LogName(RunOrigin)"/>; null when blank or unknown.</summary>
    public static RunOrigin? ParseOrigin(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "user-turn" => RunOrigin.UserTurn,
        "subagent-of-user-turn" => RunOrigin.SubagentOfUserTurn,
        "scheduled" => RunOrigin.Scheduled,
        "a2a" => RunOrigin.A2A,
        "background" => RunOrigin.Background,
        "unknown" => RunOrigin.Unknown,
        _ => null,
    };

    /// <summary>Parses a value written by <see cref="CompletionEvalTriggers.LogName(UserRequestKind)"/>; null when blank or unknown.</summary>
    public static UserRequestKind? ParseUserAskedFor(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "instruction" => UserRequestKind.Instruction,
        "information-only" => UserRequestKind.InformationOnly,
        _ => null,
    };

    /// <summary>
    /// The scope a synthesis turn relaying <paramref name="results"/> runs under: the scope each
    /// subagent ran under, folded back into the primary's user turn. Null when no result carries
    /// one (results from an older build) — the run then keeps the pre-#685 behaviour. When
    /// siblings disagree, an instruction wins.
    /// </summary>
    public static ActionGateScope? FromRelayedResults(IReadOnlyList<SubagentResultMessage> results)
    {
        var scopes = results
            .Select(r => (Origin: ParseOrigin(r.RunOrigin), AskedFor: ParseUserAskedFor(r.UserAskedFor)))
            .Where(s => s.Origin is not null)
            .ToList();
        if (scopes.Count == 0) return null;

        var userTurn = scopes.Where(s => s.Origin is RunOrigin.UserTurn or RunOrigin.SubagentOfUserTurn).ToList();
        if (userTurn.Count < scopes.Count)
        {
            // At least one sibling came from configured automation; it keeps acting as before.
            var other = scopes.First(s => s.Origin is not (RunOrigin.UserTurn or RunOrigin.SubagentOfUserTurn));
            return new ActionGateScope(other.Origin!.Value);
        }

        var askedFor = userTurn.Any(s => s.AskedFor != UserRequestKind.InformationOnly)
            ? UserRequestKind.Instruction
            : UserRequestKind.InformationOnly;
        return new ActionGateScope(RunOrigin.UserTurn, askedFor);
    }
}

/// <summary>Configuration of the consequential-action gate (#685), under <c>AgentHost:ConsequentialActionGate</c>.</summary>
public sealed class ConsequentialActionGateOptions
{
    /// <summary>
    /// When false, the gate never refuses a call (env: <c>AgentHost__ConsequentialActionGate__Enabled</c>).
    /// Default true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Native (non-MCP) tool names that change systems outside the agent and should be gated like
    /// MCP writes. Empty by default: every native tool in this repo writes only to the agent's own
    /// state (files on its volume, memory, skills, rules, task list).
    /// </summary>
    public List<string> ExternalTools { get; set; } = [];
}

/// <summary>
/// Which tool calls are consequential actions (#685): calls that change something outside the
/// agent's own state, which an information-only user message must not cause.
/// </summary>
/// <remarks>
/// A call is consequential when <see cref="ToolSideEffects.IsSideEffecting(string, string?)"/>
/// says it writes AND either
/// <list type="bullet">
///   <item>it reaches an MCP server (<c>mcp_invoke_tool</c> or a typed <c>{server}__{tool}</c>
///   wrapper) — calendar, mail, todo, file-share and every other MCP server live outside the agent;</item>
///   <item>it creates or cancels one of the agent's scheduled tasks — automation that would later
///   run with <see cref="RunOrigin.Scheduled"/> and act without a request; or</item>
///   <item>it is listed in <see cref="ConsequentialActionGateOptions.ExternalTools"/>.</item>
/// </list>
/// Everything else is agent-local and always allowed: <c>file_*</c> on the agent's volume
/// (drafts/), working and long-term memory, the task list, skills, rules, progress reports,
/// scripts in the sandbox, and delegation itself (<c>spawn_subagent</c>, <c>spawn_wisps</c>,
/// <c>spawn_workers</c>, <c>invoke_agent</c>) — the gate applies to what the delegate does.
/// </remarks>
public static partial class ConsequentialActions
{
    /// <summary>Tools that create or cancel the agent's own scheduled automation.</summary>
    private static readonly HashSet<string> AutomationTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "schedule_task", "cancel_scheduled_task",
    };

    private const string TypedToolSeparator = "__";

    /// <summary>True when the call goes to an MCP server.</summary>
    public static bool IsMcpCall(string toolName) =>
        string.Equals(toolName, ToolSideEffects.McpInvokeToolName, StringComparison.OrdinalIgnoreCase)
        || toolName.Contains(TypedToolSeparator, StringComparison.Ordinal);

    /// <summary>True when the call creates or cancels the agent's own scheduled automation.</summary>
    public static bool IsAutomationChange(string toolName) => AutomationTools.Contains(toolName);

    /// <summary>
    /// True when a call to <paramref name="toolName"/> with <paramref name="arguments"/> (a
    /// <c>key=value</c> summary or JSON) changes something outside the agent's own state.
    /// </summary>
    public static bool IsConsequential(string toolName, string? arguments,
        IReadOnlyCollection<string>? externalTools = null)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return false;
        if (!ToolSideEffects.IsSideEffecting(toolName, arguments)) return false;
        if (IsMcpCall(toolName) || IsAutomationChange(toolName)) return true;
        return externalTools is { Count: > 0 }
               && externalTools.Contains(toolName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True when <paramref name="call"/> succeeded and changed something outside the agent.</summary>
    public static bool IsSucceededConsequential(LoopToolCall call) =>
        call.Succeeded && IsConsequential(call.Name, call.Arguments);

    /// <summary>
    /// True when a relayed subagent call (<see cref="SubagentToolCallSummary"/>, whose name shows
    /// a generic proxy's target as <c>mcp_invoke_tool → create_event</c>) succeeded and changed
    /// something outside the agent.
    /// </summary>
    public static bool IsSucceededConsequential(SubagentToolCallSummary call)
    {
        if (!call.Succeeded || !call.ChangesState || string.IsNullOrWhiteSpace(call.Name)) return false;
        var name = call.Name.Trim();
        var arrow = name.IndexOf('→');
        var called = (arrow >= 0 ? name[..arrow] : name).Trim();
        return IsMcpCall(called) || IsAutomationChange(called);
    }

    /// <summary>
    /// The system a consequential call would change: the MCP server (the <c>server_name</c>
    /// argument, or the server part of a typed wrapper), "your scheduled tasks", or a generic phrase.
    /// </summary>
    public static string DescribeTarget(string toolName, string? arguments)
    {
        if (IsAutomationChange(toolName))
            return "the agent's scheduled tasks (automation that would later act without the user)";

        var sep = toolName.IndexOf(TypedToolSeparator, StringComparison.Ordinal);
        if (sep > 0)
            return toolName[..sep];

        if (string.Equals(toolName, ToolSideEffects.McpInvokeToolName, StringComparison.OrdinalIgnoreCase)
            && arguments is not null
            && ServerNameRegex().Match(arguments) is { Success: true } match)
            return match.Groups["name"].Value;

        return "an external system";
    }

    /// <summary>The name a consequential call is reported under: the generic proxy's target when it has one.</summary>
    public static string DisplayName(string toolName, string? arguments)
    {
        var effective = ToolSideEffects.EffectiveToolName(toolName, arguments);
        return string.Equals(toolName, ToolSideEffects.McpInvokeToolName, StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrEmpty(effective)
            ? $"{effective} (via {toolName})"
            : toolName;
    }

    /// <summary>The tool result a refused call returns instead of running (#685).</summary>
    public static string BuildRefusal(string toolName, string? arguments) =>
        $"Not run: {DisplayName(toolName, arguments)} would change {DescribeTarget(toolName, arguments)} " +
        "but the user did not ask for that. Propose it to the user in one sentence (what, when, where) " +
        "and wait for them to ask. Do not retry this call or route it through another tool.";

    /// <summary>True when <paramref name="result"/> is a refusal built by <see cref="BuildRefusal"/>.</summary>
    public static bool IsRefusal(string? result) =>
        result is not null && result.StartsWith("Not run: ", StringComparison.Ordinal)
                           && result.Contains("but the user did not ask for that", StringComparison.Ordinal);

    // Matches server_name=foo (native args summary) and "server_name":"foo" / "serverName":"foo" (JSON).
    [GeneratedRegex(@"server_?name""?\s*[:=]\s*""?(?<name>[A-Za-z0-9_.\-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ServerNameRegex();
}

/// <summary>
/// Decides whether a tool call may run (#685). Consulted by every tool dispatch site — the native
/// <see cref="RockBotFunctionInvokingChatClient"/>, the text-based loop in
/// <see cref="AgentLoopRunner"/>, and wisp direct steps — through <see cref="ActionGateContext.Check"/>.
/// </summary>
public interface IConsequentialActionGate
{
    /// <summary>
    /// Null when the call may run; otherwise the tool result to return instead of running it.
    /// </summary>
    /// <param name="toolName">The tool name as called.</param>
    /// <param name="arguments">A <c>key=value</c> summary or JSON of the arguments; may be null.</param>
    /// <param name="scope">The run's scope; null allows the call.</param>
    string? Check(string toolName, string? arguments, ActionGateScope? scope);
}

/// <summary>
/// The default <see cref="IConsequentialActionGate"/>: refuses a consequential call
/// (<see cref="ConsequentialActions.IsConsequential"/>) in a run whose originating user message
/// asked for nothing (<see cref="UserRequestKind.InformationOnly"/>). Runs that do not trace back
/// to a user message — scheduled tasks, patrol, A2A tasks, dream passes — are never refused.
/// </summary>
public sealed class ConsequentialActionGate(
    IOptions<AgentHostOptions> options,
    ILogger<ConsequentialActionGate>? logger = null) : IConsequentialActionGate
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <inheritdoc />
    public string? Check(string toolName, string? arguments, ActionGateScope? scope)
    {
        var gateOptions = options.Value.ConsequentialActionGate;
        if (!gateOptions.Enabled || scope is null || scope.AllowsExternalChanges)
            return null;
        if (!ConsequentialActions.IsConsequential(toolName, arguments, gateOptions.ExternalTools))
            return null;

        var display = ConsequentialActions.DisplayName(toolName, arguments);
        _logger.LogInformation(
            "Consequential action gated: {Tool} (originating request: {UserAskedFor}, origin={Origin})",
            display, CompletionEvalTriggers.LogName(scope.UserAskedFor ?? UserRequestKind.Instruction),
            ActionGateScope.LogName(scope.Origin));
        HostDiagnostics.ConsequentialActionGated.Add(1,
            new KeyValuePair<string, object?>("rockbot.tool.name", ToolSideEffects.EffectiveToolName(toolName, arguments)),
            new KeyValuePair<string, object?>("rockbot.action_gate.origin", ActionGateScope.LogName(scope.Origin)));

        return ConsequentialActions.BuildRefusal(toolName, arguments);
    }
}

/// <summary>
/// Ambient per-async-flow scope and gate of the running loop (#685). Set by
/// <see cref="AgentLoopRunner.RunAsync"/>; read at every tool dispatch site, which is how the
/// singleton function-invoking client and wisp executor reach per-run state. Wisps and workers run
/// inside their parent's tool call and inherit it; <c>SubagentManager</c> captures it at spawn.
/// </summary>
public static class ActionGateContext
{
    private sealed record State(ActionGateScope Scope, IConsequentialActionGate Gate);

    private static readonly AsyncLocal<State?> Current = new();

    /// <summary>The scope of the loop running on this async flow, or null outside a loop.</summary>
    public static ActionGateScope? Scope => Current.Value?.Scope;

    /// <summary>Binds <paramref name="scope"/> and <paramref name="gate"/> to the current async flow until disposed.</summary>
    public static IDisposable Set(ActionGateScope scope, IConsequentialActionGate gate)
    {
        var previous = Current.Value;
        Current.Value = new State(scope, gate);
        return new Restore(previous);
    }

    /// <summary>
    /// Null when the call may run (or no loop is running on this flow); otherwise the refusal to
    /// return as the tool result instead of running it.
    /// </summary>
    public static string? Check(string toolName, string? arguments) =>
        Current.Value is { } state ? state.Gate.Check(toolName, arguments, state.Scope) : null;

    private sealed class Restore(State? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
