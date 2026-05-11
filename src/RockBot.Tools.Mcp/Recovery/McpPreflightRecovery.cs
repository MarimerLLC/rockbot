using System.Text;
using Microsoft.Extensions.Logging;

namespace RockBot.Tools.Mcp.Recovery;

/// <summary>
/// Default implementation of <see cref="IMcpPreflightRecovery"/>. Iterates the
/// registered <see cref="IToolArgumentDefaultsProvider"/> set (same providers
/// used by the post-flight <see cref="McpRecoveryExecutor"/>) to silently fill
/// environmental defaults, and delegates per-field enrichment to
/// <see cref="SchemaErrorEnricher"/> for fields no provider could resolve.
/// This keeps wisp Direct MCP authoring failures on the same recovery rails as
/// LLM-orchestrated calls — see <c>design/self-repair.md</c> Amendment 1 and
/// the wisp pre-flight gap.
/// </summary>
public sealed class McpPreflightRecovery(
    IEnumerable<IToolArgumentDefaultsProvider> providers,
    SchemaErrorEnricher? enricher,
    ILogger<McpPreflightRecovery> logger,
    ToolSchemaCache? schemas = null) : IMcpPreflightRecovery
{
    private readonly IReadOnlyList<IToolArgumentDefaultsProvider> _providers = providers.ToList();

    public async Task<string?> TryGetParametersSchemaAsync(
        string serverName, string toolName, CancellationToken ct)
    {
        if (schemas is null) return null;
        try
        {
            var def = await schemas.GetAsync(serverName, toolName, ct);
            return def?.ParametersSchema;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Pre-flight schema lookup failed for {Server}/{Tool}; falling back to no validation",
                serverName, toolName);
            return null;
        }
    }

    public async Task<PreflightRecoveryResult> TryRecoverAsync(
        string serverName,
        string toolName,
        IReadOnlyList<string> missingFields,
        IReadOnlyDictionary<string, object?> existingArgs,
        string? parentSessionId,
        CancellationToken ct)
    {
        if (missingFields.Count == 0)
            return new PreflightRecoveryResult(
                new Dictionary<string, object?>(StringComparer.Ordinal), [], null);

        var filled = new Dictionary<string, object?>(StringComparer.Ordinal);
        var unresolved = new List<string>();

        foreach (var fieldName in missingFields)
        {
            var value = await TryResolveAsync(serverName, toolName, fieldName, existingArgs, ct);
            if (value is not null)
                filled[fieldName] = value.Value;
            else
                unresolved.Add(fieldName);
        }

        string? enriched = null;
        if (unresolved.Count > 0 && enricher is not null)
        {
            var sb = new StringBuilder();
            foreach (var field in unresolved)
            {
                try
                {
                    var fragment = await enricher.EnrichAsync(
                        serverName, toolName, field, parentSessionId,
                        originalError: $"Missing required field '{field}' for {serverName}/{toolName}.",
                        ct);
                    if (sb.Length > 0) sb.AppendLine();
                    sb.AppendLine(fragment);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Pre-flight enrichment failed for {Server}/{Tool} field {Field}",
                        serverName, toolName, field);
                }
            }
            if (sb.Length > 0)
                enriched = sb.ToString().TrimEnd();
        }

        return new PreflightRecoveryResult(filled, unresolved, enriched);
    }

    private async Task<ResolvedDefault?> TryResolveAsync(
        string serverName, string toolName, string fieldName,
        IReadOnlyDictionary<string, object?> existingArgs, CancellationToken ct)
    {
        var ctx = new ResolveContext(serverName, toolName, fieldName, existingArgs);
        foreach (var provider in _providers)
        {
            if (!provider.CanResolve(serverName, toolName, fieldName)) continue;

            try
            {
                var resolved = await provider.ResolveAsync(ctx, ct);
                if (resolved is not null)
                    return resolved;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Pre-flight default provider {Provider} threw for {Server}/{Tool} field {Field}",
                    provider.GetType().Name, serverName, toolName, fieldName);
            }
        }
        return null;
    }
}
