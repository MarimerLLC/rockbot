using RockBot.Messaging.RabbitMQ;
using RockBot.UserProxy;
using RockBot.UserProxy.AgUi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRockBotRabbitMq(opts => builder.Configuration.GetSection("RabbitMq").Bind(opts));
builder.Services.AddUserProxy(opts =>
{
    opts.AgentName = builder.Configuration["Agent:Name"] ?? "RockBot";
    // Unique per replica: in Kubernetes the machine name is the pod name. Replicas sharing an id
    // would share reply queues, and the broker would hand one replica's replies to another.
    opts.ProxyId = builder.Configuration["UserProxy:ProxyId"] is { Length: > 0 } proxyId
        ? proxyId
        : $"agui-{Environment.MachineName.ToLowerInvariant()}";
    opts.ChannelName = "agui";
    // Upload attachments to the agent over HTTP when it is reachable, so a file's bytes never
    // enter the broker. Unset means uploads go over the bus, which still works.
    opts.AttachmentUploadUrl = builder.Configuration["UserProxy:AttachmentUploadUrl"];
    // A pod's name, and so this proxy's queue names, change on every rollout. Ephemeral queues
    // are deleted on stop and expired by the broker after a crash, so they don't pile up.
    opts.EphemeralQueues = true;
    // How long to wait for the agent to start a run. Format: "00:03:00".
    if (builder.Configuration.GetValue<TimeSpan?>("UserProxy:ReplyTimeout") is { } replyTimeout
        && replyTimeout > TimeSpan.Zero)
        opts.DefaultReplyTimeout = replyTimeout;
    // The longest a run may last while the agent keeps sending heartbeats. Format: "00:30:00".
    if (builder.Configuration.GetValue<TimeSpan?>("UserProxy:MaxTurnDuration") is { } maxTurn
        && maxTurn > TimeSpan.Zero)
        opts.MaxTurnDuration = maxTurn;
});
var agUiOptions = new AgUiOptions();
builder.Configuration.GetSection(AgUiOptions.SectionName).Bind(agUiOptions);
// A bad public address would break every MCP sign-in, each with an error from someone else's
// authorization server. Fail here instead.
agUiOptions.ResolvePublicBaseUrl();
builder.Services.AddSingleton(agUiOptions);
builder.Services.AddSingleton<AgUiRunRegistry>();

// Everything that isn't a run's own traffic goes through the frontend to the notify stream.
builder.Services.AddSingleton(sp => new NotifyHub(agUiOptions, logger: sp.GetRequiredService<ILogger<NotifyHub>>()));
builder.Services.AddSingleton(sp => new McpQuestionRegistry(
    agUiOptions, sp.GetRequiredService<NotifyHub>(), sp.GetService<TimeProvider>()));
builder.Services.AddSingleton<IUserFrontend, AgUiUserFrontend>();

// Reply attachments are served from the shared volume (mounted read-only), under
// ${ROCKBOT_SHARED_PATH}/attachments; the resolver is what keeps the endpoint inside it.
builder.Services.AddSingleton(_ => AttachmentPathResolver.FromEnvironment());
builder.Services.AddSingleton(sp => new McpSignInTracker(sp.GetService<TimeProvider>()));

var app = builder.Build();

// No authentication and no CORS yet (#794, phase 5): the proxy is for a private network such as a
// tailnet, where whoever can reach the port is allowed in. See docs/agui.md. MapAgUi keeps every
// endpoint but /healthz and the MCP sign-in redirect in one group for that phase to lock.
app.MapAgUi();

app.Run();

/// <summary>
/// Exposed so integration tests can host this app through <c>WebApplicationFactory</c>. Top-level
/// statements generate an internal Program class, which the factory's type parameter cannot name.
/// </summary>
public partial class Program;
