namespace RockBot.Host;

/// <summary>
/// Represents the cost/quality tier to use for an LLM call.
/// Low is cheapest and fastest; High is most capable but most expensive.
/// </summary>
public enum ModelTier { Low, Balanced, High }
