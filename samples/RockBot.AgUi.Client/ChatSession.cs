using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using RockBot.UserProxy;
using RockBot.UserProxy.ChatState;

namespace RockBot.AgUi.Client;

/// <summary>Settings for one chat on one thread.</summary>
public sealed class ChatSessionOptions
{
    /// <summary>The AG-UI thread, which is the RockBot session. It must be one the proxy allows.</summary>
    public string ThreadId { get; init; } = "agui-session";

    /// <summary>
    /// The name shown on the user's own messages. Local only: the proxy decides the user id the
    /// agent sees (the token's email address in app mode, its configured default otherwise).
    /// </summary>
    public string UserId { get; init; } = "you";

    /// <summary>
    /// What the client can render, as <c>ClientCapabilities</c> names. Sent with every message, so
    /// the agent writes for this client. Keep it to what the view really does.
    /// </summary>
    public IReadOnlyList<string> Capabilities { get; init; } =
    [
        nameof(ClientCapabilities.Text),
        nameof(ClientCapabilities.MarkdownBasic),
        nameof(ClientCapabilities.MarkdownHeadings),
        nameof(ClientCapabilities.MarkdownCode),
        nameof(ClientCapabilities.LinkInline),
        nameof(ClientCapabilities.ImageAttachment),
    ];

    /// <summary>
    /// How long to wait before each reconnect of the notify stream; the last value repeats. A
    /// connection that delivered an event starts again from the first.
    /// </summary>
    public IReadOnlyList<TimeSpan> ReconnectDelays { get; init; } =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30),
    ];

    public TimeProvider? TimeProvider { get; init; }
}

/// <summary>The state of the notify stream's connection.</summary>
public enum NotifyConnection
{
    /// <summary>Not started, or stopped for good (the proxy refused the thread).</summary>
    Stopped,
    Connecting,
    Connected,

    /// <summary>The connection dropped; waiting to try again.</summary>
    Reconnecting,
}

/// <summary>
/// One chat with the agent through the AG-UI proxy: the transport under a
/// <see cref="ChatStateService"/>. It sends turns, keeps the notify stream open, loads history
/// and open questions, and feeds everything it hears to the chat state as <see cref="ChatEvent"/>s
/// (through <see cref="AgUiEventMapper"/>). A view renders <see cref="Chat"/> and calls the
/// methods here; it never changes the state itself.
/// </summary>
public sealed class ChatSession : IAsyncDisposable
{
    private readonly AgUiProxyClient _client;
    private readonly ChatSessionOptions _options;
    private readonly TimeProvider _clock;
    private readonly AgUiEventMapper _notify;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<string, string> _attachmentUrls = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TicketResponse> _tickets = new(StringComparer.Ordinal);
    private Task? _notifyLoop;
    private Task? _load;
    private ActiveRun? _run;

    public ChatSession(AgUiProxyClient client, ChatStateService chat, ChatSessionOptions? options = null)
    {
        _client = client;
        Chat = chat;
        _options = options ?? new ChatSessionOptions();
        _clock = _options.TimeProvider ?? TimeProvider.System;
        _notify = AgUiEventMapper.ForNotify(_options.ThreadId, _clock);
    }

    /// <summary>The chat state this session feeds.</summary>
    public ChatStateService Chat { get; }

    public string ThreadId => _options.ThreadId;

    public NotifyConnection Connection { get; private set; }

    /// <summary>Why the notify stream is not connected, when it isn't.</summary>
    public string? ConnectionError { get; private set; }

    /// <summary>Raised when <see cref="Connection"/> or <see cref="ConnectionError"/> changes.</summary>
    public event Action? ConnectionChanged;

    /// <summary>The id of the last notify event processed: what a reconnect sends as <c>Last-Event-ID</c>.</summary>
    public string? LastEventId { get; private set; }

    /// <summary>
    /// Opens the notify stream and keeps it open until the session is disposed. History, the
    /// agent's status and the open questions are loaded when the stream says it is connected, so
    /// nothing can fall between the load and the first event.
    /// </summary>
    public void Start() => _notifyLoop ??= Task.Run(() => NotifyLoopAsync(_stopping.Token));

    // ── Turns ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends a message and applies the run's events as they stream. Returns when the run has
    /// ended, however it ended: the outcome is in the chat state, not in an exception.
    /// </summary>
    public async Task SendAsync(
        string text, IReadOnlyList<AgentAttachment>? attachments = null, CancellationToken cancellationToken = default)
    {
        // The conversation is being (re)loaded, which replaces the transcript: let it finish, or
        // the message about to be shown would be wiped by a history that doesn't have it yet.
        if (_load is { IsCompleted: false } loading)
            await loading.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        var runId = "run-" + ChatIds.New();
        var mapper = AgUiEventMapper.ForRun(ThreadId, runId, _clock);
        mapper.PrimaryAgentName = Chat.State.AgentName;

        using var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        var run = new ActiveRun(runId, abort);
        _run = run;
        Apply(AgUiEventMapper.BeginTurn(ThreadId, runId, text, _options.UserId, attachments));

        try
        {
            var request = new AgUiRunRequest(ThreadId, runId, text)
            {
                Attachments = attachments ?? [],
                Capabilities = _options.Capabilities,
            };
            await foreach (var frame in _client.RunAsync(request, abort.Token).ConfigureAwait(false))
            {
                Apply(mapper.Map(frame.Event));
                Remember(mapper.AttachmentUrls);
            }

            Apply(mapper.StreamEnded());
        }
        catch (OperationCanceledException) when (abort.IsCancellationRequested)
        {
            // Aborted: by Cancel (the chat already stopped waiting) or by a caller that gave up.
            if (Chat.State.CurrentTurn?.TurnId == runId)
                Apply(mapper.StreamEnded("the request was cancelled"));
        }
        catch (AgUiProxyException ex)
        {
            // Refused before any event: a thread that isn't allowed, a proxy with no bus, a bad file reference.
            Apply([new TurnEnded(runId, TurnOutcome.Failed) { Error = ex.Message }]);
        }
        catch (Exception ex)
        {
            Apply(mapper.StreamEnded(ex.Message));
        }
        finally
        {
            // The agent may still be working on a run the proxy stopped waiting for; what it
            // says from now on arrives on the notify stream, and belongs to this run.
            if (mapper.ReplyPending)
                _notify.LateRunId = runId;
            Interlocked.CompareExchange(ref _run, null, run);
        }
    }

    /// <summary>
    /// Stops waiting and asks the agent to stop the thread's work. The open run then ends as
    /// cancelled. If the proxy can't be asked, the run's connection is closed instead, which the
    /// proxy also takes as a cancel.
    /// </summary>
    public async Task CancelAsync()
    {
        Chat.Apply(new UserCancelled());
        try
        {
            await _client.CancelAsync(ThreadId, _stopping.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_run is { } run)
                await run.Abort.CancelAsync().ConfigureAwait(false);
        }
    }

    // ── MCP questions (custom mode) ──────────────────────────────────────────

    /// <summary>
    /// Answers a question's form with <paramref name="answers"/> (field values by field name), or
    /// declines it. What becomes of the answer arrives on the notify stream. False when the chat
    /// state refused it (the form is closed or already being sent) or the request failed, in
    /// which case the form opens again with the reason.
    /// </summary>
    public async Task<bool> AnswerAsync(string questionId, JsonObject? answers, bool decline = false)
    {
        if (!Chat.Apply(new McpAnswerSubmitted(questionId)))
            return false;

        try
        {
            await _client.AnswerQuestionAsync(questionId, ThreadId, answers, decline, _stopping.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Chat.Apply(new McpAnswerFailed(questionId, ex.Message));
            return false;
        }
    }

    // ── Attachments ──────────────────────────────────────────────────────────

    /// <summary>Uploads a file and returns the reference to send with the next message.</summary>
    public async Task<AgentAttachment> UploadAsync(
        string fileName, string contentType, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        var stored = await _client.UploadAsync(ThreadId, fileName, contentType, content, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(stored.Url))
            _attachmentUrls[stored.Path] = stored.Url;
        return stored.ToAttachment();
    }

    /// <summary>
    /// The address a browser can load an attachment from by itself (<c>&lt;img src&gt;</c>, a
    /// link). In network mode that is the attachment's own <c>url</c>. In app mode such a request
    /// carries no token, so it is a ticketed URL, good for a few minutes; ask again when showing
    /// the attachment later.
    /// </summary>
    public async ValueTask<Uri> AttachmentUrlAsync(AgentAttachment attachment, CancellationToken cancellationToken = default)
    {
        if (!_client.UsesAccessToken)
            return _client.Resolve(_attachmentUrls.GetValueOrDefault(attachment.Path) ?? DefaultAttachmentUrl(attachment.Path));

        if (!_tickets.TryGetValue(attachment.Path, out var ticket)
            || ticket.ExpiresAt is not { } expires || expires - _clock.GetUtcNow() < TimeSpan.FromSeconds(30))
        {
            ticket = await _client.AttachmentTicketAsync(attachment.Path, cancellationToken).ConfigureAwait(false);
            _tickets[attachment.Path] = ticket;
        }

        return _client.Resolve(ticket.Url);
    }

    /// <summary>The URL the proxy gives an attachment, for one that arrived without a <c>url</c>: each path segment escaped.</summary>
    internal static string DefaultAttachmentUrl(string path) =>
        "/attachments/" + string.Join('/', path.Split('/', '\\').Where(s => s.Length > 0).Select(Uri.EscapeDataString));

    // ── The notify stream ────────────────────────────────────────────────────

    private async Task NotifyLoopAsync(CancellationToken stopping)
    {
        var attempt = 0;
        while (!stopping.IsCancellationRequested)
        {
            SetConnection(LastEventId is null && attempt == 0 ? NotifyConnection.Connecting : NotifyConnection.Reconnecting, ConnectionError);
            string? error;
            try
            {
                await foreach (var frame in _client.NotifyAsync(ThreadId, LastEventId, stopping).ConfigureAwait(false))
                {
                    attempt = 0;
                    SetConnection(NotifyConnection.Connected, null);
                    await OnNotifyAsync(frame, stopping).ConfigureAwait(false);
                }

                // The proxy ended it: a rollout, a token that expired, or this client fell behind.
                error = "The notify stream ended.";
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                break;
            }
            catch (AgUiProxyException ex) when (ex.Status is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
            {
                // Not allowed (the thread, the origin, the account). Trying again changes nothing.
                SetConnection(NotifyConnection.Stopped, ex.Message);
                return;
            }
            catch (Exception ex)
            {
                // Anything else is a drop: the network, a 401 for an expired token, a 503 while
                // the proxy reconnects to its bus. Reconnect with the last id and miss nothing.
                error = ex.Message;
            }

            SetConnection(NotifyConnection.Reconnecting, error);
            var delays = _options.ReconnectDelays;
            var delay = delays.Count == 0 ? TimeSpan.Zero : delays[Math.Min(attempt, delays.Count - 1)];
            attempt++;
            try
            {
                await Task.Delay(delay, _clock, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        SetConnection(NotifyConnection.Stopped, null);
    }

    private async Task OnNotifyAsync(AgUiFrame frame, CancellationToken cancellationToken)
    {
        if (frame.Event is CustomEvent { Name: RockBotEvents.Connected or RockBotEvents.Resync })
        {
            // A new stream, or one that missed events it can't be given again. Either way what
            // the chat shows is rebuilt from the agent: history, its status, the open questions.
            // The event's id is the stream's position now, so the load can't miss what follows.
            if (!string.IsNullOrEmpty(frame.Id))
                LastEventId = frame.Id;
            _notify.Reset();
            _load = LoadAsync(cancellationToken);
            await _load.ConfigureAwait(false);
            return;
        }

        _notify.PrimaryAgentName ??= Chat.State.AgentName;
        Apply(_notify.Map(frame.Event));
        Remember(_notify.AttachmentUrls);
        if (!string.IsNullOrEmpty(frame.Id))
            LastEventId = frame.Id;
    }

    /// <summary>
    /// Loads what a stream can't replay: the agent's name and status, the conversation, and the
    /// questions still open. Each part is best effort; one that fails leaves the rest.
    /// </summary>
    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        AgentInfo? agent = null;
        try
        {
            agent = await _client.GetAgentAsync(ThreadId, cancellationToken).ConfigureAwait(false);
            if (agent is { AgentName.Length: > 0 })
            {
                _notify.PrimaryAgentName = agent.AgentName;
                _notify.AgentVersion = agent.AgentVersion;
                Chat.Apply(new AgentInfoReceived(agent.AgentName, agent.AgentVersion));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The agent didn't say (504 when it is down). The chat still works without its name.
        }

        try
        {
            var snapshot = await _client.GetMessagesAsync(ThreadId, cancellationToken).ConfigureAwait(false);
            Chat.Apply(AgUiEventMapper.History(snapshot, ThreadId, _attachmentUrls, _clock));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Chat.Apply(new ErrorReported($"Could not load the conversation: {ex.Message}"));
        }

        // After the history: it replaces the transcript, and with it the logs of running work.
        if (agent is not null)
        {
            Chat.Apply(new ActiveStatusReconciled(new ActiveStatusResponse
            {
                Subagents = [],
                // A turn this session is streaming is running whatever the agent said a moment ago.
                IsProcessing = agent.IsProcessing || _run is not null,
                Work = agent.Work ?? [],
            }));
        }

        try
        {
            foreach (var value in await _client.GetQuestionsAsync(ThreadId, cancellationToken).ConfigureAwait(false))
            {
                if (AgUiEventMapper.Question(value) is { } question)
                    Chat.Apply(question);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Open questions also arrive on the notify stream; a failed list only loses old ones.
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void Apply(IReadOnlyList<ChatEvent> events)
    {
        foreach (var chatEvent in events)
            Chat.Apply(chatEvent);
    }

    private void Remember(IReadOnlyDictionary<string, string> urls)
    {
        foreach (var (path, url) in urls)
            _attachmentUrls[path] = url;
    }

    private void SetConnection(NotifyConnection connection, string? error)
    {
        if (Connection == connection && ConnectionError == error)
            return;
        Connection = connection;
        ConnectionError = error;
        ConnectionChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_notifyLoop is { } loop)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The loop reports its failures through Connection; nothing to add while closing.
            }
        }
        _stopping.Dispose();
    }

    private sealed record ActiveRun(string RunId, CancellationTokenSource Abort);
}
