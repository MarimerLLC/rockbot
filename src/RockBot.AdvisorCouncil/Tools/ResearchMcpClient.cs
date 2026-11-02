using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using RockBot.AdvisorCouncil.Council;
using RockBot.Host;
using RockBot.Tools.Mcp.Elicitation;

namespace RockBot.AdvisorCouncil.Tools;

/// <summary>The council's way to ask for research: a question in, findings (or a <c>(research …)</c> sentinel) out.</summary>
internal interface IResearchClient
{
    /// <param name="question">The focused research question.</param>
    /// <param name="context">
    /// Background for the research server: the council's question and the asking lens. It also
    /// settles most of research's clarifying questions, since the council's responder answers
    /// only from the call's own arguments.
    /// </param>
    /// <param name="ct">Cancels the call (and the research task on the server).</param>
    /// <param name="timeout">
    /// How long to wait for the answer; defaults to <see cref="CouncilOptions.ResearchAgentTimeoutSeconds"/>.
    /// </param>
    Task<string> InvokeAsync(string question, string? context, CancellationToken ct, TimeSpan? timeout = null);
}

/// <summary>
/// Calls the research server's MCP <c>research</c> tool (<c>design/research-agent-mcp.md</c>,
/// Phase 3). Replaces the A2A <c>ResearchAgentInvoker</c> and its private reply queue.
/// </summary>
/// <remarks>
/// <para>
/// The call runs as a Tasks-extension task (<see cref="McpTasksClientExtensions.CallToolAsTaskAsync"/>)
/// and is polled here rather than through <c>CallToolWithPollingAsync</c>, for one reason: on a
/// timeout this client cancels the task on the server, so a persona that gave up doesn't leave a
/// research run spending budget nobody will read. <c>CallToolWithPollingAsync</c> keeps the task
/// id to itself and leaves it running.
/// </para>
/// <para>
/// Research may ask one clarifying question. Inside a task it arrives as an input request on the
/// poll and is answered by #593's <see cref="McpElicitationCoordinator"/> with the <c>llm</c>
/// responder, which answers only from this call's own arguments: nothing reaches research that
/// research wasn't already sent. The council passes a rich <c>context</c>, so that usually
/// settles it; a decline is fine, and research proceeds with its most likely reading and says so.
/// </para>
/// <para>
/// Failures come back as text beginning <c>(research</c> — <see cref="PersonaStep"/> keeps those
/// out of working memory — rather than as exceptions, except cancellation of <c>ct</c>.
/// </para>
/// </remarks>
internal sealed class ResearchMcpClient : IResearchClient, IAsyncDisposable
{
    /// <summary>The server name used in logs and elicitation records.</summary>
    public const string ServerName = "research";

    /// <summary>The tool this client calls.</summary>
    public const string ToolName = "research";

    private static readonly TimeSpan MinPoll = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxPoll = TimeSpan.FromSeconds(5);

    private readonly CouncilOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly McpElicitationCoordinator? _elicitation;
    private readonly Func<CancellationToken, Task<McpClient>> _connect;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private McpClient? _client;

    public ResearchMcpClient(IOptions<CouncilOptions> options, ILlmClient llmClient, ILoggerFactory loggerFactory)
        : this(options, llmClient, loggerFactory, connect: null)
    {
    }

    /// <summary>Test seam: <paramref name="connect"/> replaces the HTTP connection to the configured URL.</summary>
    internal ResearchMcpClient(
        IOptions<CouncilOptions> options,
        ILlmClient llmClient,
        ILoggerFactory loggerFactory,
        Func<McpClientOptions, CancellationToken, Task<McpClient>>? connect)
    {
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ResearchMcpClient>();
        _elicitation = McpElicitationCoordinator.TryCreate(
            ServerName,
            new McpElicitationConfig { Mode = McpElicitationConfig.ModeAuto, Responder = LlmElicitationResponder.Key },
            new LlmElicitationResponder(llmClient, loggerFactory.CreateLogger<LlmElicitationResponder>()),
            _logger);

        var clientOptions = new McpClientOptions();
        if (_elicitation is { } coordinator)
            clientOptions.Handlers = new McpClientHandlers { ElicitationHandler = coordinator.HandleAsync };

        _connect = connect is not null
            ? ct => connect(clientOptions, ct)
            : ct => McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri(_options.ResearchServerUrl),
                    TransportMode = HttpTransportMode.StreamableHttp,
                }, _loggerFactory),
                clientOptions, _loggerFactory, ct);
    }

    public async Task<string> InvokeAsync(string question, string? context, CancellationToken ct, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(question))
            return "(empty research question)";

        var arguments = new Dictionary<string, JsonElement>
        {
            ["question"] = JsonSerializer.SerializeToElement(question.Trim()),
        };
        if (!string.IsNullOrWhiteSpace(context))
            arguments["context"] = JsonSerializer.SerializeToElement(context.Trim());
        if (!string.IsNullOrWhiteSpace(_options.ResearchDepth))
            arguments["depth"] = JsonSerializer.SerializeToElement(_options.ResearchDepth.Trim());

        var limit = timeout ?? TimeSpan.FromSeconds(Math.Max(5, _options.ResearchAgentTimeoutSeconds));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(limit);

        // Makes this call the current one for the coordinator, so research's question is
        // answered from exactly these arguments.
        using var scope = _elicitation?.BeginCall(ToolName, JsonSerializer.Serialize(arguments));

        var sw = Stopwatch.StartNew();
        try
        {
            var client = await GetClientAsync(timeoutCts.Token);
            var request = new CallToolRequestParams { Name = ToolName, Arguments = arguments };
            var started = await client.CallToolAsTaskAsync(request, timeoutCts.Token);
            var result = started.IsTask
                ? await PollAsync(client, started.TaskCreated!, timeoutCts.Token)
                : started.Result!;

            var text = Format(result);
            _logger.LogInformation("Research call returned in {Ms}ms ({Len} chars)", sw.ElapsedMilliseconds, text.Length);
            return text;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Research call timed out after {Sec}s", limit.TotalSeconds);
            return "(research timed out)";
        }
        catch (McpException ex)
        {
            _logger.LogWarning(ex, "Research call failed on the server");
            return $"(research failed: {Shorten(ex.Message)})";
        }
        catch (Exception ex)
        {
            // Transport trouble: drop the connection so the next call makes a fresh one.
            _logger.LogError(ex, "Research call failed");
            await ResetClientAsync();
            return $"(research call failed: {Shorten(ex.Message)})";
        }
    }

    /// <summary>
    /// Polls a research task to its end, answering any input request through the client's
    /// elicitation handler. Cancels the task on the server if <paramref name="ct"/> fires first.
    /// </summary>
    private async Task<CallToolResult> PollAsync(McpClient client, CreateTaskResult created, CancellationToken ct)
    {
        var taskId = created.TaskId;
        var interval = ClampPoll(created.PollIntervalMs);
        var answered = new HashSet<string>(StringComparer.Ordinal);
        var finished = false;
        try
        {
            while (true)
            {
                var task = await client.GetTaskAsync(taskId, ct);
                if (task.PollIntervalMs is { } suggested)
                    interval = ClampPoll(suggested);

                switch (task)
                {
                    case CompletedTaskResult completed:
                        finished = true;
                        return completed.Result.Deserialize<CallToolResult>(McpJsonUtilities.DefaultOptions)
                               ?? throw new McpException("the research task completed without a result");

                    case FailedTaskResult failed:
                        finished = true;
                        throw new McpException($"the research task failed: {failed.Error}");

                    case CancelledTaskResult:
                        finished = true;
                        throw new McpException("the research task was cancelled on the server");

                    case InputRequiredTaskResult input when input.InputRequests is { Count: > 0 } requests:
                        var fresh = requests.Where(r => !answered.Contains(r.Key))
                            .ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
                        if (fresh.Count > 0)
                        {
                            var responses = await client.ResolveInputRequestsAsync(fresh, ct);
                            await client.UpdateTaskAsync(new UpdateTaskRequestParams { TaskId = taskId, InputResponses = responses }, ct);
                            answered.UnionWith(responses.Keys);
                            continue;
                        }
                        break;
                }

                await Task.Delay(interval, ct);
            }
        }
        finally
        {
            if (!finished)
            {
                try
                {
                    await client.CancelTaskAsync(taskId, CancellationToken.None);
                    _logger.LogInformation("Cancelled research task {TaskId} on the server", taskId);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not cancel research task {TaskId}", taskId);
                }
            }
        }
    }

    /// <summary>The findings: every text block, joined. An error result becomes a failure sentinel.</summary>
    internal static string Format(CallToolResult result)
    {
        var text = new StringBuilder();
        foreach (var block in result.Content.OfType<TextContentBlock>())
        {
            if (string.IsNullOrWhiteSpace(block.Text))
                continue;
            if (text.Length > 0)
                text.Append("\n\n");
            text.Append(block.Text.Trim());
        }

        if (result.IsError == true)
            return $"(research failed: {Shorten(text.Length > 0 ? text.ToString() : "the tool reported an error")})";
        return text.Length > 0 ? text.ToString() : "(no research result)";
    }

    private async Task<McpClient> GetClientAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _client) is { } existing)
            return existing;

        await _connectLock.WaitAsync(ct);
        try
        {
            return _client ??= await _connect(ct);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task ResetClientAsync()
    {
        await _connectLock.WaitAsync();
        try
        {
            if (_client is { } client)
            {
                _client = null;
                try { await client.DisposeAsync(); } catch { /* best effort */ }
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private static TimeSpan ClampPoll(long? ms) =>
        ms is { } value
            ? TimeSpan.FromMilliseconds(Math.Clamp(value, MinPoll.TotalMilliseconds, MaxPoll.TotalMilliseconds))
            : TimeSpan.FromSeconds(1);

    private static string Shorten(string text) =>
        text.Length <= 300 ? text : text[..299] + "…";

    public async ValueTask DisposeAsync()
    {
        if (_client is { } client)
            await client.DisposeAsync();
        _connectLock.Dispose();
    }
}
