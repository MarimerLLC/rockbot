using System.Text.Json;

namespace RockBot.A2A.Tests;

/// <summary>
/// JSON round-trip tests for the A2A abstractions. These guard the wire
/// contract that the gateway bridge relies on — the <c>Metadata</c> maps on
/// <see cref="AgentMessage"/> and <see cref="AgentTaskRequest"/> must survive
/// serialization so capability handlers can read values a caller attached
/// to the request.
/// </summary>
[TestClass]
public class AgentMessageSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    [TestMethod]
    public void AgentMessage_Metadata_RoundTripsThroughJson()
    {
        var original = new AgentMessage
        {
            Role = "user",
            Parts = [new AgentMessagePart { Kind = "text", Text = "extract" }],
            Metadata = new Dictionary<string, string>
            {
                ["url"] = "https://example.com/page",
                ["description"] = "the product name"
            }
        };

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<AgentMessage>(json, JsonOptions);

        Assert.IsNotNull(deserialized);
        Assert.IsNotNull(deserialized.Metadata);
        Assert.AreEqual("https://example.com/page", deserialized.Metadata["url"]);
        Assert.AreEqual("the product name", deserialized.Metadata["description"]);
    }

    [TestMethod]
    public void AgentMessage_Metadata_IsOptional()
    {
        var message = new AgentMessage
        {
            Role = "user",
            Parts = [new AgentMessagePart { Kind = "text", Text = "hi" }]
        };

        var json = JsonSerializer.Serialize(message, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<AgentMessage>(json, JsonOptions);

        Assert.IsNotNull(deserialized);
        Assert.IsNull(deserialized.Metadata);
    }

    [TestMethod]
    public void AgentTaskRequest_Metadata_RoundTripsThroughJson()
    {
        var original = new AgentTaskRequest
        {
            TaskId = "t1",
            Skill = "extract-structured-data",
            Metadata = new Dictionary<string, string>
            {
                ["tenant"] = "acme",
                ["priority"] = "high"
            },
            Message = new AgentMessage
            {
                Role = "user",
                Parts = [new AgentMessagePart { Kind = "text", Text = "go" }]
            }
        };

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<AgentTaskRequest>(json, JsonOptions);

        Assert.IsNotNull(deserialized);
        Assert.IsNotNull(deserialized.Metadata);
        Assert.AreEqual("acme", deserialized.Metadata["tenant"]);
        Assert.AreEqual("high", deserialized.Metadata["priority"]);
    }

    [TestMethod]
    public void AgentTaskRequest_Metadata_IsOptional()
    {
        var request = new AgentTaskRequest
        {
            TaskId = "t1",
            Skill = "summarize",
            Message = new AgentMessage
            {
                Role = "user",
                Parts = [new AgentMessagePart { Kind = "text", Text = "hi" }]
            }
        };

        var json = JsonSerializer.Serialize(request, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<AgentTaskRequest>(json, JsonOptions);

        Assert.IsNotNull(deserialized);
        Assert.IsNull(deserialized.Metadata);
    }
}
