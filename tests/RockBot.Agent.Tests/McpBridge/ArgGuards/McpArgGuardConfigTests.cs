using System.Text.Json;
using RockBot.Agent.McpBridge;
using RockBot.Agent.McpBridge.ArgGuards;

namespace RockBot.Agent.Tests.McpBridge.ArgGuards;

[TestClass]
public class McpArgGuardConfigTests
{
    // Mirrors the bridge's JsonOptions (McpBridgeService) and PersistServerConfigAsync.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private const string FeatureSpecJson = """
        {
          "mcpServers": {
            "onedrive-personal": {
              "type": "sse",
              "url": "http://onedrive-personal:3001/",
              "argGuards": [
                { "handler": "path-prefix",
                  "tools": ["download_file"],
                  "options": {
                    "args": ["save_directory"],
                    "allowedPrefixes": ["/rockbot/shared"],
                    "requireArgs": true } }
              ]
            }
          }
        }
        """;

    [TestMethod]
    public void Deserialize_FeatureSpecJson_PopulatesArgGuards()
    {
        var config = JsonSerializer.Deserialize<McpBridgeConfig>(FeatureSpecJson, JsonOptions);

        Assert.IsNotNull(config);
        var server = config.McpServers["onedrive-personal"];
        Assert.AreEqual(1, server.ArgGuards.Count);

        var rule = server.ArgGuards[0];
        Assert.AreEqual("path-prefix", rule.Handler);
        CollectionAssert.AreEqual(new[] { "download_file" }, rule.Tools);
        Assert.IsNotNull(rule.Options);

        // The path-prefix handler must accept these options as-is.
        new PathPrefixArgGuard().ValidateOptions(rule.Options);
    }

    [TestMethod]
    public void Deserialize_NoArgGuards_DefaultsToEmptyList()
    {
        const string json = """{ "mcpServers": { "s": { "type": "sse", "url": "http://x/" } } }""";
        var config = JsonSerializer.Deserialize<McpBridgeConfig>(json, JsonOptions);
        Assert.IsNotNull(config);
        Assert.AreEqual(0, config.McpServers["s"].ArgGuards.Count);
    }

    [TestMethod]
    public void SerializeRoundTrip_ArgGuards_OptionsSurvive()
    {
        // PersistServerConfigAsync re-serializes the whole config back to mcp.json;
        // guard options (raw JsonElement) must survive that rewrite.
        var first = JsonSerializer.Deserialize<McpBridgeConfig>(FeatureSpecJson, JsonOptions)!;
        var rewritten = JsonSerializer.Serialize(first, JsonOptions);
        var second = JsonSerializer.Deserialize<McpBridgeConfig>(rewritten, JsonOptions)!;

        var rule = second.McpServers["onedrive-personal"].ArgGuards[0];
        Assert.AreEqual("path-prefix", rule.Handler);
        CollectionAssert.AreEqual(new[] { "download_file" }, rule.Tools);
        new PathPrefixArgGuard().ValidateOptions(rule.Options);

        var options = rule.Options!.Value;
        Assert.IsTrue(options.GetProperty("requireArgs").GetBoolean());
        Assert.AreEqual("/rockbot/shared", options.GetProperty("allowedPrefixes")[0].GetString());
    }
}
