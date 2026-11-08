using McpServer.Slack;
using McpServer.Slack.Services;
using McpServer.Slack.Tools;
using McpServer.TodoApp;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<SlackOptions>()
    .BindConfiguration(SlackOptions.SectionName)
    .ValidateDataAnnotations()
    .Validate(o => o.Workspaces.Count > 0, "configure at least one Slack:Workspaces entry; run deploy/slack/setup.sh")
    .Validate(o => o.Workspaces.All(w => SlackOptions.IsValidName(w.Name)), "Slack workspace names must be lower-case letters, digits and '-'")
    .Validate(o => o.Workspaces.Select(w => w.Name).Distinct().Count() == o.Workspaces.Count, "Slack workspace names must be unique")
    .Validate(o => o.Workspaces.All(w => w.Token.StartsWith("xoxp-", StringComparison.Ordinal)), "each Slack workspace needs a user token (xoxp-…)")
    .Validate(o => IsKnownZone(o.TimeZone), "Slack:TimeZone is not a known time zone")
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<SlackApi>(c =>
{
    c.BaseAddress = new Uri("https://slack.com/api/");
    c.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton<SlackClient>();

builder.Services.AddHealthChecks();
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<SlackTools>()
    .WithStrictArguments();

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapMcp();

await app.RunAsync();

static bool IsKnownZone(string id)
{
    try
    {
        TimeZoneInfo.FindSystemTimeZoneById(id);
        return true;
    }
    catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
    {
        return false;
    }
}

public partial class Program;
