namespace RockBot.Messaging;

/// <summary>
/// Result of processing a message, telling the transport
/// how to acknowledge it.
/// </summary>
public enum MessageResult
{
    /// <summary>Message processed successfully. Acknowledge it.</summary>
    Ack,

    /// <summary>Message processing failed but is retryable. Requeue it.</summary>
    Retry,

    /// <summary>Message is poison / permanently unprocessable. Dead-letter it.</summary>
    DeadLetter
}
