using System.ComponentModel;
using Spectre.Console.Cli;

namespace RockBot.UserProxy.Cli;

/// <summary>
/// Connection and identity options shared by every CLI subcommand.
/// CLI flags override matching keys in <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>
/// (appsettings.json, env vars, user secrets).
/// </summary>
public abstract class CommonSettings : CommandSettings
{
    [CommandOption("--rabbitmq-host <HOST>")]
    [Description("RabbitMQ host (overrides RabbitMq:HostName)")]
    public string? RabbitMqHost { get; init; }

    [CommandOption("--rabbitmq-port <PORT>")]
    [Description("RabbitMQ port (overrides RabbitMq:Port)")]
    public int? RabbitMqPort { get; init; }

    [CommandOption("--rabbitmq-user <USER>")]
    [Description("RabbitMQ username (overrides RabbitMq:UserName)")]
    public string? RabbitMqUser { get; init; }

    [CommandOption("--rabbitmq-password <PASSWORD>")]
    [Description("RabbitMQ password (overrides RabbitMq:Password)")]
    public string? RabbitMqPassword { get; init; }

    [CommandOption("--agent <NAME>")]
    [Description("Agent name to address (overrides Agent:Name; default: RockBot)")]
    public string? AgentName { get; init; }

    [CommandOption("-s|--session <ID>")]
    [Description("Session ID for the conversation (default: cli-session)")]
    public string? SessionId { get; init; }

    [CommandOption("-u|--user <ID>")]
    [Description("User ID (default: cli-user)")]
    public string? UserId { get; init; }

    [CommandOption("--timeout <SECONDS>")]
    [Description("Reply timeout in seconds (default: 180)")]
    public int? TimeoutSeconds { get; init; }

    [CommandOption("--proxy-id <ID>")]
    [Description("UserProxy identity used to derive private response queues (default: random per run; set explicitly to share queues across runs)")]
    public string? ProxyId { get; init; }

    [CommandOption("-v|--verbose")]
    [Description("Enable verbose logging")]
    public bool Verbose { get; init; }

    /// <summary>Concrete subclass for commands that need no extra options.</summary>
    public sealed class Plain : CommonSettings { }
}
