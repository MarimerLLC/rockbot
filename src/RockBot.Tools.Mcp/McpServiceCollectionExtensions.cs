using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RockBot.Messaging;
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
    /// Registers stdio MCP tool servers for direct in-process execution, without the bridge.
    /// Not the production path: the agent uses <see cref="AddMcpToolProxy"/> with the hosted bridge.
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
    /// the handler registers the 8 management tools in <see cref="IToolRegistry"/>,
    /// plus a typed <c>{server}__{tool}</c> tool per downstream tool when some tier's wrapper mode
    /// (<see cref="McpToolSurfaceOptions.ModeFor"/>) is <see cref="McpWrapperMode.Eager"/>, or
    /// <c>mcp_find_tools</c> and per-session activation when one is <see cref="McpWrapperMode.Lazy"/>
    /// or <see cref="McpWrapperMode.Pinned"/>.
    /// </summary>
    public static AgentHostBuilder AddMcpToolProxy(
        this AgentHostBuilder builder,
        TimeSpan? requestTimeout = null,
        TimeSpan? responseTimeout = null)
    {
        var agentName = builder.Identity.Name;

        var effectiveRequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(60);
        var effectiveResponseTimeout = responseTimeout ?? effectiveRequestTimeout;
        builder.Services.AddSingleton(sp =>
                new McpToolProxy(
                    sp.GetRequiredService<IMessagePublisher>(),
                    sp.GetRequiredService<IMessageSubscriber>(),
                    sp.GetRequiredService<AgentIdentity>(),
                    sp.GetRequiredService<ILogger<McpToolProxy>>(),
                    effectiveRequestTimeout,
                    effectiveResponseTimeout));

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

        // Self-repair Amendment 1: schema-error enrichment. The cache populates lazily
        // through McpManagementExecutor.GetSurfaceAsync — the Func factory defers DI
        // resolution to call time so the executor → recovery → enricher → cache cycle
        // resolves without DI complaining. It keeps each server's prompts too, for the typed
        // prompt tools (#616).
        builder.Services.AddSingleton(sp => ToolSchemaCache.WithPrompts(
            (server, ct) => sp.GetRequiredService<McpManagementExecutor>().GetSurfaceAsync(server, ct)));
        builder.Services.AddSingleton<SchemaErrorEnricher>();

        // Skill freshness (#615): baselines for mcp/{server} skills and the stale marker. The
        // interface lives in Host.Abstractions so skill tools, repair tickets and the dream service
        // can use it without referencing this project.
        builder.Services.AddSingleton<IMcpSkillSurface>(sp => new McpSkillSurface(
            sp.GetRequiredService<McpServerIndex>(), sp.GetRequiredService<ToolSchemaCache>()));

        builder.Services.AddSingleton<McpRecoveryExecutor>();

        // Pre-flight recovery: same providers + enricher exposed via a small abstraction
        // so wisp Direct MCP steps can resolve environmental defaults and surface enriched
        // errors before invoking the tool, without RockBot.Wisp taking a hard dependency
        // on RockBot.Tools.Mcp.
        builder.Services.AddSingleton<IMcpPreflightRecovery, McpPreflightRecovery>();

        // Typed {server}__{tool} tools (#420), eager or lazy (#612). Off unless McpBridge:WrapperMode
        // says otherwise. The surface is what the agent loop sees: it carries each session's lazy
        // activations into the run's tool list.
        builder.Services.AddOptions<McpToolSurfaceOptions>();
        builder.Services.AddSingleton<McpTypedToolSurface>();
        builder.Services.AddSingleton<ITypedToolSurface>(sp => sp.GetRequiredService<McpTypedToolSurface>());
        builder.Services.AddSingleton<McpWrapperCatalog>();

        // Typed tools by server and tool, or by typed name, for code that can't see the catalog —
        // wisps above all (#647): in pinned and lazy modes no wrapper is in the registry.
        builder.Services.AddSingleton<IMcpToolDirectory>(sp => new McpToolDirectory(
            sp.GetRequiredService<McpWrapperCatalog>(), sp.GetRequiredService<McpServerIndex>()));

        builder.HandleMessage<McpServersIndexed, McpServersIndexedHandler>();
        builder.SubscribeTo($"tool.meta.mcp.{agentName}");

        // Note: mcp.manage.response.{agentName} is subscribed directly by
        // McpManagementExecutor (lazy, on first management call) — not via the pipeline.

        return builder;
    }
}
