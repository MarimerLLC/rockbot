using System.Text.Json;

namespace RockBot.A2A.IntegrationTests.Scenarios;

/// <summary>
/// Verifies agent-trust.json was created on the shared volume after inbound tasks.
/// </summary>
internal static class TrustStoreScenario
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static async Task VerifyTrustEntryAsync(string trustStorePath, CancellationToken ct)
    {
        // Brief delay for the file write to flush
        await Task.Delay(2000, ct);

        Assert(File.Exists(trustStorePath),
            $"Trust store file not found at '{trustStorePath}'");

        var json = await File.ReadAllTextAsync(trustStorePath, ct);
        Assert(!string.IsNullOrWhiteSpace(json), "Trust store file is empty");

        var entries = JsonSerializer.Deserialize<List<AgentTrustEntry>>(json, JsonOptions);
        Assert(entries is not null, "Could not deserialize trust store entries");
        Assert(entries!.Count > 0, "Trust store is empty (no entries)");

        var harness = entries.FirstOrDefault(e =>
            string.Equals(e.AgentId, "TestHarness", StringComparison.OrdinalIgnoreCase));
        Assert(harness is not null,
            $"No trust entry for 'TestHarness'. Found: [{string.Join(", ", entries.Select(e => e.AgentId))}]");

        Assert(harness!.Level == AgentTrustLevel.Observe,
            $"Expected Observe trust level, got {harness.Level}");

        Assert(harness.InteractionCount >= 1,
            $"Expected InteractionCount >= 1, got {harness.InteractionCount}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
