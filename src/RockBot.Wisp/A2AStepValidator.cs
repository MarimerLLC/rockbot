namespace RockBot.Wisp;

/// <summary>
/// Validates A2A wisp step definitions for semantic incompatibilities that the
/// JSON-schema layer in <see cref="WispToolRegistrar"/> can't express. Kept
/// narrow on purpose — today it only rejects <c>output_to</c> on A2A steps
/// (A2A results are asynchronous; <c>output_to</c> would capture the dispatch
/// stub, not the real result, and a downstream reader would see garbage and
/// fail the wisp — which is how duplicate agent executions were happening in
/// practice).
/// </summary>
internal static class A2AStepValidator
{
    public static WispStepError? Validate(WispStep step)
    {
        if (step.Gateway != GatewayType.A2A)
            return null;

        if (!string.IsNullOrEmpty(step.OutputTo))
        {
            return new WispStepError
            {
                Category = FailureCategory.Structural,
                Message =
                    "A2A steps cannot use 'output_to'. invoke_agent returns synchronously " +
                    "with a dispatch acknowledgement, not the real agent result (which " +
                    "arrives asynchronously on the message bus). Writing that stub to a " +
                    "shared-volume file and reading it back in a later step will produce " +
                    "garbage and fail the wisp — leaving the dispatched A2A task running " +
                    "on the remote side. Remove 'output_to' from this step; downstream steps " +
                    "should consume the result after it arrives (e.g. via a follow-up wisp " +
                    "keyed on the task_id) rather than reading a file.",
                ToolName = "invoke_agent"
            };
        }

        return null;
    }
}
