using System.Text;
using System.Text.RegularExpressions;

namespace RockBot.Host;

/// <summary>Why the completion evaluator runs on a loop (#666). Logged as <c>trigger=…</c>.</summary>
public enum CompletionEvalTrigger
{
    /// <summary>No trigger: the evaluator is skipped.</summary>
    None,
    /// <summary>The loop relays a subagent's result to the user.</summary>
    SubagentSynthesis,
    /// <summary>The reply promises or describes work ("I should have…", "Let me…") and the loop made no tool calls.</summary>
    PromiseNoAction,
    /// <summary>The reply opens with a bare completion claim ("Done.", "Updated.").</summary>
    BareClaim,
    /// <summary>The user gave an instruction and the loop made no tool calls.</summary>
    ImperativeNoTools,
    /// <summary>The loop made a side-effecting tool call (wrote, edited, uploaded, sent…).</summary>
    SideEffect,
    /// <summary>The pre-#666 gate: hallucinated-action or capability-denial pattern, a very short reply, or a loop that did not stop on its own.</summary>
    Pattern,
}

/// <summary>What the user message a reply answers asked for (#683). Shown to the evaluator as <c>UserAskedFor:</c>.</summary>
public enum UserRequestKind
{
    /// <summary>The user asked the agent to do something (or the request is unknown): the full rubric applies.</summary>
    Instruction,
    /// <summary>
    /// The user only shared context or information, or asked a question. The reply is judged on
    /// whether its facts and claims are supported and accurate, not on work the user's wider goal implies.
    /// </summary>
    InformationOnly,
}

/// <summary>
/// The work a subagent did, as the evaluator of the synthesis turn that relays it reads it (#683).
/// The synthesis loop itself only reads the subagent's saved output, so without this every honest
/// relay of a subagent's writes and uploads looked like an unsupported claim.
/// </summary>
/// <param name="TaskId">The subagent's task id.</param>
/// <param name="ToolCalls">The subagent's tool calls (oldest first, capped), or null when its result did not report them.</param>
/// <param name="TotalToolCalls">How many tool calls the subagent made in all, when known.</param>
/// <param name="Artifacts">Files and uploads the session work registry recorded for the subagent's session (and its wisps), one line each.</param>
public sealed record RelayedSubagentWork(
    string TaskId,
    IReadOnlyList<SubagentToolCallSummary>? ToolCalls,
    int? TotalToolCalls = null,
    IReadOnlyList<string>? Artifacts = null);

/// <summary>
/// Decides whether the completion evaluator runs on a finished loop, and builds what it reads
/// (#666). Before this the gate was <see cref="AgentLoopRunner.HallucinatedActionRegex"/> alone,
/// and the evaluator ran on 1 of 31 production loops while "Updated." claims, promises with no
/// tool call, and subagent results that missed the user's request went out unchecked.
/// </summary>
/// <remarks>
/// Every regex here is a cheap gate in front of a <see cref="Llm.ModelTier.Low"/> evaluator call,
/// not a verdict: a false positive costs one small LLM call, a false negative lets a broken reply
/// through. They are still kept tight enough that greetings, acknowledgements and plain
/// question-and-answer turns do not trigger.
/// </remarks>
public static class CompletionEvalTriggers
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant;

    // Apostrophe: ASCII or typographic.
    private const string Ap = @"['’]";

    /// <summary>
    /// A reply that promises, plans or apologises for work instead of doing it. Only meaningful
    /// when the loop made no tool calls — "I'll summarise below" after real work is fine.
    /// </summary>
    public static readonly Regex PromiseWithoutActionRegex = new(
        // "I should have done a web search…", "I should've checked"
        @"\bI\s+should(?:\s+have|" + Ap + @"ve)\s+(?!been\b)\w+" +
        // "I've now got the right path: search the spec…"
        @"|\bI(?:" + Ap + @"ve|\s+have)\s+(?:now\s+)?got\s+(?:the|a)\s+(?:right|clear|better|correct)\s+(?:path|approach|plan|idea|handle|direction)\b" +
        // "I'm going to…", "I'm about to…", "I'm on it", "On it."
        @"|\bI(?:" + Ap + @"m|\s+am)\s+(?:now\s+)?(?:going\s+to|about\s+to|on\s+it\b)" +
        @"|^\s*On\s+it\b" +
        // "I'm cutting it to 12 slides now", "I'm working on that now"
        @"|\bI(?:" + Ap + @"m|\s+am)\s+(?:now\s+)?(?:doing|checking|cutting|updating|working\s+on|searching|looking|pulling|fetching|running|trimming|rewriting|fixing|drafting|creating|reading|grabbing|getting|digging|starting|reworking|revising|editing|writing|preparing|building|generating|researching|verifying|making|reducing|shortening|redoing)\b[^.!?\n]{0,160}?\bnow\b" +
        // "I'll search the spec", "I will go ahead and update the doc"
        @"|\bI(?:\s+will|" + Ap + @"ll)\s+(?:(?:now|go\s+ahead\s+and|then|also|next|just|quickly|immediately|first)\s+)*" +
        @"(?:search|look|check|find|update|create|make|fix|cut|trim|write|rewrite|draft|send|upload|run|read|pull|fetch|get|research|dig|investigate|schedule|cancel|add|remove|delete|edit|revise|rework|build|generate|prepare|put|start|go|do|handle|review|verify|confirm|redo|reduce|shorten|try|work|follow\s+up|circle\s+back|report\s+back)\b" +
        // "Give me a moment", "Hang tight", "Stand by"
        @"|\b(?:give\s+me\s+(?:a\s+)?(?:moment|minute|sec(?:ond)?)|one\s+moment|hang\s+tight|stand\s+by)\b" +
        // A reply that ends on "Let me …" (but not "Let me know …")
        @"|(?:^|[.!?]\s+|\n\s*)Let\s+me\s+(?!know\b)[^.!?\n]*[.!?:…]*\s*$",
        Opts);

    /// <summary>
    /// A reply whose first sentence is a bare completion claim — "Done.", "Updated.", "Fixed it!".
    /// With no side-effecting tool call in the loop it is an unbacked claim; with one, it still
    /// needs checking against what was asked ("Updated." when the request was not met).
    /// </summary>
    public static readonly Regex BareCompletionClaimRegex = new(
        @"^[\s*_#>]*(?:All\s+|It" + Ap + @"s\s+|That" + Ap + @"s\s+)?" +
        @"(?:done|updated|fixed|uploaded|saved|sent|created|completed|finished|trimmed|cut|deleted|removed|scheduled|cancell?ed|added|edited|rewritten|revised|published|posted|applied|changed|submitted|shared|moved|renamed|replaced|written)" +
        @"(?:\s+(?:it|that|this|now))?[*_]*\s*(?:[.!:;,—–]|-\s|\r?\n|$)",
        Opts);

    // Verbs a user gives as an instruction. Shared by the sentence-start and "can you…" forms.
    private const string ActionVerbs =
        @"go\s+ahead|figure\s+out|make(?!\s+sense\b)|create|fix|update|trim|cut|upload|send|write|rewrite|edit|add|remove|delete|" +
        @"schedule|cancel|move|rename|change|build|generate|draft|search|find|look\s+(?:up|into|for)|check|research|try|run|" +
        @"put|set\s+up|reduce|shorten|revise|redo|rework|finish|publish|save|reply|forward|replace|convert|merge|split|" +
        @"rerun|retry|proceed|continue|implement|apply|prepare|compile|resend|share|archive|clean\s+up|get\s+(?:me|it|that|rid)|" +
        @"pull|fetch|grab|download|attach|insert|tighten|shrink|expand|extend|lengthen|condense|restructure|reorgani[sz]e|rearrange";

    /// <summary>
    /// A user message that gives an instruction — explicit ("do it", "so do it now", "go ahead",
    /// "figure out a way to update the doc", "can you trim it?") or implicit pushback that asks
    /// for a change ("why didn't you search?", "30 slides is a lot", "try again"). When the loop
    /// answering it made no tool calls, the reply is checked. Questions ("how do I fix…?",
    /// "do you know…?") and acknowledgements do not match.
    /// </summary>
    public static readonly Regex ImperativeInstructionRegex = new(
        // Sentence-start imperative, optionally after a softener: "so do it now", "ok, go ahead", "please trim it"
        @"(?:^|[.!?;:\n,—–]\s*|\s-\s+|\b(?:so|then|ok(?:ay)?|alright|please)[,!.]?\s+)" +
        @"(?:(?:please|just|now|so|then|ok(?:ay)?|go\s+ahead\s+and)[,]?\s+)*" +
        @"(?:do(?!\s+(?:you|i|we|they|u|people|not|n" + Ap + @"t|y" + Ap + @"all)\b)(?=\s+(?:it|that|this|so|them|the|a|an|what|as|one|both|all|some|those|these|now)\b|\s*[.!]*\s*$)" +
        @"|(?:" + ActionVerbs + @")\b(?![\-'’:]))" +
        // "can you trim it?", "could you please update the doc"
        @"|\b(?:can|could|would|will)\s+(?:you|u)\s+(?:please\s+)?(?:just\s+)?(?:do\b|(?:" + ActionVerbs + @")\b)" +
        @"|\bI\s+(?:need|want|" + Ap + @"d\s+like|would\s+like)\s+you\s+to\b" +
        // Implicit instruction: pushback on what was (not) done
        @"|\bwhy\s+(?:didn" + Ap + @"?t|did\s+not|haven" + Ap + @"?t|have\s+not|wouldn" + Ap + @"?t|aren" + Ap + @"?t|don" + Ap + @"?t)\s+you\b" +
        @"|\byou\s+(?:didn" + Ap + @"?t|did\s+not|forgot|missed|never|haven" + Ap + @"?t)\b" +
        @"|\b(?:is|are|that" + Ap + @"s|it" + Ap + @"s|seems?)\s+(?:a\s+lot|way\s+too\s+\w+|too\s+(?:many|much|long|short|big|small|few|wordy|dense))\b" +
        @"|\btry\s+again\b",
        Opts);

    /// <summary>
    /// A whole user message that is only a greeting or acknowledgement ("thanks", "ok", "hi").
    /// A short reply to one is expected, so it does not trip the short-reply gate.
    /// </summary>
    public static readonly Regex TrivialChatRegex = new(
        @"^\s*(?:(?:ok(?:ay)?|k|thanks?|thank\s+you|thx|ty|tysm|cheers|cool|great|nice|awesome|perfect|excellent|wonderful|" +
        @"got\s+it|sounds\s+good|makes\s+sense|hi|hello|hey|yo|good\s+(?:morning|afternoon|evening|night)|bye|goodbye|" +
        @"see\s+you|later|lol|haha|ha|np|no\s+problem|no\s+worries|you\s+too|so\s+much|a\s+lot|again|very\s+much|there|" +
        @"you|all|everyone|folks)[\s!.,:;)(\-]*|[\p{So}\p{Cs}️‍]+\s*)+$",
        Opts);

    /// <summary>
    /// A user message that opens by agreeing to what the agent proposed — "yes", "sure", "please",
    /// "sounds good". After an agent message it is an instruction ("want me to trim it?" — "yes").
    /// </summary>
    public static readonly Regex AffirmationRegex = new(
        @"^[\s*_]*(?:yes|yeah|yea|yep|yup|sure|ok(?:ay)?|absolutely|definitely|of\s+course|please|go|" +
        @"sounds\s+good|let" + Ap + @"?s\s+(?:do\s+it|go)|do\s+it|perfect,?\s+(?:do|go))\b",
        Opts);

    /// <summary>True when <paramref name="reply"/> promises or describes work instead of doing it.</summary>
    public static bool IsPromiseWithoutAction(string? reply) =>
        !string.IsNullOrWhiteSpace(reply) && PromiseWithoutActionRegex.IsMatch(reply);

    /// <summary>True when the first sentence of <paramref name="reply"/> is a bare completion claim.</summary>
    public static bool IsBareCompletionClaim(string? reply) =>
        !string.IsNullOrWhiteSpace(reply) && BareCompletionClaimRegex.IsMatch(reply);

    /// <summary>True when <paramref name="userMessage"/> gives the agent an instruction.</summary>
    public static bool IsImperativeInstruction(string? userMessage) =>
        !string.IsNullOrWhiteSpace(userMessage) && ImperativeInstructionRegex.IsMatch(userMessage);

    /// <summary>True when <paramref name="userMessage"/> is only a greeting or acknowledgement.</summary>
    public static bool IsTrivialChat(string? userMessage) =>
        !string.IsNullOrWhiteSpace(userMessage) && TrivialChatRegex.IsMatch(userMessage);

    /// <summary>
    /// Whether <paramref name="request"/> asked the agent to do something (#683). A message that
    /// only shares context ("the talk doesn't exist yet") or asks a question is
    /// <see cref="UserRequestKind.InformationOnly"/>: the evaluator must not mark the reply
    /// INCOMPLETE because the user's wider goal implies work nobody asked for. Reuses the #666
    /// <see cref="ImperativeInstructionRegex"/>. An unknown request (null/blank) is treated as an
    /// instruction, which keeps the full rubric.
    /// </summary>
    /// <param name="request">The user message the reply answers.</param>
    /// <param name="followsAgentMessage">True when the agent spoke just before it, so a bare "yes"
    /// accepts what the agent proposed and counts as an instruction.</param>
    public static UserRequestKind ClassifyUserRequest(string? request, bool followsAgentMessage)
    {
        if (string.IsNullOrWhiteSpace(request) || IsImperativeInstruction(request))
            return UserRequestKind.Instruction;
        if (followsAgentMessage && AffirmationRegex.IsMatch(request))
            return UserRequestKind.Instruction;
        return UserRequestKind.InformationOnly;
    }

    /// <summary>The <c>UserAskedFor:</c> value shown to the evaluator and tagged on the counters.</summary>
    public static string LogName(UserRequestKind kind) => kind switch
    {
        UserRequestKind.InformationOnly => "information-only",
        _ => "instruction",
    };

    /// <summary>The <c>trigger=</c> value logged for <paramref name="trigger"/>.</summary>
    public static string LogName(CompletionEvalTrigger trigger) => trigger switch
    {
        CompletionEvalTrigger.SubagentSynthesis => "subagent-synthesis",
        CompletionEvalTrigger.PromiseNoAction => "promise-no-action",
        CompletionEvalTrigger.BareClaim => "bare-claim",
        CompletionEvalTrigger.ImperativeNoTools => "imperative-no-tools",
        CompletionEvalTrigger.SideEffect => "side-effect",
        CompletionEvalTrigger.Pattern => "pattern",
        _ => "none",
    };

    /// <summary>
    /// True when a reply produced with no tool calls should go through the full loop so the
    /// evaluator can see it — used by the text-based path's first-response routing, which would
    /// otherwise publish the reply without ever entering <see cref="AgentLoopRunner.RunAsync"/>.
    /// </summary>
    public static bool NeedsCheckWithoutTools(string? reply, string? userMessage) =>
        IsPromiseWithoutAction(reply) || IsBareCompletionClaim(reply) || IsImperativeInstruction(userMessage);

    /// <summary>
    /// The tool calls that count as the agent doing something: everything except its own
    /// bookkeeping (task list, memory, progress reports).
    /// </summary>
    public static IReadOnlyList<LoopToolCall> SubstantiveCalls(IReadOnlyList<LoopToolCall> calls) =>
        calls.Where(c => !ToolSideEffects.IsBookkeeping(ToolSideEffects.EffectiveToolName(c.Name, c.Arguments))).ToList();

    /// <summary>
    /// Picks the trigger for a finished loop, or <see cref="CompletionEvalTrigger.None"/> to skip
    /// the evaluator. Precedence (first match wins, for the log): subagent synthesis, promise
    /// without action, bare claim, instruction with no tools, side effect, then the pre-#666
    /// pattern gate.
    /// </summary>
    /// <param name="response">The loop's final reply.</param>
    /// <param name="latestUserMessage">The user message the loop answers.</param>
    /// <param name="toolCalls">Every tool call the loop made.</param>
    /// <param name="subagentSynthesis">True when the loop relays a subagent's result.</param>
    /// <param name="modelStopped">False when the loop ended on its iteration cap rather than on its own.</param>
    public static CompletionEvalTrigger Decide(
        string response,
        string latestUserMessage,
        IReadOnlyList<LoopToolCall> toolCalls,
        bool subagentSynthesis,
        bool modelStopped = true)
    {
        if (subagentSynthesis)
            return CompletionEvalTrigger.SubagentSynthesis;

        var noTools = SubstantiveCalls(toolCalls).Count == 0;

        if (noTools && IsPromiseWithoutAction(response))
            return CompletionEvalTrigger.PromiseNoAction;

        if (IsBareCompletionClaim(response))
            return CompletionEvalTrigger.BareClaim;

        if (noTools && IsImperativeInstruction(latestUserMessage))
            return CompletionEvalTrigger.ImperativeNoTools;

        if (toolCalls.Any(ToolSideEffects.IsSideEffecting))
            return CompletionEvalTrigger.SideEffect;

        // Pre-#666 gate. A short reply to "thanks" or "hi" is expected, so it is exempt.
        var shortReply = response.Length < 20 && !(noTools && IsTrivialChat(latestUserMessage));
        if (!modelStopped
            || shortReply
            || AgentLoopRunner.HallucinatedActionRegex.IsMatch(response)
            || AgentLoopRunner.CapabilityDenialRegex.IsMatch(response))
            return CompletionEvalTrigger.Pattern;

        return CompletionEvalTrigger.None;
    }

    // ── Evaluator input ─────────────────────────────────────────────────────

    /// <summary>How many of the most recent user messages the evaluator reads.</summary>
    public const int RecentUserMessageCount = 3;

    /// <summary>How many of its tool calls a subagent's result carries (#683).</summary>
    public const int MaxRelayedToolCalls = 40;

    /// <summary>The longest argument summary a relayed tool call keeps (#683).</summary>
    public const int MaxRelayedArgumentChars = 100;

    /// <summary>Character budget shared by all "Tool calls made by subagent" sections of one evaluator request (#683).</summary>
    public const int RelayedWorkBudgetChars = 3000;

    /// <summary>
    /// Opens every completion re-prompt (#683). The re-prompt goes in as a user-role message, the
    /// only role every provider path accepts at the end of the history, and without the marker the
    /// model answered it as if the user had said it ("You're right…").
    /// </summary>
    public const string InternalRepromptMarker = "[Internal completion check — not a message from the user]";

    private const int MinRelayedWorkBudgetPerSubagent = 500;
    private const int MaxRelayedArtifactsListed = 10;
    private const int MaxRelayedArgumentCharsShown = 80;
    private const int MaxUserMessageChars = 2000;
    private const int MaxPreviousAgentChars = 800;
    private const int MaxResponseChars = 8000;
    private const int MaxToolCallsListed = 40;
    private const int MaxRequestCharsInReprompt = 300;

    /// <summary>What the evaluator is told when the user's message gave no instruction (#683).</summary>
    public const string InformationOnlyRubric =
        "UserAskedFor: information-only — the message being answered gives no instruction: it shares " +
        "context or information, or asks a question. Skip questions 1–2, except that a question the user " +
        "asked must be answered. Judge the reply on questions 3–5 only: are its facts, numbers and claims " +
        "supported by the tool calls and accurately stated? Do NOT mark it INCOMPLETE because the user's " +
        "wider goal implies more work (building, writing, researching, fixing) that this message did not ask for.";

    /// <summary>What the evaluator is told when the user gave an instruction (#683).</summary>
    public const string InstructionRubric =
        "UserAskedFor: instruction — the user asked the agent to do something; apply every question of the rubric.";

    /// <summary>What the completion evaluator reads about a finished loop.</summary>
    /// <param name="RecentUserMessages">Up to <see cref="RecentUserMessageCount"/> user messages, oldest first; the last is the one the loop answers.</param>
    /// <param name="PreviousAgentMessage">The agent's message just before the latest user message, for resolving "do it" / "that".</param>
    /// <param name="OriginatingUserRequest">For a subagent synthesis, the user request that led to the spawn.</param>
    /// <param name="AgentResponse">The loop's final reply.</param>
    /// <param name="ToolCalls">Every tool call the loop made.</param>
    /// <param name="Trigger">Why the evaluator is running.</param>
    /// <param name="RelayedWork">For a subagent synthesis, the work of each subagent whose result is relayed (#683).</param>
    /// <param name="UserAskedFor">Whether the request being judged gave an instruction (#683).</param>
    public sealed record EvaluatorInput(
        IReadOnlyList<string> RecentUserMessages,
        string? PreviousAgentMessage,
        string? OriginatingUserRequest,
        string AgentResponse,
        IReadOnlyList<LoopToolCall> ToolCalls,
        CompletionEvalTrigger Trigger,
        IReadOnlyList<RelayedSubagentWork>? RelayedWork = null,
        UserRequestKind UserAskedFor = UserRequestKind.Instruction);

    /// <summary>Renders <paramref name="input"/> as the evaluator's user message.</summary>
    public static string BuildEvaluatorUserMessage(EvaluatorInput input)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(input.OriginatingUserRequest))
        {
            sb.AppendLine("## Original user request (the request this subagent result must satisfy)");
            sb.AppendLine(Truncate(input.OriginatingUserRequest!, MaxUserMessageChars));
            sb.AppendLine();
        }

        sb.AppendLine("## Recent user messages (oldest first; the last one is what the agent is answering)");
        if (input.RecentUserMessages.Count == 0)
        {
            sb.AppendLine("(none)");
        }
        else
        {
            for (var i = 0; i < input.RecentUserMessages.Count; i++)
                sb.AppendLine($"{i + 1}. {Truncate(input.RecentUserMessages[i], MaxUserMessageChars)}");
        }
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(input.PreviousAgentMessage))
        {
            sb.AppendLine("## Agent's previous message (context for the latest user message)");
            sb.AppendLine(Truncate(input.PreviousAgentMessage!, MaxPreviousAgentChars));
            sb.AppendLine();
        }

        sb.AppendLine("## What the user asked for");
        sb.AppendLine(input.UserAskedFor == UserRequestKind.InformationOnly ? InformationOnlyRubric : InstructionRubric);
        sb.AppendLine();

        var relayed = input.Trigger == CompletionEvalTrigger.SubagentSynthesis && input.RelayedWork is { Count: > 0 }
            ? input.RelayedWork
            : null;

        sb.AppendLine(relayed is null
            ? "## Tool calls the agent made this turn"
            : "## Tool calls the agent made this turn (its own calls — judged separately from the relayed work)");
        sb.AppendLine(FormatToolCalls(input.ToolCalls));
        sb.AppendLine();

        if (relayed is not null)
        {
            var perSubagent = Math.Max(MinRelayedWorkBudgetPerSubagent, RelayedWorkBudgetChars / relayed.Count);
            foreach (var work in relayed)
            {
                sb.AppendLine(FormatRelayedWork(work, perSubagent));
                sb.AppendLine();
            }
        }

        sb.AppendLine("## Why this reply is being checked");
        sb.AppendLine(DescribeTrigger(input.Trigger, input.ToolCalls, relayed is not null));
        sb.AppendLine();

        sb.AppendLine("## Agent response");
        sb.Append(Truncate(input.AgentResponse, MaxResponseChars));

        return sb.ToString();
    }

    /// <summary>
    /// A compact list of <paramref name="calls"/>: one line each with name, outcome, and whether
    /// it changed anything. Generic proxies show their target, e.g. <c>mcp_invoke_tool → send_mail</c>.
    /// </summary>
    public static string FormatToolCalls(IReadOnlyList<LoopToolCall> calls)
    {
        if (calls.Count == 0)
            return "(none — the agent made no tool calls this turn)";

        var sb = new StringBuilder();
        foreach (var call in calls.Take(MaxToolCallsListed))
        {
            var outcome = call.Succeeded ? "ok" : "FAILED";
            var effect = ToolSideEffects.IsSideEffecting(call) ? ", changes state" : string.Empty;
            sb.AppendLine($"- {DisplayName(call)} ({outcome}{effect})");
        }
        if (calls.Count > MaxToolCallsListed)
            sb.AppendLine($"- … and {calls.Count - MaxToolCallsListed} more");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The compact form of a subagent's tool calls carried on its result (#683): at most
    /// <paramref name="max"/> calls, oldest first, each with its outcome, whether it changed state,
    /// and a short argument summary. When there are more, failed and state-changing calls are kept
    /// first, then other calls, then the subagent's own bookkeeping — newest first within each.
    /// </summary>
    public static IReadOnlyList<SubagentToolCallSummary> SummarizeForRelay(
        IReadOnlyList<LoopToolCall> calls, int max = MaxRelayedToolCalls)
    {
        if (max <= 0 || calls.Count == 0) return [];

        var ranked = calls.Select((call, index) => (Index: index, Call: call, Rank: EvidenceRank(call))).ToList();
        if (ranked.Count > max)
        {
            ranked = ranked
                .OrderBy(r => r.Rank).ThenByDescending(r => r.Index)
                .Take(max)
                .OrderBy(r => r.Index)
                .ToList();
        }

        return ranked
            .Select(r => new SubagentToolCallSummary(
                DisplayName(r.Call),
                r.Call.Succeeded,
                ToolSideEffects.IsSideEffecting(r.Call),
                CompactArguments(r.Call.Arguments, MaxRelayedArgumentChars)))
            .ToList();
    }

    /// <summary>
    /// The "Tool calls made by subagent …" section for one relayed result (#683), within
    /// <paramref name="budgetChars"/> characters of call lines. When the calls do not fit, failed
    /// and state-changing ones are kept first; the section says how many were left out.
    /// </summary>
    public static string FormatRelayedWork(RelayedSubagentWork work, int budgetChars = RelayedWorkBudgetChars)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Tool calls made by subagent {work.TaskId} (the relayed work)");

        if (work.ToolCalls is null)
        {
            sb.AppendLine(
                "(not reported — this subagent's result does not carry its tool calls. Do not treat the " +
                "absence of writes or uploads in the agent's own tool calls as proof the relayed work did not happen.)");
        }
        else if (work.ToolCalls.Count == 0)
        {
            sb.AppendLine("(none — the subagent made no tool calls)");
        }
        else
        {
            var lines = work.ToolCalls
                .Select((call, index) => (Index: index, Rank: EvidenceRank(call), Text: FormatRelayedCall(call)))
                .ToList();

            var chosen = lines;
            if (lines.Sum(l => l.Text.Length + 1) > budgetChars)
            {
                chosen = [];
                var used = 0;
                foreach (var line in lines.OrderBy(l => l.Rank).ThenByDescending(l => l.Index))
                {
                    if (used + line.Text.Length + 1 > budgetChars) continue;
                    chosen.Add(line);
                    used += line.Text.Length + 1;
                }
                chosen = chosen.OrderBy(l => l.Index).ToList();
            }

            var total = Math.Max(work.TotalToolCalls ?? work.ToolCalls.Count, work.ToolCalls.Count);
            if (chosen.Count < total)
                sb.AppendLine($"({total} calls in all; {chosen.Count} shown, state-changing and failed calls first)");
            foreach (var line in chosen)
                sb.AppendLine(line.Text);
        }

        if (work.Artifacts is { Count: > 0 } artifacts)
        {
            sb.AppendLine("Files and uploads the session recorded for this subagent (including its wisps):");
            foreach (var artifact in artifacts.Take(MaxRelayedArtifactsListed))
                sb.AppendLine($"- {artifact}");
            if (artifacts.Count > MaxRelayedArtifactsListed)
                sb.AppendLine($"- … and {artifacts.Count - MaxRelayedArtifactsListed} more");
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatRelayedCall(SubagentToolCallSummary call)
    {
        var outcome = call.Succeeded ? "ok" : "FAILED";
        var effect = call.ChangesState ? ", changes state" : string.Empty;
        var args = CompactArguments(call.Arguments, MaxRelayedArgumentCharsShown);
        return args is null
            ? $"- {call.Name} ({outcome}{effect})"
            : $"- {call.Name} ({outcome}{effect}): {args}";
    }

    // 0: failed or state-changing (the evidence a relayed claim stands on), 1: other calls,
    // 2: the agent's own bookkeeping.
    private static int EvidenceRank(LoopToolCall call) =>
        !call.Succeeded || ToolSideEffects.IsSideEffecting(call) ? 0
        : ToolSideEffects.IsBookkeeping(ToolSideEffects.EffectiveToolName(call.Name, call.Arguments)) ? 2
        : 1;

    private static int EvidenceRank(SubagentToolCallSummary call) =>
        !call.Succeeded || call.ChangesState ? 0
        : ToolSideEffects.IsBookkeeping(call.Name) ? 2
        : 1;

    private static string DisplayName(LoopToolCall call)
    {
        var effective = ToolSideEffects.EffectiveToolName(call.Name, call.Arguments);
        return string.Equals(effective, call.Name, StringComparison.Ordinal) || string.IsNullOrEmpty(effective)
            ? call.Name
            : $"{call.Name} → {effective}";
    }

    private static string? CompactArguments(string? arguments, int max)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return null;
        var flat = string.Join(' ', arguments.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return flat.Length == 0 ? null : Truncate(flat, max);
    }

    private static string DescribeTrigger(CompletionEvalTrigger trigger, IReadOnlyList<LoopToolCall> calls, bool hasRelayedWork = false) => trigger switch
    {
        CompletionEvalTrigger.SubagentSynthesis when hasRelayedWork =>
            "The agent is relaying a subagent's result. Judge it against the user's original request, " +
            "not against the subagent's own task description or self-report. The relayed work was done by " +
            "the subagent in its own session: check claims about it (files written, uploads, read-backs) " +
            "against the subagent's tool calls above, and the agent's own tool calls only for what the " +
            "agent says it did itself this turn.",
        CompletionEvalTrigger.SubagentSynthesis =>
            "The agent is relaying a subagent's result. Judge it against the user's original request, " +
            "not against the subagent's own task description or self-report.",
        CompletionEvalTrigger.PromiseNoAction =>
            "The reply promises or describes work, and the agent made no tool calls this turn.",
        CompletionEvalTrigger.BareClaim when !calls.Any(ToolSideEffects.IsSideEffecting) =>
            "The reply opens with a completion claim, but no tool call this turn changed anything.",
        CompletionEvalTrigger.BareClaim =>
            "The reply opens with a completion claim. Check that what changed is what the user asked for.",
        CompletionEvalTrigger.ImperativeNoTools =>
            "The user gave an instruction, and the agent made no tool calls this turn.",
        CompletionEvalTrigger.SideEffect =>
            "The agent changed something (a file, document, message or record). Check that the change matches the request.",
        _ => "Routine check.",
    };

    /// <summary>
    /// The extra instruction added to the re-prompt after an INCOMPLETE verdict, specific to why
    /// the reply was checked. Empty when the generic continuation is enough.
    /// </summary>
    public static string RepromptGuidance(
        CompletionEvalTrigger trigger, IReadOnlyList<LoopToolCall> calls,
        UserRequestKind userAskedFor = UserRequestKind.Instruction) => trigger switch
    {
        CompletionEvalTrigger.PromiseNoAction when userAskedFor == UserRequestKind.InformationOnly =>
            " Your reply promised or described work instead of doing it. If answering the user's message " +
            "needs that work, do it now with your tools; otherwise answer without promising work the user " +
            "did not ask for.",
        CompletionEvalTrigger.BareClaim when userAskedFor == UserRequestKind.InformationOnly
                                             && !calls.Any(ToolSideEffects.IsSideEffecting) =>
            " You said something was done, but no tool call this turn changed anything. Do not claim " +
            "changes that were not made.",
        CompletionEvalTrigger.PromiseNoAction =>
            " Your reply promised or described work instead of doing it. Do that work now with your tools — " +
            "do not describe what you will do and do not apologise for not having done it.",
        CompletionEvalTrigger.BareClaim when !calls.Any(ToolSideEffects.IsSideEffecting) =>
            " You said it was done, but no tool call this turn changed anything. Make the change now with " +
            "your tools, then report what actually changed.",
        CompletionEvalTrigger.ImperativeNoTools =>
            " The user gave an instruction and you made no tool calls. Carry it out now with your tools.",
        CompletionEvalTrigger.SubagentSynthesis when userAskedFor == UserRequestKind.InformationOnly =>
            " The user's message gave no instruction. Correct any relayed fact or claim the review flagged, " +
            "and do not start work the user did not ask for. Work the subagent's own tool calls show it did " +
            "is real — do not disown it.",
        CompletionEvalTrigger.SubagentSynthesis =>
            " Compare the subagent's result with what the user originally asked for. If it falls short, do the " +
            "remaining work or say plainly what is missing — do not present it as meeting the request. Work the " +
            "subagent's own tool calls show it did is real — do not disown it.",
        _ => string.Empty,
    };

    /// <summary>
    /// The re-prompt injected after an INCOMPLETE verdict (#683). It is marked as internal and tells
    /// the model to answer the user's original message directly: an unmarked "Not complete because…"
    /// in the user role read as user feedback, and the model replied "You're right…" to a user who
    /// had not spoken, and disowned real work.
    /// </summary>
    /// <param name="reason">The evaluator's reason.</param>
    /// <param name="instructions">What to do about it (the generic continuation plus <see cref="RepromptGuidance"/>).</param>
    /// <param name="userRequest">The user message the reply must answer, quoted when known.</param>
    public static string BuildRepromptMessage(string? reason, string instructions, string? userRequest)
    {
        var sb = new StringBuilder();
        sb.Append(InternalRepromptMarker).Append(' ');
        sb.Append("An automated review of your draft reply found it incomplete");
        var trimmedReason = reason?.Trim().TrimEnd('.');
        sb.Append(string.IsNullOrEmpty(trimmedReason) ? "." : $": {trimmedReason}.");
        if (!string.IsNullOrWhiteSpace(instructions))
            sb.Append(' ').Append(instructions.Trim());
        sb.Append(" The user has not said anything new and has not seen the draft. Reply directly to the user's original message");
        if (CompactArguments(userRequest, MaxRequestCharsInReprompt) is { } quoted)
            sb.Append(" (\"").Append(quoted).Append("\")");
        sb.Append(", as the complete reply they will read. Do not address or thank the user for feedback, " +
                  "do not say \"you're right\" or \"you were right\", do not apologise for or discuss the draft, " +
                  "and do not mention this review.");
        return sb.ToString();
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
