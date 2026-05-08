using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RockBot.Tools;

namespace RockBot.Host;

/// <summary>
/// Evaluates a <see cref="VerifyShape"/> against the live system by re-running its tool
/// call through <c>mcp_invoke_tool</c>. Caches results per-process, keyed by a normalized
/// hash of the shape, so a shape that is hot in a session does not pay the gateway cost
/// for every injection.
/// </summary>
/// <remarks>
/// The verifier deliberately routes through <c>mcp_invoke_tool</c> (rather than calling
/// the MCP proxy directly) so it benefits from any cross-cutting concerns wrapped around
/// that tool — including the Phase 1 mechanical recovery layer (#345). That means a verify
/// call whose missing argument is auto-filled by recovery counts as a successful call,
/// which is intentional: the predicate asks "would a real session calling this tool
/// succeed?" and recovery is part of every real session's behaviour.
/// </remarks>
public sealed class CapabilityClaimVerifier : ICapabilityClaimVerifier
{
    /// <summary>Default result cache TTL: 5 minutes.</summary>
    public static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>Default per-call wallclock budget: 5 seconds.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(5);

    private const string McpInvokeTool = "mcp_invoke_tool";

    private static readonly JsonSerializerOptions ArgsJsonOptions = new()
    {
        PropertyNamingPolicy = null,            // mcp_invoke_tool expects snake_case literals
        WriteIndented = false
    };

    private readonly IToolRegistry _toolRegistry;
    private readonly ILogger<CapabilityClaimVerifier> _logger;
    private readonly TimeProvider _time;
    private readonly TimeSpan _cacheTtl;
    private readonly TimeSpan _budget;

    private readonly ConcurrentDictionary<string, CachedResult> _cache = new();

    public CapabilityClaimVerifier(
        IToolRegistry toolRegistry,
        ILogger<CapabilityClaimVerifier> logger,
        TimeProvider? timeProvider = null,
        TimeSpan? cacheTtl = null,
        TimeSpan? budget = null)
    {
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = timeProvider ?? TimeProvider.System;
        _cacheTtl = cacheTtl ?? DefaultCacheTtl;
        _budget = budget ?? DefaultBudget;
    }

    /// <summary>
    /// Evaluates the shape and returns a categorical outcome. Never throws on predicate
    /// evaluation; gateway errors and budget exhaustion are reported as
    /// <see cref="VerifyOutcome.Uncertain"/>.
    /// </summary>
    public async Task<VerifyResult> VerifyAsync(VerifyShape shape, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(shape);

        var key = HashShape(shape);
        if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt > _time.GetUtcNow())
        {
            _logger.LogDebug("CapabilityClaimVerifier cache hit for {Server}/{Tool}", shape.Server, shape.Tool);
            return cached.Result;
        }

        var executor = _toolRegistry.GetExecutor(McpInvokeTool);
        if (executor is null)
        {
            var miss = new VerifyResult(VerifyOutcome.Uncertain,
                $"{McpInvokeTool} executor not registered — verifier cannot run");
            _logger.LogWarning("Verifier could not run: {Detail}", miss.Detail);
            // Don't cache uncertain-due-to-config — fix the config and the next call should retry.
            return miss;
        }

        var request = BuildRequest(shape);

        VerifyResult result;
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetCts.CancelAfter(_budget);

        try
        {
            var response = await executor.ExecuteAsync(request, budgetCts.Token);
            result = EvaluateExpectation(response, shape.Expect);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = new VerifyResult(VerifyOutcome.Uncertain,
                $"verify budget exceeded ({_budget.TotalSeconds:F1}s)");
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled — propagate.
            throw;
        }
        catch (Exception ex)
        {
            result = new VerifyResult(VerifyOutcome.Uncertain,
                $"verifier error: {ex.GetType().Name}: {ex.Message}");
        }

        _cache[key] = new CachedResult(result, _time.GetUtcNow() + _cacheTtl);

        _logger.LogDebug(
            "CapabilityClaimVerifier evaluated {Server}/{Tool} → {Outcome}{Detail}",
            shape.Server, shape.Tool, result.Outcome,
            result.Detail is null ? "" : $" ({result.Detail})");

        return result;
    }

    private static ToolInvokeRequest BuildRequest(VerifyShape shape)
    {
        // mcp_invoke_tool expects { server_name, tool_name, arguments } as the args body.
        var argsObj = new Dictionary<string, object?>
        {
            ["server_name"] = shape.Server,
            ["tool_name"] = shape.Tool,
            ["arguments"] = shape.Arguments
        };

        return new ToolInvokeRequest
        {
            ToolCallId = "verify-" + Guid.NewGuid().ToString("N")[..12],
            ToolName = McpInvokeTool,
            Arguments = JsonSerializer.Serialize(argsObj, ArgsJsonOptions)
        };
    }

    internal static VerifyResult EvaluateExpectation(ToolInvokeResponse response, VerifyExpectation expect) =>
        expect.Kind switch
        {
            VerifyExpectationKind.Success =>
                response.IsError
                    ? new VerifyResult(VerifyOutcome.PredicateFailed,
                        $"expected success, got error: {Truncate(response.Content)}")
                    : new VerifyResult(VerifyOutcome.PredicateSucceeded),

            VerifyExpectationKind.FailureWithMessage =>
                MatchFailurePattern(response, expect.FailurePattern!),

            _ => new VerifyResult(VerifyOutcome.Uncertain, $"unknown expectation kind: {expect.Kind}")
        };

    private static VerifyResult MatchFailurePattern(ToolInvokeResponse response, string pattern)
    {
        if (!response.IsError)
            return new VerifyResult(VerifyOutcome.PredicateFailed, "expected error, got success");

        var content = response.Content ?? string.Empty;
        var hit = content.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
        return hit
            ? new VerifyResult(VerifyOutcome.PredicateSucceeded)
            : new VerifyResult(VerifyOutcome.PredicateFailed,
                $"error did not contain '{pattern}': {Truncate(content)}");
    }

    private static string HashShape(VerifyShape shape)
    {
        // Canonical form: server | tool | raw-arguments-json | expect-kind | failure-pattern.
        // GetRawText preserves formatting from the original document; for cache identity we
        // accept that semantically-identical-but-differently-formatted JSON hashes differently.
        // That's fine — two shapes that disagree on whitespace are written by different code paths.
        var argsRaw = shape.Arguments.ValueKind == JsonValueKind.Undefined
            ? ""
            : shape.Arguments.GetRawText();
        var input = string.Join(
            '|',
            shape.Server,
            shape.Tool,
            argsRaw,
            shape.Expect.Kind.ToString(),
            shape.Expect.FailurePattern ?? "");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash);
    }

    private static string Truncate(string? s, int max = 200) =>
        s is null ? "" : (s.Length <= max ? s : s[..max] + "…");

    private sealed record CachedResult(VerifyResult Result, DateTimeOffset ExpiresAt);
}
