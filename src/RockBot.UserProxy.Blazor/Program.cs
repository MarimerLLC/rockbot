using Microsoft.AspNetCore.Components;
using RockBot.Messaging.RabbitMQ;
using RockBot.UserProxy;
using RockBot.UserProxy.Blazor.Components;
using RockBot.UserProxy.Blazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Add Blazor services
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Add RockBot services
builder.Services.AddRockBotRabbitMq();
builder.Services.AddUserProxy();
builder.Services.AddSingleton<IUserFrontend, BlazorUserFrontend>();
builder.Services.AddSingleton<ChatStateService>();

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
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
