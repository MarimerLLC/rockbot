using Microsoft.AspNetCore.Components;
using RockBot.Messaging.RabbitMQ;
using RockBot.UserProxy.Blazor.Auth;
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

// Persist the data-protection key ring when DataProtection:KeyRingPath is configured.
// The ring backs antiforgery tokens (see UseAntiforgery below); left in memory it is
// regenerated on every restart, so tokens minted by the previous process are rejected.
builder.Services.AddRockBotDataProtection(builder.Configuration);

// OAuth sign-in. Off by default (Auth:Enabled=false) — the deployment is then gated by whatever
// network sits in front of it, exactly as before. Enabled with a bad configuration, this throws
// here rather than serving an open UI.
var authOptions = builder.Services.AddRockBotAuth(builder.Configuration);

var app = builder.Build();

// First in the pipeline: every absolute URL built downstream — HTTPS redirection and the OAuth
// redirect_uri above all — has to agree with the address the browser actually used, not with the
// plain http://:8080 the container sees behind a TLS-terminating ingress.
app.UseRockBotPublicOrigin(authOptions);

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();

if (authOptions.Enabled)
    app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRockBotAuthEndpoints(authOptions);

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
})
// This endpoint serves PVC bytes. Left anonymous it is a side door around sign-in entirely —
// however well the chat page is locked down, the attachments it references would not be. The
// default policy is permissive when Auth:Enabled=false, so this stays a no-op for deployments
// that have not turned sign-in on.
.RequireAuthorization();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>
/// Exposed so integration tests can host this app through <c>WebApplicationFactory</c>. Top-level
/// statements generate an internal Program class, which the factory's type parameter cannot name.
/// </summary>
public partial class Program;
