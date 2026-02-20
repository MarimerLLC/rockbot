using RockBot.Messaging;

namespace RockBot.Messaging.InProcess;

internal sealed class InProcessBus
{
    private readonly List<InProcessSubscription> _subscriptions = [];
    private readonly Lock _lock = new();

    public void Register(InProcessSubscription subscription)
    {
        lock (_lock)
        {
            _subscriptions.Add(subscription);
        }
    }

    public void Unregister(InProcessSubscription subscription)
    {
        lock (_lock)
        {
            _subscriptions.Remove(subscription);
        }
    }

    public async ValueTask DeliverAsync(string topic, MessageEnvelope envelope, CancellationToken ct)
    {
        List<InProcessSubscription> matching;
        lock (_lock)
        {
            matching = _subscriptions
                .Where(s => TopicMatches(s.Topic, topic))
                .ToList();
        }

        foreach (var subscription in matching)
        {
            await subscription.EnqueueAsync(topic, envelope, ct);
        }
    }

    internal static bool TopicMatches(string pattern, string topic)
    {
        var patternParts = pattern.Split('.');
        var topicParts = topic.Split('.');
        return MatchParts(patternParts, 0, topicParts, 0);
    }

    private static bool MatchParts(string[] pattern, int pi, string[] topic, int ti)
    {
        if (pi == pattern.Length && ti == topic.Length) return true;
        if (pi == pattern.Length) return false;

        if (pattern[pi] == "#")
        {
            // Try matching zero or more topic segments
            for (int remaining = ti; remaining <= topic.Length; remaining++)
            {
                if (MatchParts(pattern, pi + 1, topic, remaining))
                    return true;
            }
            return false;
        }

        if (ti == topic.Length) return false;

        if (pattern[pi] == "*")
        {
            return MatchParts(pattern, pi + 1, topic, ti + 1);
        }

        if (pattern[pi] == topic[ti])
        {
            return MatchParts(pattern, pi + 1, topic, ti + 1);
        }

        return false;
    }
}
