using Microsoft.Extensions.DependencyInjection;
using RockBot.Messaging;

namespace RockBot.A2A.IntegrationTests.Scenarios;

/// <summary>
/// Verifies RockBot publishes its AgentCard on the discovery.announce topic.
/// </summary>
internal static class DiscoveryScenario
{
    public static async Task VerifyAnnouncementAsync(IServiceProvider services, CancellationToken ct)
    {
        var subscriber = services.GetRequiredService<IMessageSubscriber>();

        var tcs = new TaskCompletionSource<AgentCard>();
        var subName = $"a2a-test-discovery-{Guid.NewGuid():N}";

        await using var subscription = await subscriber.SubscribeAsync(
            topic: "discovery.announce",
            subscriptionName: subName,
            handler: (envelope, _) =>
            {
                try
                {
                    var card = envelope.GetPayload<AgentCard>();
                    if (card?.AgentName == "RockBot" && card.IsDeregistering != true)
                        tcs.TrySetResult(card);
                }
                catch
                {
                    // Not an AgentCard or deserialization failed — ignore
                }
                return Task.FromResult(MessageResult.Ack);
            },
            ct);

        var card = await tcs.Task.WaitAsync(ct);

        Assert(card.AgentName == "RockBot", $"Expected AgentName 'RockBot', got '{card.AgentName}'");
        Assert(card.Skills is { Count: >= 2 }, $"Expected at least 2 skills, got {card.Skills?.Count ?? 0}");

        var skillIds = card.Skills!.Select(s => s.Id).ToList();
        Assert(skillIds.Contains("notify-user"), "Missing 'notify-user' skill");
        Assert(skillIds.Contains("query-availability"), "Missing 'query-availability' skill");
        Assert(!string.IsNullOrEmpty(card.Description), "AgentCard description is empty");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
