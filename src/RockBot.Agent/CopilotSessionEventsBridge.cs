using RockBot.Host;
using RockBot.Llm.Copilot;

namespace RockBot.Agent;

/// <summary>
/// Bridges <see cref="ICopilotSessionEvents"/> to <see cref="IToolProgressNotifier"/>
/// so that tool-call progress from Copilot sessions is surfaced to the user's UI.
/// Uses deferred resolution: the <see cref="IServiceProvider"/> is set after the host
/// is built, since Copilot clients are constructed during startup before DI is ready.
/// </summary>
internal sealed class CopilotSessionEventsBridge : ICopilotSessionEvents
{
    private IServiceProvider? _serviceProvider;

    /// <summary>
    /// Sets the service provider after the host is built. Must be called before
    /// any tool events fire (safe because sessions aren't created until messages arrive).
    /// </summary>
    public void SetServiceProvider(IServiceProvider sp) => _serviceProvider = sp;

    public Task OnToolStartAsync(string toolName, string? arguments, CancellationToken ct)
    {
        var notifier = _serviceProvider?.GetService(typeof(IToolProgressNotifier)) as IToolProgressNotifier;
        return notifier?.OnToolInvokingAsync(toolName, arguments, ct) ?? Task.CompletedTask;
    }

    public Task OnToolCompleteAsync(string toolName, bool success, string? resultSummary, CancellationToken ct)
    {
        var notifier = _serviceProvider?.GetService(typeof(IToolProgressNotifier)) as IToolProgressNotifier;
        return notifier?.OnToolInvokedAsync(toolName, resultSummary, ct) ?? Task.CompletedTask;
    }
}
