using Microsoft.AspNetCore.Components;
using RockBot.Messaging.RabbitMQ;
using RockBot.UserProxy;
using RockBot.UserProxy.Blazor.Components;
using RockBot.UserProxy.Blazor.Services;
using RockBot.UserProxy.WorkIqAuth;

var builder = WebApplication.CreateBuilder(args);

// Add Blazor services
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Add RockBot services
builder.Services.AddRockBotRabbitMq(opts => builder.Configuration.GetSection("RabbitMq").Bind(opts));
builder.Services.AddUserProxy(opts =>
{
    opts.AgentName = builder.Configuration["Agent:Name"] ?? "RockBot";
});
builder.Services.AddSingleton<IUserFrontend, BlazorUserFrontend>();
builder.Services.AddSingleton<ChatStateService>();

// WorkIQ device-code flow + expired-notification listener. Always registered;
// the flow itself fails fast with a NotConfigured error if WorkIQ settings
// are not present in configuration, so deployments without WorkIQ pay only
// the cost of an idle bus subscription.
builder.Services.AddWorkIqAuthClient(builder.Configuration);
builder.Services.AddScoped<WorkIqAuthUiService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();

// Serves agent reply attachments from the shared PVC (co-mounted read-only). The agent
// references files by name on AgentReply.Attachments; bytes never ride the bus. The resolver
// enforces containment under the shared attachments directory so this endpoint can't be coaxed
// into serving arbitrary files via traversal or absolute paths.
var attachmentResolver = AttachmentPathResolver.FromEnvironment();
app.MapGet("/attachments", (string? file) =>
{
    var resolved = attachmentResolver.Resolve(file);
    if (resolved is null)
        return Results.NotFound();

    return Results.File(resolved, AttachmentPathResolver.GuessMime(resolved));
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
