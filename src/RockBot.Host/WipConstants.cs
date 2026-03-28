namespace RockBot.Host;

/// <summary>
/// Well-known keys for WIP tracking state in <see cref="MessageHandlerContext.Items"/>.
/// </summary>
public static class WipConstants
{
    /// <summary>Key for the WIP message ID (string).</summary>
    public const string MessageIdKey = "wip:messageId";

    /// <summary>
    /// Set this key to prevent <c>WipMiddleware</c> from auto-completing the WIP entry
    /// when the handler returns. Background loops must call
    /// <see cref="IWipTracker.CompleteAsync"/> explicitly.
    /// </summary>
    public const string DeferredKey = "wip:deferred";

    /// <summary>Header key injected into recovered envelopes so handlers can detect replay.</summary>
    public const string RecoveryHeader = "wip:recovery";
}
