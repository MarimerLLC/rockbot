namespace RockBot.Tools.Web;

/// <summary>
/// Thrown by an <see cref="IWebSearchProvider"/> when web search cannot run because its API
/// credentials are not configured. Lets callers distinguish a configuration gap — which should
/// be surfaced to the user as an actionable error — from a search that ran and found nothing.
/// </summary>
public sealed class WebSearchNotConfiguredException(string message) : InvalidOperationException(message);
