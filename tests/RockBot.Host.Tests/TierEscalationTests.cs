using System.Text.Json;
using Microsoft.Extensions.AI;
using RockBot.Host;

namespace RockBot.Host.Tests;

/// <summary>
/// Mid-turn tier escalation (#663): the side-effect classifier, the per-run escalation state,
/// and the chat client that redirects a native loop's remaining iterations.
/// </summary>
[TestClass]
public class TierEscalationTests
{
    // ── ToolSideEffects ──────────────────────────────────────────────────────

    [TestMethod]
    [DataRow("file_write")]
    [DataRow("file_edit")]
    [DataRow("file_delete")]
    [DataRow("sharepoint_upload_file")]
    [DataRow("onedrive__upload_file")]
    [DataRow("outlook_send_mail")]
    [DataRow("outlook_create_reply_draft")]
    [DataRow("update_task")]
    [DataRow("deleteEvent")]
    [DataRow("run_script")]
    [DataRow("save_skill")]
    public void SideEffecting_ToolNames(string name)
    {
        Assert.IsTrue(ToolSideEffects.IsSideEffectingName(name), $"{name} changes external state.");
    }

    [TestMethod]
    [DataRow("file_read")]
    [DataRow("file_list")]
    [DataRow("file_get_path")]
    [DataRow("web_search")]
    [DataRow("web_fetch")]
    [DataRow("get_run_status")]
    [DataRow("teams_list_channel_messages")]
    [DataRow("sharepoint_folder_search")]
    [DataRow("mcp_find_tools")]
    [DataRow("current_datetime")]
    // Agent bookkeeping is exempt even though its names carry write verbs.
    [DataRow("save_memory")]
    [DataRow("save_to_working_memory")]
    [DataRow("task_update")]
    [DataRow("spawn_subagent")]
    [DataRow("set_timezone")]
    public void ReadOnlyOrExempt_ToolNames(string name)
    {
        Assert.IsFalse(ToolSideEffects.IsSideEffectingName(name), $"{name} must not escalate a turn.");
    }

    [TestMethod]
    public void McpInvokeTool_ClassifiesTheInnerTool()
    {
        var upload = new Dictionary<string, object?>
        {
            ["server_name"] = "onedrive",
            ["tool_name"] = JsonSerializer.SerializeToElement("upload_file"),
        };
        var list = new Dictionary<string, object?> { ["server_name"] = "onedrive", ["tool_name"] = "list_files" };

        Assert.IsTrue(ToolSideEffects.IsSideEffecting("mcp_invoke_tool", upload, out var effective));
        Assert.AreEqual("upload_file", effective);
        Assert.IsFalse(ToolSideEffects.IsSideEffecting("mcp_invoke_tool", list, out _));
        Assert.IsFalse(ToolSideEffects.IsSideEffecting("mcp_invoke_tool", null, out _));
    }

    // ── TierEscalationContext.State ──────────────────────────────────────────

    [TestMethod]
    public void LowRun_SideEffectingCall_EscalatesToBalanced()
    {
        var diag = new LoopDiagnostics();
        var state = new TierEscalationContext.State(ModelTier.Low, diagnostics: diag);
        using var _ = TierEscalationContext.Set(state);

        Assert.AreEqual(ModelTier.Low, TierEscalationContext.EffectiveTier(ModelTier.Low));
        state.ObserveToolCall("file_read", null);
        Assert.IsNull(state.EscalatedTo, "A read does not escalate.");

        state.ObserveToolCall("file_write", new Dictionary<string, object?> { ["path"] = "deck.md" });

        Assert.AreEqual(ModelTier.Balanced, state.EscalatedTo);
        Assert.AreEqual(ModelTier.Balanced, TierEscalationContext.EffectiveTier(ModelTier.Low));
        StringAssert.Contains(state.Reason, "file_write");
        Assert.AreEqual(ModelTier.Balanced, diag.EscalatedTier);
        StringAssert.Contains(diag.EscalationReason, "file_write");
    }

    [TestMethod]
    public void LowRun_TwoToolErrors_Escalate()
    {
        var state = new TierEscalationContext.State(ModelTier.Low);

        state.ObserveToolResult("web_fetch", isError: false);
        state.ObserveToolResult("web_fetch", isError: true);
        Assert.IsNull(state.EscalatedTo, "One error is not enough.");

        state.ObserveToolResult("web_search", isError: true);
        Assert.AreEqual(ModelTier.Balanced, state.EscalatedTo);
        StringAssert.Contains(state.Reason, "2 tool errors");
    }

    [TestMethod]
    public void BalancedRun_NeverEscalates()
    {
        var state = new TierEscalationContext.State(ModelTier.Balanced);

        state.ObserveToolCall("file_write", null);
        state.ObserveToolResult("x", true);
        state.ObserveToolResult("x", true);

        Assert.IsNull(state.EscalatedTo);
    }

    [TestMethod]
    public void NoBoundState_IsANoOp()
    {
        using var _ = TierEscalationContext.Set(null);

        Assert.AreEqual(ModelTier.Low, TierEscalationContext.EffectiveTier(ModelTier.Low));
        Assert.IsFalse(TierEscalationContext.ShouldRedirect);
        using (TierEscalationContext.EnterPrimaryCall())
            Assert.IsFalse(TierEscalationContext.ShouldRedirect);
    }

    // ── TierEscalatingChatClient ─────────────────────────────────────────────

    private sealed class NamedClient(string name) : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, name)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [TestMethod]
    public async Task EscalatingClient_RedirectsOnlyEscalatedPrimaryCalls()
    {
        var low = new NamedClient("low");
        var balanced = new NamedClient("balanced");
        var client = new TierEscalatingChatClient(low, balanced);
        var state = new TierEscalationContext.State(ModelTier.Low);
        using var _ = TierEscalationContext.Set(state);

        using (TierEscalationContext.EnterPrimaryCall())
        {
            Assert.AreEqual("low", (await client.GetResponseAsync("hi")).Text, "Not escalated yet.");
            state.ObserveToolCall("file_write", null);
            Assert.AreEqual("balanced", (await client.GetResponseAsync("hi")).Text, "Escalated main-loop call.");
        }

        Assert.AreEqual("low", (await client.GetResponseAsync("hi")).Text,
            "An auxiliary Low call outside the main loop (e.g. the completion evaluator) keeps its tier.");
        Assert.AreEqual(2, low.Calls);
        Assert.AreEqual(1, balanced.Calls);
    }
}
