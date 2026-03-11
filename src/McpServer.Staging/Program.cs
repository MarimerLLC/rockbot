using McpServer.Staging;
using McpServer.Staging.Tools;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<StagingRepository>();
builder.Services.AddHealthChecks();
builder.Services.AddMcpServer().WithHttpTransport().WithTools<StagingTools>();

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapMcp();

// REST API for script pods (cross-namespace HTTP access)
app.MapPut("/api/staging/{**path}", async (string path, HttpRequest req, StagingRepository repo) =>
{
    try
    {
        await repo.StoreStreamAsync(path, req.Body);
        return Results.Ok(new { path });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

app.MapGet("/api/staging/{**path}", (string path, StagingRepository repo) =>
{
    try
    {
        var stream = repo.OpenRead(path);
        return stream is null ? Results.NotFound() : Results.Stream(stream, "application/octet-stream");
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

app.MapDelete("/api/staging/{**path}", (string path, StagingRepository repo) =>
{
    try
    {
        var deleted = repo.Delete(path);
        return deleted ? Results.Ok() : Results.NotFound();
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

app.MapGet("/api/staging", (StagingRepository repo) => Results.Ok(repo.List()));

await app.RunAsync();

public partial class Program { }
