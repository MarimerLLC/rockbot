namespace RockBot.UserProxy;

/// <summary>
/// Points at a reply the agent stored as a conversation turn: the reply's
/// <see cref="AgentReply.SessionId"/> and <see cref="AgentReply.MessageId"/>, which is also the
/// stored turn's id in that session. Carried on <see cref="UserMessage.RelatedReplies"/>.
/// </summary>
/// <param name="SessionId">The session the reply was stored in.</param>
/// <param name="MessageId">The reply's message id, the same as its stored turn's id.</param>
public sealed record ReplyReference(string SessionId, string MessageId);
