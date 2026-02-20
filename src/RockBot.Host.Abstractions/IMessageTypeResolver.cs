namespace RockBot.Host;

/// <summary>
/// Maps MessageEnvelope.MessageType strings to CLR types for
/// deserialization and handler lookup.
/// </summary>
public interface IMessageTypeResolver
{
    /// <summary>
    /// Resolve a message type string to a CLR type.
    /// </summary>
    /// <returns>The CLR type, or null if the message type is unknown.</returns>
    Type? Resolve(string messageType);
}
