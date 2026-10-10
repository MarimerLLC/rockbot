using RockBot.Host;

namespace RockBot.Host.Tests;

/// <summary>The name-verb side-effect classification behind the evaluator's <c>side-effect</c> trigger (#666).</summary>
[TestClass]
public class ToolSideEffectsTests
{
    [TestMethod]
    [DataRow("file_write", null)]
    [DataRow("file_edit", null)]
    [DataRow("file_delete", null)]
    [DataRow("attach_image", null)]
    [DataRow("schedule_task", null)]
    [DataRow("cancel_scheduled_task", null)]
    [DataRow("send_email", null)]
    [DataRow("execute_python_script", null)]
    [DataRow("sharepoint__sharepoint_upload_file", null)]       // typed MCP wrapper
    [DataRow("calendar-mcp__createEvent", null)]               // camelCase tool part
    [DataRow("mcp_invoke_tool", "server_name=mail, tool_name=send_mail, arguments={}")]
    [DataRow("mcp_invoke_tool", "{\"server_name\":\"slides\",\"tool_name\":\"update_presentation\"}")]
    public void SideEffecting(string name, string? args)
    {
        Assert.IsTrue(ToolSideEffects.IsSideEffecting(name, args), $"{name} {args}");
    }

    [TestMethod]
    [DataRow("file_read", null)]
    [DataRow("file_list", null)]
    [DataRow("file_get_path", null)]
    [DataRow("web_search", null)]
    [DataRow("web_browse", null)]
    [DataRow("get_file_base64", null)]
    [DataRow("list_completed", null)]
    [DataRow("search_memory", null)]
    [DataRow("mcp_list_services", null)]
    [DataRow("mcp_get_service_details", null)]
    [DataRow("calendar__list_events", null)]
    [DataRow("mcp_invoke_tool", "server_name=calendar, tool_name=list_events")]
    [DataRow("mcp_invoke_tool", null)]                          // no target: unknown, not counted
    // The agent's own bookkeeping is never a side effect.
    [DataRow("task_create", null)]
    [DataRow("task_update", null)]
    [DataRow("save_memory", null)]
    [DataRow("save_to_working_memory", null)]
    [DataRow("report_progress", null)]
    // Delegation is handled separately (the spawning loop is skipped).
    [DataRow("spawn_subagent", null)]
    [DataRow("invoke_agent", null)]
    public void NotSideEffecting(string name, string? args)
    {
        Assert.IsFalse(ToolSideEffects.IsSideEffecting(name, args), $"{name} {args}");
    }

    [TestMethod]
    public void EffectiveToolName_UnwrapsProxiesAndTypedNames()
    {
        Assert.AreEqual("send_mail", ToolSideEffects.EffectiveToolName("mcp_invoke_tool", "server_name=m, tool_name=send_mail"));
        Assert.AreEqual("create_event", ToolSideEffects.EffectiveToolName("calendar__create_event", null));
        Assert.AreEqual("file_write", ToolSideEffects.EffectiveToolName("file_write", "path=x"));
    }
}
