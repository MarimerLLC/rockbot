using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using RockBot.AgUi.Client;
using RockBot.AgUi.WasmClient;
using RockBot.UserProxy.ChatState;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var settings = ClientSettings.From(builder.Configuration, builder.HostEnvironment.BaseAddress);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<GoogleSignIn>();

// The shared chat state (no Blazor dependency): the page renders it, the session feeds it.
builder.Services.AddSingleton(_ => new ChatStateService());

builder.Services.AddSingleton(services => new AgUiProxyClient(new HttpClient(), new AgUiProxyClientOptions
{
    BaseAddress = settings.ProxyAddress,
    // App mode: the Google ID token as the bearer token. Network mode sends none.
    AccessToken = settings.AuthEnabled ? services.GetRequiredService<GoogleSignIn>().GetTokenAsync : null,
    // The browser's fetch hands a response over as it arrives only when asked to. Without this
    // a run would show nothing until it ended, and the notify stream nothing at all.
    ConfigureStreamingRequest = request => request.SetBrowserResponseStreamingEnabled(true),
}));

builder.Services.AddSingleton(services => new ChatSession(
    services.GetRequiredService<AgUiProxyClient>(),
    services.GetRequiredService<ChatStateService>(),
    new ChatSessionOptions { ThreadId = settings.ThreadId }));

await builder.Build().RunAsync();
