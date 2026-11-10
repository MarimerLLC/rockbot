using System.Text.RegularExpressions;

namespace RockBot.Wisp;

/// <summary>
/// Structural checks for <c>for_each</c> steps (#779) that the JSON schema in
/// <see cref="WispToolRegistrar"/> can't express. Every failure is
/// <see cref="FailureCategory.Structural"/>: an authoring mistake that no amount of retrying the
/// same definition fixes. Runs before the step does any work, like <see cref="A2AStepValidator"/>.
/// </summary>
internal static partial class ForEachStepValidator
{
    private static readonly HashSet<string> ReservedAliases = new(StringComparer.Ordinal)
    {
        "steps", GatewayRouter.ItemIndexName
    };

    /// <param name="step">The step about to run.</param>
    /// <param name="index">Its position in <paramref name="definition"/>.</param>
    /// <param name="definition">The wisp, so a <c>for_each</c> source can be checked to be an
    /// earlier step.</param>
    public static WispStepError? Validate(WispStep step, int index, WispDefinition definition)
    {
        if (!step.IsIterated)
            return ValidateNonIterated(step);

        if (step.Mode != StepMode.Direct)
            return Error("'for_each' is only supported on Direct steps. An LLM step can't be fanned out; " +
                         "make the per-item call a Direct step, or let one LLM step handle the whole list.");

        if (!GatewayRouter.TryParseForEachSource(step.ForEach, out var sourceId, out _))
            return Error($"'for_each' must be a single {{{{steps.<id>.result}}}} or {{{{steps.<id>.result.<path>}}}} " +
                         $"template naming the array to iterate; got '{step.ForEach}'.");

        var sourceIndex = -1;
        for (var i = 0; i < definition.Steps.Count; i++)
        {
            if (string.Equals(definition.Steps[i].Id, sourceId, StringComparison.OrdinalIgnoreCase))
            {
                sourceIndex = i;
                break;
            }
        }

        if (sourceIndex < 0)
            return Error($"'for_each' references step '{sourceId}', which is not a step in this wisp.");
        if (sourceIndex >= index)
            return Error($"'for_each' references step '{sourceId}', which does not run before step '{step.Id}'. " +
                         "The source must be an earlier step.");

        if (step.As is { } alias)
        {
            if (!IdentifierPattern().IsMatch(alias))
                return Error($"'as' must be a simple name (letters, digits, underscore; not starting with a digit); got '{alias}'.");
            if (ReservedAliases.Contains(alias))
                return Error($"'as' cannot be '{alias}', which is a reserved template name.");
        }

        if (step.OnItemFailure is { } onItemFailure
            && !string.Equals(onItemFailure, "continue", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(onItemFailure, "abort", StringComparison.OrdinalIgnoreCase))
            return Error($"'on_item_failure' must be 'continue' or 'abort'; got '{onItemFailure}'.");

        if (step.MaxItems is < 1)
            return Error($"'max_items' must be at least 1; got {step.MaxItems}.");
        if (step.Concurrency is < 1)
            return Error($"'concurrency' must be at least 1; got {step.Concurrency}.");

        return null;
    }

    // A step that doesn't iterate must not carry the iteration settings or reference an element:
    // {{item}} would otherwise reach the tool as literal text.
    private static WispStepError? ValidateNonIterated(WispStep step)
    {
        var stray = new List<string>();
        if (step.As is not null) stray.Add("as");
        if (step.MaxItems is not null) stray.Add("max_items");
        if (step.Concurrency is not null) stray.Add("concurrency");
        if (step.OnItemFailure is not null) stray.Add("on_item_failure");
        if (stray.Count > 0)
            return Error($"Step '{step.Id}' sets {string.Join(", ", stray)} but has no 'for_each'. " +
                         "Add 'for_each' naming the array to iterate, or remove them.");

        if (ReferencesItem(step))
            return Error($"Step '{step.Id}' uses {{{{item}}}} or {{{{item_index}}}} but has no 'for_each', so there is no " +
                         "current item. Add 'for_each' naming the array to iterate over.");

        return null;
    }

    private static bool ReferencesItem(WispStep step)
    {
        // A script's source may legitimately hold {{item}} for its own template engine (Jinja,
        // Handlebars), so a script step without for_each is left alone.
        if (step.Gateway == GatewayType.Script)
            return false;

        foreach (var text in new[] { step.ResolvedParams?.GetRawText(), step.Message })
        {
            if (text is not null && ItemReferencePattern().IsMatch(text))
                return true;
        }
        return false;
    }

    private static WispStepError Error(string message) => new()
    {
        Category = FailureCategory.Structural,
        Message = message
    };

    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_]*$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"\{\{item(?:_index)?(?:\.[a-zA-Z0-9_\-.]+)?\}\}")]
    private static partial Regex ItemReferencePattern();
}
