namespace RockBot.Llm;

/// <summary>
/// Controls how a <see cref="ModelBehavior"/> presents results at the end of a
/// scheduled-task execution.
/// </summary>
public enum ScheduledTaskResultMode
{
    /// <summary>
    /// Default. The model writes a natural-language summary of what it did and what
    /// the outcome was. Raw tool output is not included in the reply.
    /// </summary>
    Summarize,

    /// <summary>
    /// The model includes the complete, verbatim output from every tool it called.
    /// Useful for models that tend to paraphrase results rather than show them, or
    /// when the user wants to see exact script output, email bodies, etc.
    /// </summary>
    VerbatimOutput,

    /// <summary>
    /// The model produces a brief natural-language summary followed by the complete
    /// raw output from every tool it called. Combines readability with transparency.
    /// </summary>
    SummarizeWithOutput,
}
