using RockBot.Agent.McpBridge;

namespace RockBot.Agent.Tests.McpBridge;

/// <summary>
/// Tests for the config change-detection seam used by the MCP bridge's hot-reload
/// poll fallback (issue #470). The poll loop and FileSystemWatcher wiring are verified
/// end-to-end on-cluster; these cover the cheap, deterministic units.
/// </summary>
[TestClass]
public class ConfigReloadTests
{
    // ── ReadConfigStamp ───────────────────────────────────────────────────────

    [TestMethod]
    public void ReadConfigStamp_MissingFile_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcp-missing-{Guid.NewGuid():N}.json");
        Assert.IsNull(McpBridgeService.ReadConfigStamp(path));
    }

    [TestMethod]
    public void ReadConfigStamp_ExistingFile_ReturnsStampWithLength()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{}");
            var stamp = McpBridgeService.ReadConfigStamp(path);

            Assert.IsNotNull(stamp);
            Assert.AreEqual(2, stamp.Value.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void ReadConfigStamp_AfterContentChange_StampDiffers()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{}");
            var before = McpBridgeService.ReadConfigStamp(path);

            // Changing length guarantees a different stamp regardless of filesystem
            // mtime resolution.
            File.WriteAllText(path, "{ \"mcpServers\": {} }");
            var after = McpBridgeService.ReadConfigStamp(path);

            Assert.IsNotNull(before);
            Assert.IsNotNull(after);
            Assert.AreNotEqual(before.Value, after.Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── ConfigStamp equality ──────────────────────────────────────────────────

    [TestMethod]
    public void ConfigStamp_SameValues_AreEqual()
    {
        var when = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        Assert.AreEqual(
            new McpBridgeService.ConfigStamp(when, 42),
            new McpBridgeService.ConfigStamp(when, 42));
    }

    [TestMethod]
    public void ConfigStamp_DifferentLength_NotEqual()
    {
        var when = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        Assert.AreNotEqual(
            new McpBridgeService.ConfigStamp(when, 42),
            new McpBridgeService.ConfigStamp(when, 43));
    }

    [TestMethod]
    public void ConfigStamp_DifferentWriteTime_NotEqual()
    {
        Assert.AreNotEqual(
            new McpBridgeService.ConfigStamp(new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc), 42),
            new McpBridgeService.ConfigStamp(new DateTime(2026, 6, 15, 12, 0, 1, DateTimeKind.Utc), 42));
    }
}
