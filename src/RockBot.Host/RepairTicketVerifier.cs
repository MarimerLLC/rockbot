using System.Text.Json;
using Microsoft.Extensions.Logging;
using RockBot.Tools;

namespace RockBot.Host;

/// <summary>
/// Evaluates a <see cref="VerifyShape"/> for a <see cref="RepairTicket"/> by re-running
/// its tool call through <c>mcp_invoke_tool</c>. Behaves like
/// <see cref="CapabilityClaimVerifier"/> but **never caches** results — each apply
/// attempt must observe the post-apply state, not a stale verification from a
/// previous cycle.
/// </summary>
internal sealed class RepairTicketVerifier : IRepairTicketVerifier
{
    /// <summary>Default per-call wallclock budget: 5 seconds.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(5);

    private const string McpInvokeTool = "mcp_invoke_tool";

    private static readonly JsonSerializerOptions ArgsJsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    private readonly IToolRegistry _toolRegistry;
    private readonly ILogger<RepairTicketVerifier> _logger;
    private readonly TimeSpan _budget;

    public RepairTicketVerifier(
        IToolRegistry toolRegistry,
        ILogger<RepairTicketVerifier> logger,
        TimeSpan? budget = null)
    {
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _budget = budget ?? DefaultBudget;
    }

    public Task<VerifyResult> VerifyAsync(VerifyShape shape, CancellationToken cancellationToken = default) =>
        VerifyAsync(shape, budget: null, cancellationToken);

    public async Task<VerifyResult> VerifyAsync(VerifyShape shape, TimeSpan? budget, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shape);

        var executor = _toolRegistry.GetExecutor(McpInvokeTool);
        if (executor is null)
        {
            var miss = new VerifyResult(VerifyOutcome.Uncertain,
                $"{McpInvokeTool} executor not registered — verifier cannot run");
            _logger.LogWarning("RepairTicketVerifier could not run: {Detail}", miss.Detail);
            return miss;
        }

        var request = BuildRequest(shape);
        var effectiveBudget = budget ?? _budget;

        VerifyResult result;
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetCts.CancelAfter(effectiveBudget);

        try
        {
            var response = await executor.ExecuteAsync(request, budgetCts.Token);
            result = CapabilityClaimVerifier.EvaluateExpectation(response, shape.Expect);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = new VerifyResult(
                VerifyOutcome.Uncertain,
                $"verify budget exceeded ({effectiveBudget.TotalSeconds:F1}s)",
                TimedOut: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = new VerifyResult(VerifyOutcome.Uncertain,
                $"verifier error: {ex.GetType().Name}: {ex.Message}");
        }

        _logger.LogDebug(
            "RepairTicketVerifier {Server}/{Tool} budget={Budget:F1}s → {Outcome}{Detail}",
            shape.Server, shape.Tool, effectiveBudget.TotalSeconds, result.Outcome,
            result.Detail is null ? "" : $" ({result.Detail})");

        return result;
    }

    private static ToolInvokeRequest BuildRequest(VerifyShape shape)
    {
        var argsObj = new Dictionary<string, object?>
        {
            ["server_name"] = shape.Server,
            ["tool_name"] = shape.Tool,
            ["arguments"] = shape.Arguments,
        };

        return new ToolInvokeRequest
        {
            ToolCallId = "repair-verify-" + Guid.NewGuid().ToString("N")[..12],
            ToolName = McpInvokeTool,
            Arguments = JsonSerializer.Serialize(argsObj, ArgsJsonOptions),
        };
    }
}
