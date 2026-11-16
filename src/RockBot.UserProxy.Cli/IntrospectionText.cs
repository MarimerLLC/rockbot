using System.Globalization;

namespace RockBot.UserProxy.Cli;

/// <summary>
/// Plain-text rendering of an <see cref="AgentIntrospectionResponse"/> for <c>rockbot info</c>
/// (#834). Plain lines, no markup, so the output reads the same in a terminal and in a pipe.
/// </summary>
internal static class IntrospectionText
{
    public static void Write(TextWriter output, AgentIntrospectionResponse response, IReadOnlyList<string> sections)
    {
        var header = $"{response.AgentName} {response.AgentVersion}";
        if (response.StartedAt is { } started)
            header += $" (started {Timestamp(started)})";
        output.WriteLine(header);

        var wanted = sections.Count > 0 ? sections : IntrospectionSections.All;
        foreach (var section in wanted)
        {
            output.WriteLine();
            switch (section.ToLowerInvariant())
            {
                case IntrospectionSections.Models:
                    WriteModels(output, response.Models);
                    break;
                case IntrospectionSections.Pricing:
                    WritePricing(output, response.Pricing);
                    break;
                case IntrospectionSections.Usage:
                    WriteUsage(output, response.Usage);
                    break;
                case IntrospectionSections.Mcp:
                    WriteMcp(output, response.Mcp);
                    break;
                case IntrospectionSections.Skills:
                    WriteSkills(output, response.Skills);
                    break;
                case IntrospectionSections.Memory:
                    WriteMemory(output, response.Memory);
                    break;
                case IntrospectionSections.Routing:
                    WriteRouting(output, response.Routing);
                    break;
            }
        }
    }

    private static void WriteModels(TextWriter output, ModelsIntrospection? models)
    {
        if (models is null)
        {
            output.WriteLine("Models: (the agent did not report its models)");
            return;
        }

        output.WriteLine($"Models (loaded {Timestamp(models.LoadedAt)}; a change needs a restart)");
        if (models.Tiers.Count == 0)
            output.WriteLine($"  {models.Note ?? "No LLM tiers are configured."}");

        var tierWidth = models.Tiers.Count == 0 ? 0 : models.Tiers.Max(t => t.Tier.Length);
        foreach (var tier in models.Tiers)
        {
            var label = tier.Tier.PadRight(tierWidth);
            if (tier.UsesTier is not null)
            {
                output.WriteLine($"  {label}  (no model of its own; uses the {tier.UsesTier} model)");
                continue;
            }

            for (var i = 0; i < tier.Models.Count; i++)
            {
                var model = tier.Models[i];
                var prefix = i == 0 ? label : new string(' ', tierWidth);
                var order = tier.Models.Count > 1 ? $"{i + 1}. " : "";
                output.WriteLine($"  {prefix}  {order}{Describe(model, tier)}");
            }
        }

        if (models.Tiers.Any(t => t.Models.Count > 1 && t.UsesTier is null))
            output.WriteLine("  A numbered list is a fallback chain: each later model is used only when the ones before it fail.");
        if (models.FixedTier is not null)
            output.WriteLine($"  Every request is pinned to the {models.FixedTier} tier (LLM:FixedTier).");

        output.WriteLine(models.Embedding is { } embedding
            ? $"  Embeddings: {embedding.ModelId} ({embedding.Service})"
            : "  Embeddings: none (keyword search only)");
        output.WriteLine(models.DecisionModel is { } decision
            ? $"  {decision.Summary}"
            : "  Decision model: none (checks use their deterministic rules)");
    }

    private static string Describe(ModelIntrospection model, TierIntrospection tier)
    {
        var parts = new List<string> { model.Service };
        if (model.EndpointHost is { } host && !string.Equals(host, model.Service, StringComparison.OrdinalIgnoreCase))
            parts.Add(host);
        if (model.ReasoningEffort is { } effort)
            parts.Add($"reasoning {effort}");
        if (tier.AcceptsImages)
            parts.Add(model.SupportsImageInput ? "images" : "text only");

        var text = $"{model.ModelId} ({string.Join(", ", parts)})";
        return model.Price is { } price
            ? $"{text}  ${Money(price.InputPerM)} in / ${Money(price.OutputPerM)} out per M"
            : $"{text}  (no pricing row; costed at $0)";
    }

    private static void WritePricing(TextWriter output, PricingIntrospection? pricing)
    {
        if (pricing is null)
        {
            output.WriteLine("Pricing: (the agent did not report its pricing table)");
            return;
        }

        output.WriteLine(pricing.LoadedFrom is { } path
            ? $"Pricing (from {path}), USD per million tokens"
            : "Pricing (the agent's built-in fallback table; no pricing file loaded), USD per million tokens");

        var width = Math.Max("prefix".Length, pricing.Rows.Count == 0 ? 0 : pricing.Rows.Max(r => r.Prefix.Length));
        output.WriteLine($"  {"prefix".PadRight(width)}  {"input",8}  {"output",8}  {"cached",8}");
        foreach (var row in pricing.Rows)
        {
            var cached = row.CachedInputPerM is { } c ? Money(c) : "-";
            output.WriteLine($"  {row.Prefix.PadRight(width)}  {Money(row.InputPerM),8}  {Money(row.OutputPerM),8}  {cached,8}");
        }
        output.WriteLine("  A model is priced by the first row (longest prefix) its id contains. Cache writes default to 1.25x (5 min) and 2x (1 h) input.");
    }

    private static void WriteUsage(TextWriter output, UsageIntrospection? usage)
    {
        if (usage is null)
        {
            output.WriteLine("Usage: (the agent did not report its usage)");
            return;
        }

        output.WriteLine($"Usage (estimated from the pricing table; days in {usage.TimeZone}" +
                         (usage.Since is { } since ? $"; counted since {Timestamp(since)})" : ")"));

        var rows = new List<(string Label, UsageTotalsIntrospection Totals)>
        {
            ($"Today ({usage.Today})", usage.TodayTotals),
            ("Last 7 days", usage.Last7Days),
            ("Last 30 days", usage.Last30Days),
        };
        if (usage.Thread is { } thread)
            rows.Add(($"This session ({thread.ThreadId})", thread.Totals));
        WriteTotalsTable(output, "", rows);

        if (usage.Models.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("  By model, last 30 days");
            WriteTotalsTable(output, "  ", [.. usage.Models.Select(m => (m.ModelId, m.Totals))]);
        }

        if (usage.Kinds.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("  By workload, last 30 days");
            WriteTotalsTable(output, "  ", [.. usage.Kinds.Select(k => (KindLabel(k.Kind), k.Totals))]);
        }

        var unpriced = usage.Last30Days.UnpricedRequests;
        if (unpriced > 0)
            output.WriteLine($"  {unpriced} request(s) in the last 30 days matched no pricing row and are counted at $0.");
    }

    private static void WriteTotalsTable(TextWriter output, string indent, IReadOnlyList<(string Label, UsageTotalsIntrospection Totals)> rows)
    {
        var width = Math.Max(5, rows.Max(r => r.Label.Length));
        output.WriteLine($"  {indent}{"".PadRight(width)}  {"requests",8}  {"input",10}  {"cached",10}  {"output",10}  {"cost",10}");
        foreach (var (label, t) in rows)
        {
            output.WriteLine($"  {indent}{label.PadRight(width)}  {t.Requests,8:N0}  {Tokens(t.InputTokens),10}  {Tokens(t.CachedInputTokens),10}  " +
                             $"{Tokens(t.OutputTokens),10}  {Cost(t.CostUsd),10}");
        }
    }

    private static void WriteMcp(TextWriter output, McpIntrospection? mcp)
    {
        if (mcp is null)
        {
            output.WriteLine("MCP servers: (the agent did not report them)");
            return;
        }

        var connected = mcp.Servers.Count(s => s.State == "connected");
        output.WriteLine($"MCP servers ({connected} of {mcp.Servers.Count} connected)");
        if (mcp.Servers.Count == 0)
        {
            output.WriteLine("  None configured.");
            return;
        }

        var width = mcp.Servers.Max(s => s.Name.Length);
        var stateWidth = mcp.Servers.Max(s => s.State.Length);
        foreach (var s in mcp.Servers)
        {
            var where = s.Host ?? s.Transport;
            var detail = s.State == "connected"
                ? $"{s.ToolCount} tools" + (s.PromptCount > 0 ? $", {s.PromptCount} prompts" : "") + (s.Version is { } v ? $", v{v}" : "")
                : s.LastError ?? "";
            output.WriteLine($"  {s.Name.PadRight(width)}  {s.State.PadRight(stateWidth)}  {where}{(s.UsesOAuth ? " (OAuth)" : "")}{(detail.Length > 0 ? "  " + detail : "")}");
        }
    }

    private static void WriteSkills(TextWriter output, SkillsIntrospection? skills)
    {
        if (skills is null)
        {
            output.WriteLine("Skills: (the agent did not report them)");
            return;
        }

        output.WriteLine($"Skills ({skills.Count}: {skills.Learned} learned, {skills.Starter} starter, {skills.FromMcp} from MCP servers)");
        foreach (var s in skills.Skills.OrderByDescending(s => s.LastUsedAt ?? DateTimeOffset.MinValue).ThenBy(s => s.Name, StringComparer.Ordinal))
        {
            var used = s.LastUsedAt is { } at ? $"used {at.ToUniversalTime():yyyy-MM-dd}" : "never used";
            var summary = s.Summary.Length > 90 ? s.Summary[..87] + "..." : s.Summary;
            output.WriteLine($"  {s.Name}  [{s.Source}, {used}]{(summary.Length > 0 ? "  " + summary : "")}");
        }
    }

    private static void WriteMemory(TextWriter output, MemoryIntrospection? memory)
    {
        if (memory is null)
        {
            output.WriteLine("Memory: (the agent did not report its memory health)");
            return;
        }

        if (memory.AuditedAt is not { } audited)
        {
            output.WriteLine("Memory: no audit has run yet.");
        }
        else
        {
            output.WriteLine($"Memory ({memory.Status ?? "unknown"}; audited {Timestamp(audited)})");
            output.WriteLine($"  {memory.Live:N0} live, {memory.Archived:N0} archived; since the audit before: +{memory.CreatedSinceLast} created, {memory.ArchivedSinceLast} archived, {memory.HardDeletedSinceLast} deleted" +
                             (memory.NetGrowthPerDay is { } g ? $" ({g.ToString("+0.0;-0.0;0", CultureInfo.InvariantCulture)}/day)" : ""));
            output.WriteLine($"  {memory.NearDuplicatePairs} near-duplicate pair(s); {memory.PurgeDueCount} due for purge within {memory.PurgeDueWithinDays} day(s)");
            if (memory.EvalSoundRate is { } rate)
                output.WriteLine($"  Sampled memories judged sound: {rate:P0}" + (memory.EvaluatedAt is { } e ? $" (evaluated {Timestamp(e)})" : ""));
            foreach (var f in memory.Findings)
                output.WriteLine($"  ! {f.Name}: {f.Message}");
        }

        if (memory.ConsolidationPaused)
            output.WriteLine($"  Consolidation is PAUSED{(memory.ConsolidationPausedReason is { } r ? ": " + r : "")}");
        else if (memory.ConsolidationLastRunAt is { } last)
            output.WriteLine($"  Consolidation last ran {Timestamp(last)}");
    }

    private static void WriteRouting(TextWriter output, RoutingIntrospection? routing)
    {
        if (routing is null)
        {
            output.WriteLine("Routing: (the agent did not report it)");
            return;
        }

        output.WriteLine($"Routing (last {routing.WindowDays} days: {routing.Total:N0} requests, {routing.UserMessages:N0} user messages, {routing.Subagents:N0} subagent runs)");
        if (routing.Total == 0)
        {
            output.WriteLine("  Nothing routed in the window.");
            return;
        }

        output.WriteLine($"  {"tier",-9}  {"requests",8}  {"share",6}  {"latency",8}  {"in",8}  {"out",8}  {"tools",6}");
        foreach (var t in routing.Tiers)
        {
            output.WriteLine($"  {t.Tier,-9}  {t.Count,8:N0}  {t.Pct.ToString("0.0", CultureInfo.InvariantCulture) + "%",6}  " +
                             $"{(t.AvgLatencyMs is { } ms ? (ms / 1000d).ToString("0.0", CultureInfo.InvariantCulture) + "s" : "-"),8}  " +
                             $"{(t.AvgInputTokens is { } i ? Tokens(i) : "-"),8}  {(t.AvgOutputTokens is { } o ? Tokens(o) : "-"),8}  " +
                             $"{t.AvgToolCalls.ToString("0.0", CultureInfo.InvariantCulture),6}");
        }
        output.WriteLine($"  Averages per request. {routing.Fallbacks:N0} request(s) ({routing.FallbackPct.ToString("0.0", CultureInfo.InvariantCulture)}%) were answered by a fallback model.");
    }

    /// <summary>An estimated cost: "$12.34", "<$0.01" for a sliver, "$0.00" for none.</summary>
    internal static string Cost(double usd) =>
        usd <= 0 ? "$0.00" : usd < 0.01 ? "<$0.01" : "$" + usd.ToString("#,##0.00", CultureInfo.InvariantCulture);

    /// <summary>A workload kind in words.</summary>
    internal static string KindLabel(string kind) => kind switch
    {
        "session" => "Conversations",
        "subagent" => "Subagents",
        "worker" => "Workers",
        "patrol" => "Scheduled tasks",
        "background" => "Background (dreams, memory audits)",
        _ => "Other",
    };

    /// <summary>A token count, compact: 950, 12.3K, 4.56M.</summary>
    internal static string Tokens(long count) => count switch
    {
        >= 1_000_000 => (count / 1_000_000d).ToString("0.00", CultureInfo.InvariantCulture) + "M",
        >= 10_000 => (count / 1_000d).ToString("0.0", CultureInfo.InvariantCulture) + "K",
        _ => count.ToString("N0", CultureInfo.InvariantCulture),
    };

    private static string Money(double value) =>
        value.ToString(value < 0.1 && value != 0 ? "0.000" : "0.00", CultureInfo.InvariantCulture);

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
