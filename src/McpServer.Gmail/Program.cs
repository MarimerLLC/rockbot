using McpServer.Gmail;
using McpServer.Gmail.Services;
using McpServer.Gmail.Tools;
using McpServer.TodoApp;

// Mail may declare legacy charsets such as windows-1252.
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<GmailOptions>()
    .BindConfiguration(GmailOptions.SectionName)
    .ValidateDataAnnotations()
    .Validate(o => o.Accounts.Count > 0, "configure at least one Gmail:Accounts entry; run deploy/gmail/setup.sh")
    .Validate(o => o.Accounts.All(a => GmailOptions.IsValidName(a.Name)), "Gmail account names must be lower-case letters, digits and '-'")
    .Validate(o => o.Accounts.Select(a => a.Name).Distinct().Count() == o.Accounts.Count, "Gmail account names must be unique")
    .Validate(o => o.Accounts.All(a => a.RefreshToken.Length > 0 && a.Email.Contains('@')), "each Gmail account needs an Email and a RefreshToken")
    .Validate(o => IsKnownZone(o.TimeZone), "Gmail:TimeZone is not a known time zone")
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<GoogleTokens>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient<GmailApi>(c =>
{
    c.BaseAddress = new Uri("https://gmail.googleapis.com/gmail/v1/");
    c.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddSingleton<GmailClient>();

builder.Services.AddHealthChecks();
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<GmailTools>()
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
