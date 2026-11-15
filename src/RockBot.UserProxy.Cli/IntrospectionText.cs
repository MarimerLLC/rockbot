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

    private static string Money(double value) =>
        value.ToString(value < 0.1 && value != 0 ? "0.000" : "0.00", CultureInfo.InvariantCulture);

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
