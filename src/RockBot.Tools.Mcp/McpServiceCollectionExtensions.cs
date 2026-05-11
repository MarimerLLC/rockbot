using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using RockBot.Host;
using RockBot.Tools;
using RockBot.Tools.Mcp.Recovery;
using RockBot.Tools.Mcp.Recovery.Providers;

namespace RockBot.Tools.Mcp;

/// <summary>
/// DI registration extensions for MCP tool backends.
/// </summary>
public static class McpServiceCollectionExtensions
{
    /// <summary>
    /// Registers MCP tool servers for in-process execution (used by the MCP Bridge process).
    /// </summary>
    public static AgentHostBuilder AddMcpTools(
        this AgentHostBuilder builder,
        Action<McpOptions> configure)
    {
        var options = new McpOptions();
        configure(options);
        builder.Services.AddSingleton(options);

        builder.Services.AddHostedService<McpToolRegistrar>();

        return builder;
    }

    /// <summary>
    /// Registers the MCP management proxy for agents that interact with MCP servers via
    /// the message bus. On startup the bridge sends <see cref="McpServersIndexed"/>;
    /// the handler registers exactly 5 management tools in <see cref="IToolRegistry"/>
    /// instead of one tool per schema.
    /// </summary>
    public static AgentHostBuilder AddMcpToolProxy(this AgentHostBuilder builder)
    {
        var agentName = builder.Identity.Name;

        builder.Services.AddSingleton<McpToolProxy>();
        builder.Services.AddSingleton<McpServerIndex>();
        builder.Services.AddSingleton<McpManagementExecutor>();
        builder.Services.AddHostedService<McpStartupProbeService>();
        builder.Services.AddHostedService<McpSkillNameMigrationService>();
        builder.Services.AddSingleton<IToolSkillProvider, McpToolSkillProvider>();

        // Self-repair Phase 1: mechanical recovery for missing required parameters.
        // See design/self-repair.md.
        builder.Services.AddSingleton<McpInvokeDelegate>(sp =>
        {
            var proxy = sp.GetRequiredService<McpToolProxy>();
            return (req, headers, ct) => proxy.ExecuteAsync(req, headers, ct);
        });
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IToolArgumentDefaultsProvider, TimeZoneDefaultProvider>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IToolArgumentDefaultsProvider, CurrentTimeDefaultProvider>());
        // Self-repair Phase 4: file-backed defaults registered by repair tickets.
        // Registered after the deterministic providers so hard-coded resolution wins
        // when both can answer; the file-backed provider augments rather than overrides.
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IToolArgumentDefaultsProvider, FileToolDefaultsProvider>());
        builder.Services.AddSingleton<StageBLlmFiller>();

        // Self-repair Amendment 1: schema-error enrichment. The cache populates lazily
        // through McpManagementExecutor.GetSchemasAsync — the Func factory defers DI
        // resolution to call time so the executor → recovery → enricher → cache cycle
        // resolves without DI complaining.
        builder.Services.AddSingleton(sp => new ToolSchemaCache(
            (server, ct) => sp.GetRequiredService<McpManagementExecutor>().GetSchemasAsync(server, ct)));
        builder.Services.AddSingleton<SchemaErrorEnricher>();

        builder.Services.AddSingleton<McpRecoveryExecutor>();

        builder.HandleMessage<McpServersIndexed, McpServersIndexedHandler>();
        builder.SubscribeTo($"tool.meta.mcp.{agentName}");

        // Note: mcp.manage.response.{agentName} is subscribed directly by
        // McpManagementExecutor (lazy, on first management call) — not via the pipeline.

        return builder;
    }
}
