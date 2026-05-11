using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RockBot.Host;

namespace RockBot.Tools.Mcp.Recovery;

/// <summary>
/// Recovery wrapper around <see cref="McpToolProxy"/>. When an
/// <c>mcp_invoke_tool</c> call fails with a "missing required field" schema
/// error, runs Stage A (deterministic providers) then Stage B (Low-tier LLM
/// fill), retries the call once, and either returns the recovered response or
/// returns the original error annotated with a recovery trail.
///
/// Some MCP servers report schema errors with <see cref="ToolInvokeResponse.IsError"/>
/// set to <c>false</c> and the error embedded in a JSON body like
/// <c>{"error":"&lt;X&gt; is required"}</c>. This wrapper inspects content
/// regardless of the <c>IsError</c> flag so we are robust to that pattern.
/// Chained recovery (multiple missing fields surfaced one at a time) is
/// supported up to a fixed depth.
///
/// See <c>design/self-repair.md</c> Phase 1.
/// </summary>
public sealed class McpRecoveryExecutor
{
    /// <summary>
    /// Maximum number of chained recovery iterations for a single tool call.
    /// Each iteration handles one missing field, so this caps the number of
    /// fields a single recovery sequence can fill before giving up.
    /// </summary>
    internal const int MaxChainDepth = 4;

    /// <summary>
    /// Maximum content length to scan for embedded JSON error fields. Larger
    /// payloads are assumed to be legitimate tool output, not error envelopes.
    /// </summary>
    internal const int MaxEmbeddedErrorScanLength = 4096;

    private readonly McpInvokeDelegate _invoke;
    private readonly IReadOnlyList<IToolArgumentDefaultsProvider> _providers;
    private readonly StageBLlmFiller? _stageB;
    private readonly ICapabilityClaimWriter? _capabilityClaimWriter;
    private readonly IFailureClusterStore? _failureClusterStore;
    private readonly ILogger<McpRecoveryExecutor> _logger;

    public McpRecoveryExecutor(
        McpInvokeDelegate invoke,
        IEnumerable<IToolArgumentDefaultsProvider> providers,
        ILogger<McpRecoveryExecutor> logger,
        StageBLlmFiller? stageB = null,
        ICapabilityClaimWriter? capabilityClaimWriter = null,
        IFailureClusterStore? failureClusterStore = null)
    {
        _invoke = invoke;
        _providers = providers.ToList();
        _stageB = stageB;
        _capabilityClaimWriter = capabilityClaimWriter;
        _failureClusterStore = failureClusterStore;
        _logger = logger;
    }

    /// <summary>
    /// Returns the original response when no recovery is possible (no recoverable
    /// error pattern, no provider, Stage B unavailable or unsuccessful), or a
    /// successful recovered response. Exhausted-recovery responses carry an
    /// annotated trail in <see cref="ToolInvokeResponse.Content"/>.
    /// </summary>
    /// <param name="sessionId">Originating session id (from the outer
    /// <c>mcp_invoke_tool</c> request). Forwarded to the failure cluster store so
    /// post-recovery failures can be grouped by distinct sessions. May be null.</param>
    public Task<ToolInvokeResponse> RecoverAsync(
        string serverName,
        string toolName,
        ToolInvokeRequest innerRequest,
        ToolInvokeResponse response,
        CancellationToken ct,
        string? sessionId = null) =>
        TryRecoverAsync(serverName, toolName, innerRequest, response, depth: 0, sessionId, ct);

    private async Task<ToolInvokeResponse> TryRecoverAsync(
        string serverName,
        string toolName,
        ToolInvokeRequest innerRequest,
        ToolInvokeResponse response,
        int depth,
        string? sessionId,
        CancellationToken ct)
    {
        if (depth >= MaxChainDepth)
        {
            // Chain exhausted — if the response still carries a recoverable error,
            // annotate so the agent sees recovery was attempted but ran out of budget.
            var chainErrorText = TryExtractRecoverableError(response);
            if (chainErrorText is null) return response;

            await EmitCapabilityClaimAsync(
                serverName, toolName, innerRequest,
                statement: $"recovery-exhausted: chain depth {depth} reached for {serverName}/{toolName}",
                evidence: chainErrorText,
                ct);
            await RecordFailureAsync(serverName, toolName, sessionId, chainErrorText, fieldHint: null, ct);
            return Annotate(response, $"chain-exhausted at depth {depth}");
        }

        var errorText = TryExtractRecoverableError(response);
        if (errorText is null) return response;

        if (!SchemaErrorPatterns.TryExtractMissingField(errorText, out var fieldName))
        {
            // Non-schema error — recovery has no field to fill. Still a post-recovery
            // failure from the gateway's perspective; record under "unknown" so
            // DreamService can spot recurring auth/network/server-side failures the
            // same way it spots schema-pattern failures.
            await RecordFailureAsync(serverName, toolName, sessionId, errorText, fieldHint: null, ct);
            return response;
        }

        var existingArgs = McpToolExecutor.ParseArguments(innerRequest.Arguments);
        if (existingArgs.ContainsKey(fieldName))
        {
            // The field is already present — the error is something else despite matching
            // the pattern (e.g., the value was wrong, not missing). Don't loop. Cluster
            // these as "unknown" so the merged group reflects "server complains a field
            // is required even though it's in args" rather than fragmenting by field.
            await RecordFailureAsync(serverName, toolName, sessionId, errorText, fieldHint: "unknown", ct);
            return response;
        }

        var sw = Stopwatch.StartNew();
        var ctx = new ResolveContext(serverName, toolName, fieldName, existingArgs);

        // ── Stage A ─────────────────────────────────────────────────────────
        foreach (var provider in _providers)
        {
            if (!provider.CanResolve(serverName, toolName, fieldName)) continue;

            ResolvedDefault? resolved;
            try
            {
                resolved = await provider.ResolveAsync(ctx, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Recovery provider {Provider} threw resolving {Server}/{Tool} field {Field}",
                    provider.GetType().Name, serverName, toolName, fieldName);
                continue;
            }

            if (resolved is null) continue;

            var providerName = provider.GetType().Name;
            var (retryResponse, mergedArgs) = await RetryAsync(
                serverName, toolName, innerRequest, existingArgs, fieldName, resolved, ct);

            sw.Stop();

            var retryError = TryExtractRecoverableError(retryResponse);
            var recovered = retryError is null;
            RecordTelemetry(serverName, toolName, fieldName, "A", recovered, providerName,
                sw.Elapsed.TotalMilliseconds);

            if (recovered)
            {
                _logger.LogInformation(
                    "MCP auto-recovered {Server}/{Tool} field {Field} via {Provider}",
                    serverName, toolName, fieldName, providerName);
                return retryResponse;
            }

            // Chained recovery: only recurse when the retry's error names a DIFFERENT
            // missing field (otherwise we'd loop on the same problem).
            if (ShouldChain(retryError, fieldName))
            {
                _logger.LogInformation(
                    "MCP recovery {Server}/{Tool} field {Field} resolved via {Provider} but response surfaced another missing field — chaining (depth {Depth})",
                    serverName, toolName, fieldName, providerName, depth + 1);

                var nextRequest = new ToolInvokeRequest
                {
                    ToolCallId = innerRequest.ToolCallId,
                    ToolName = toolName,
                    Arguments = JsonSerializer.Serialize(mergedArgs)
                };
                return await TryRecoverAsync(serverName, toolName, nextRequest, retryResponse, depth + 1, sessionId, ct);
            }

            // Retry still failed and isn't chainable.
            await EmitCapabilityClaimAsync(
                serverName, toolName, innerRequest,
                statement: $"recovery-exhausted: Stage A provider {providerName} resolved field {fieldName} but the call still failed",
                evidence: retryError ?? errorText,
                ct);
            await RecordFailureAsync(serverName, toolName, sessionId, retryError ?? errorText, fieldHint: null, ct);
            return Annotate(response, $"stageA={providerName} retry-failed: {Truncate(retryResponse.Content)}");
        }

        // ── Stage B ─────────────────────────────────────────────────────────
        if (_stageB is not null)
        {
            var filled = await _stageB.TryFillAsync(
                serverName, toolName, fieldName, existingArgs, errorText, ct);

            if (filled is not null)
            {
                var resolved = new ResolvedDefault(filled);
                var (retryResponse, mergedArgs) = await RetryAsync(
                    serverName, toolName, innerRequest, existingArgs, fieldName, resolved, ct);

                sw.Stop();
                var retryError = TryExtractRecoverableError(retryResponse);
                var recovered = retryError is null;
                RecordTelemetry(serverName, toolName, fieldName, "B", recovered, "StageBLlmFiller",
                    sw.Elapsed.TotalMilliseconds);

                if (recovered)
                {
                    _logger.LogInformation(
                        "MCP auto-recovered {Server}/{Tool} field {Field} via Stage B",
                        serverName, toolName, fieldName);
                    return retryResponse;
                }

                if (ShouldChain(retryError, fieldName))
                {
                    _logger.LogInformation(
                        "MCP recovery {Server}/{Tool} field {Field} resolved via Stage B but response surfaced another missing field — chaining (depth {Depth})",
                        serverName, toolName, fieldName, depth + 1);

                    var nextRequest = new ToolInvokeRequest
                    {
                        ToolCallId = innerRequest.ToolCallId,
                        ToolName = toolName,
                        Arguments = JsonSerializer.Serialize(mergedArgs)
                    };
                    return await TryRecoverAsync(serverName, toolName, nextRequest, retryResponse, depth + 1, sessionId, ct);
                }

                await EmitCapabilityClaimAsync(
                    serverName, toolName, innerRequest,
                    statement: $"recovery-exhausted: Stage B filled field {fieldName} but the call still failed",
                    evidence: retryError ?? errorText,
                    ct);
                await RecordFailureAsync(serverName, toolName, sessionId, retryError ?? errorText, fieldHint: null, ct);
                return Annotate(response,
                    $"stageA=no-provider; stageB=filled retry-failed: {Truncate(retryResponse.Content)}");
            }

            sw.Stop();
            RecordTelemetry(serverName, toolName, fieldName, "B", recovered: false, "StageBLlmFiller",
                sw.Elapsed.TotalMilliseconds);
            await EmitCapabilityClaimAsync(
                serverName, toolName, innerRequest,
                statement: $"recovery-exhausted: Stage B could not fill field {fieldName}",
                evidence: errorText,
                ct);
            await RecordFailureAsync(serverName, toolName, sessionId, errorText, fieldHint: fieldName, ct);
            return Annotate(response, "stageA=no-provider; stageB=fill-failed");
        }

        sw.Stop();
        RecordTelemetry(serverName, toolName, fieldName, "A", recovered: false, "no-provider",
            sw.Elapsed.TotalMilliseconds);
        await RecordFailureAsync(serverName, toolName, sessionId, errorText, fieldHint: fieldName, ct);
        return Annotate(response, "stageA=no-provider; stageB=disabled");
    }

    /// <summary>
    /// Returns the error text to attempt recovery against, or null if the response
    /// is not in a recoverable error state. Handles two shapes:
    /// <list type="number">
    ///   <item><see cref="ToolInvokeResponse.IsError"/> is <c>true</c> — content is the error.</item>
    ///   <item><see cref="ToolInvokeResponse.IsError"/> is <c>false</c> but content is a small JSON
    ///         object with an <c>error</c> property — some MCP servers report schema
    ///         failures this way instead of flagging IsError.</item>
    /// </list>
    /// </summary>
    internal static string? TryExtractRecoverableError(ToolInvokeResponse response)
    {
        if (response.IsError)
            return response.Content;

        var content = response.Content;
        if (string.IsNullOrWhiteSpace(content)) return null;
        if (content.Length > MaxEmbeddedErrorScanLength) return null;

        // Cheap shape check: must look like a JSON object.
        var trimmed = content.AsSpan().TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{') return null;

        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            // "error" or "Error" (in that order) — only string values count.
            // Deliberately not "message" — too many legitimate success responses use that.
            foreach (var key in EmbeddedErrorKeys)
            {
                if (doc.RootElement.TryGetProperty(key, out var v)
                    && v.ValueKind == JsonValueKind.String)
                {
                    var text = v.GetString();
                    return string.IsNullOrWhiteSpace(text) ? null : text;
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON; nothing to extract.
        }

        return null;
    }

    private static readonly string[] EmbeddedErrorKeys = ["error", "Error"];

    /// <summary>
    /// Returns true if a retry response's error indicates a *different* missing
    /// field than the one we just filled, meaning chained recovery has a fresh
    /// problem to solve. Returns false if there's no recoverable error, no
    /// pattern match, or the same field is reported (which would loop).
    /// </summary>
    private static bool ShouldChain(string? retryError, string justFilledField)
    {
        if (retryError is null) return false;
        if (!SchemaErrorPatterns.TryExtractMissingField(retryError, out var nextField)) return false;
        return !string.Equals(nextField, justFilledField, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(ToolInvokeResponse Response, Dictionary<string, object?> MergedArgs)> RetryAsync(
        string serverName,
        string toolName,
        ToolInvokeRequest innerRequest,
        Dictionary<string, object?> existingArgs,
        string fieldName,
        ResolvedDefault resolved,
        CancellationToken ct)
    {
        var merged = MergeArgs(existingArgs, fieldName, resolved.Value);
        var response = await CallAsync(serverName, toolName, innerRequest, merged, ct);
        return (response, merged);
    }

    private async Task<ToolInvokeResponse> CallAsync(
        string serverName,
        string toolName,
        ToolInvokeRequest innerRequest,
        Dictionary<string, object?> merged,
        CancellationToken ct)
    {
        var argsJson = JsonSerializer.Serialize(merged);
        var retried = new ToolInvokeRequest
        {
            ToolCallId = innerRequest.ToolCallId,
            ToolName = toolName,
            Arguments = argsJson
        };
        var headers = new Dictionary<string, string>
        {
            [McpHeaders.ServerName] = serverName
        };
        return await _invoke(retried, headers, ct);
    }

    private static Dictionary<string, object?> MergeArgs(
        Dictionary<string, object?> existing, string fieldName, object? value)
    {
        var copy = new Dictionary<string, object?>(existing);
        copy[fieldName] = value;
        return copy;
    }

    private static ToolInvokeResponse Annotate(ToolInvokeResponse failed, string trail) => new()
    {
        ToolCallId = failed.ToolCallId,
        ToolName = failed.ToolName,
        Content = $"{failed.Content}\n[recovery-trail] {trail}",
        ContentBlocks = failed.ContentBlocks,
        IsError = true
    };

    private static string Truncate(string? s, int max = 240)
    {
        if (string.IsNullOrEmpty(s)) return "(no content)";
        return s.Length <= max ? s : s[..max] + "…";
    }

    private static void RecordTelemetry(
        string server, string tool, string field, string stage,
        bool recovered, string provider, double durationMs)
    {
        var outcome = recovered ? "recovered" : "failed";
        var tags = new TagList
        {
            { "server", server },
            { "tool", tool },
            { "field", field },
            { "stage", stage },
            { "outcome", outcome },
            { "provider", provider }
        };

        RecoveryDiagnostics.Attempts.Add(1, tags);
        RecoveryDiagnostics.Duration.Record(durationMs, tags);
    }

    /// <summary>
    /// Phase 5 producer side: records a post-recovery failure into the cluster store
    /// so DreamService can spot recurring (server, tool, error-class) patterns and
    /// open repair tickets. Auto-recovered calls are not recorded — those are
    /// covered by <see cref="RecoveryDiagnostics"/> metrics. Error class is the
    /// missing field name from <see cref="SchemaErrorPatterns"/>, or
    /// <c>"unknown"</c> if no pattern matches. Best-effort; failures here never
    /// break the recovery path.
    /// </summary>
    private async Task RecordFailureAsync(
        string serverName,
        string toolName,
        string? sessionId,
        string? errorText,
        string? fieldHint,
        CancellationToken ct)
    {
        if (_failureClusterStore is null) return;

        var errorClass = !string.IsNullOrEmpty(fieldHint)
            ? fieldHint
            : ExtractErrorClass(errorText);

        ClusterKey key;
        try
        {
            key = new ClusterKey(serverName, toolName, errorClass);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex,
                "Skipping failure cluster record for {Server}/{Tool} — invalid key components",
                serverName, toolName);
            return;
        }

        try
        {
            await _failureClusterStore.RecordAsync(
                key, sessionId, errorText ?? string.Empty, DateTimeOffset.UtcNow, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to record failure cluster for {Server}/{Tool} class {ErrorClass}",
                serverName, toolName, errorClass);
        }
    }

    private static string ExtractErrorClass(string? errorText) =>
        SchemaErrorPatterns.TryExtractMissingField(errorText, out var fieldName) ? fieldName : "unknown";

    /// <summary>
    /// Phase 2 producer side: when recovery has been attempted but exhausted, write
    /// a falsifiable capability claim against (server, tool, current arguments).
    /// The verify shape replays the same call expecting success — so the next
    /// session that pulls the claim will evict it automatically once the underlying
    /// problem is fixed (e.g. a future provider lands that fills the missing field,
    /// or the MCP server starts accepting the call). Best-effort; failures here
    /// never break the recovery path.
    /// </summary>
    private async Task EmitCapabilityClaimAsync(
        string serverName,
        string toolName,
        ToolInvokeRequest innerRequest,
        string statement,
        string? evidence,
        CancellationToken ct)
    {
        if (_capabilityClaimWriter is null) return;

        JsonElement argsElement;
        try
        {
            using var doc = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(innerRequest.Arguments) ? "{}" : innerRequest.Arguments!);
            argsElement = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Arguments aren't JSON we can replay — record a claim with empty args
            // rather than skipping; the statement still names server/tool.
            using var fallback = JsonDocument.Parse("{}");
            argsElement = fallback.RootElement.Clone();
        }

        var verify = new VerifyShape(
            Server: serverName,
            Tool: toolName,
            Arguments: argsElement,
            Expect: new VerifyExpectation(VerifyExpectationKind.Success));

        var claim = new CapabilityClaim(
            Server: serverName,
            Tool: toolName,
            Statement: statement,
            Verify: verify,
            Evidence: evidence is null ? [] : [Truncate(evidence, 512)],
            CreatedAt: DateTimeOffset.UtcNow);

        try
        {
            await _capabilityClaimWriter.SaveCapabilityClaimAsync(claim, ct);
            _logger.LogInformation(
                "Saved capability claim after exhausted recovery for {Server}/{Tool}: {Statement}",
                serverName, toolName, statement);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to save capability claim after exhausted recovery for {Server}/{Tool}",
                serverName, toolName);
        }
    }
}
