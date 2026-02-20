namespace RockBot.UserProxy.Blazor.Services;

/// <summary>
/// Manages chat state and provides real-time updates to Blazor components.
/// </summary>
public sealed class ChatStateService
{
    private readonly List<ChatMessage> _messages = new();
    private string? _currentThinkingMessage;
    private bool _isProcessing;

    public event Action? OnStateChanged;

    public IReadOnlyList<ChatMessage> Messages => _messages.AsReadOnly();
    public string? CurrentThinkingMessage => _currentThinkingMessage;
    public bool IsProcessing => _isProcessing;

    public void LoadHistory(IReadOnlyList<ConversationHistoryTurn> turns, string sessionId)
    {
        _messages.Clear();
        foreach (var turn in turns)
        {
            _messages.Add(new ChatMessage
            {
                Content = turn.Content,
                IsFromUser = turn.Role == "user",
                Timestamp = turn.Timestamp.UtcDateTime,
                SessionId = sessionId
            });
        }
        NotifyStateChanged();
    }

    public void AddUserMessage(string content, string userId, string sessionId)
    {
        _messages.Add(new ChatMessage
        {
            Content = content,
            IsFromUser = true,
            Timestamp = DateTime.UtcNow,
            UserId = userId,
            SessionId = sessionId
        });
        NotifyStateChanged();
    }

    public void AddAgentReply(AgentReply reply)
    {
        _messages.Add(new ChatMessage
        {
            Content = reply.Content,
            IsFromUser = false,
            Timestamp = DateTime.UtcNow,
            AgentName = reply.AgentName,
            SessionId = reply.SessionId,
            ContentType = reply.ContentType
        });
        NotifyStateChanged();
    }

    /// <summary>
    /// Records the user's thumbs-up or thumbs-down on a specific agent message.
    /// Has no effect if the message has already been rated.
    /// </summary>
    public void RecordFeedback(string messageId, bool isPositive)
    {
        var msg = _messages.FirstOrDefault(m => m.MessageId == messageId);
        if (msg is null || msg.Feedback != FeedbackState.None)
            return;

        msg.Feedback = isPositive ? FeedbackState.ThumbsUp : FeedbackState.ThumbsDown;
        NotifyStateChanged();
    }

    public void SetThinkingMessage(string? message)
    {
        _currentThinkingMessage = message;
        NotifyStateChanged();
    }

    public void SetProcessing(bool isProcessing)
    {
        _isProcessing = isProcessing;
        if (!isProcessing)
        {
            _currentThinkingMessage = null;
        }
        NotifyStateChanged();
    }

    public void AddError(string message)
    {
        _messages.Add(new ChatMessage
        {
            Content = message,
            IsFromUser = false,
            IsError = true,
            Timestamp = DateTime.UtcNow
        });
        NotifyStateChanged();
    }

    private void NotifyStateChanged() => OnStateChanged?.Invoke();
}

public enum FeedbackState { None, ThumbsUp, ThumbsDown }

public sealed class ChatMessage
{
    public string MessageId { get; init; } = Guid.NewGuid().ToString("N");
    public required string Content { get; init; }
    public required bool IsFromUser { get; init; }
    public required DateTime Timestamp { get; init; }
    public string? AgentName { get; init; }
    public string? UserId { get; init; }
    public string? SessionId { get; init; }
    public string? ContentType { get; init; }
    public bool IsError { get; init; }

    /// <summary>Mutable so <see cref="ChatStateService.RecordFeedback"/> can update it in place.</summary>
    public FeedbackState Feedback { get; set; } = FeedbackState.None;
}
