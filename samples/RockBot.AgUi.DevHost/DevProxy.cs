using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RockBot.Messaging;
using RockBot.Messaging.InProcess;
using RockBot.UserProxy;

namespace RockBot.AgUi.DevHost;

/// <summary>How to start a <see cref="DevProxy"/>.</summary>
public sealed class DevProxyOptions
{
    /// <summary>
    /// The port to listen on, on localhost. Null serves no socket at all: the proxy is reached
    /// through <see cref="WebApplicationFactory{TEntryPoint}.CreateClient()"/>, which is what a test wants.
    /// </summary>
    public int? Port { get; init; }

    /// <summary>The browser origins allowed to call the proxy (<c>AgUi:Cors:AllowedOrigins</c>).</summary>
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];

    /// <summary>Where attachments are kept. Null makes a temporary directory, deleted on dispose.</summary>
    public string? AttachmentsPath { get; init; }

    /// <summary>Any other proxy setting, by its configuration key (<c>UserProxy:ReplyTimeout</c>, say).</summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// The real AG-UI proxy (<c>RockBot.UserProxy.AgUi</c>, its own <c>Program</c> and HTTP pipeline),
/// with the in-process message bus in place of RabbitMQ and a <see cref="DemoAgent"/> on that bus
/// in place of the agent. Everything between the HTTP request and the bus is the production code.
/// </summary>
public sealed class DevProxy : WebApplicationFactory<Program>
{
    public const string AgentName = "RockBot";

    private readonly DevProxyOptions _options;
    private readonly string _attachments;
    private readonly bool _ownsAttachments;

    private DevProxy(DevProxyOptions options)
    {
        _options = options;
        _ownsAttachments = options.AttachmentsPath is null;
        _attachments = options.AttachmentsPath
            ?? Path.Combine(Path.GetTempPath(), "rockbot-agui-devhost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_attachments);
    }

    /// <summary>The scripted agent on the other side of the bus.</summary>
    public DemoAgent Agent { get; private set; } = null!;

    /// <summary>The directory attachments are written to and served from.</summary>
    public string AttachmentsPath => _attachments;

    public static async Task<DevProxy> StartAsync(DevProxyOptions? options = null)
    {
        var proxy = new DevProxy(options ?? new DevProxyOptions());
        if (proxy._options.Port is { } port)
            proxy.UseKestrel(port);
        proxy.StartServer();

        proxy.Agent = await DemoAgent.StartAsync(
            proxy.Services.GetRequiredService<IMessagePublisher>(),
            proxy.Services.GetRequiredService<IMessageSubscriber>(),
            proxy._attachments);
        return proxy;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // WebApplicationFactory looks for the proxy's source directory, which it only finds when
        // run under a test host. The proxy needs nothing from its content root here.
        builder.UseContentRoot(AppContext.BaseDirectory);
        builder.UseSetting("UserProxy:ProxyId", "agui-devhost");
        builder.UseSetting("Agent:Name", AgentName);
        for (var i = 0; i < _options.AllowedOrigins.Count; i++)
            builder.UseSetting($"AgUi:Cors:AllowedOrigins:{i}", _options.AllowedOrigins[i]);
        foreach (var (key, value) in _options.Settings)
            builder.UseSetting(key, value);

        builder.ConfigureTestServices(services =>
        {
            // Everything RabbitMQ: the publisher and subscriber, and the hosted services that
            // would dial the broker.
            var rabbit = services
                .Where(d => d.ServiceType == typeof(IMessagePublisher)
                    || d.ServiceType == typeof(IMessageSubscriber)
                    || (d.ServiceType == typeof(IHostedService)
                        && d.ImplementationType?.Namespace?.StartsWith("RockBot.Messaging.RabbitMQ", StringComparison.Ordinal) == true))
                .ToList();
            foreach (var descriptor in rabbit)
                services.Remove(descriptor);
            services.AddRockBotInProcessMessaging();

            // Serve attachments from this host's directory, not the pod's shared volume.
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(AttachmentPathResolver)).ToList())
                services.Remove(descriptor);
            services.AddSingleton(new AttachmentPathResolver(_attachments));
        });
    }

    public override async ValueTask DisposeAsync()
    {
        if (Agent is not null)
            await Agent.DisposeAsync();
        await base.DisposeAsync();

        if (_ownsAttachments)
        {
            try
            {
                Directory.Delete(_attachments, recursive: true);
            }
            catch (IOException)
            {
                // A file still open somewhere: a temporary directory left behind is harmless.
            }
        }
    }
}
