namespace RockBot.A2A;

/// <summary>
/// Sentinel type used to prevent double-registration of <see cref="AgentDirectory"/>
/// as <see cref="Microsoft.Extensions.Hosting.IHostedService"/> when both
/// <c>AddA2A()</c> and <c>AddA2ACaller()</c> are called on the same host.
/// </summary>
internal sealed class AgentDirectoryHostedServiceMarker;
