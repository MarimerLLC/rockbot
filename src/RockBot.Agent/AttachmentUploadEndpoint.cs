using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RockBot.Agent;

/// <summary>
/// A minimal HTTP surface on the agent for uploading attachments, so a file the size of a real
/// screenshot never has to cross the message bus.
/// </summary>
/// <remarks>
/// The agent is otherwise bus-only, deliberately. This is the one exception, and it exists
/// because the alternative is worse: RabbitMQ holds message bodies in memory until they are
/// acked, so routing every screenshot through it charges the broker for something that is not a
/// message in any meaningful sense — it is a file being moved to a volume the agent already
/// mounts. <c>POST /attachments</c> writes it directly.
///
/// <para>The endpoint is <b>not</b> a general file-write API. It goes through the same
/// <see cref="InboundAttachmentService"/> as the bus path, so the allowlist, size cap, extension
/// check and filename sanitisation are identical, and a caller supplies a name, never a path.</para>
///
/// <para>It listens on the pod network only and carries no authentication of its own — the same
/// posture as the introspection MCP sidecar in this pod. Do not expose it beyond the cluster
/// without putting auth in front of it; anything reachable here can write into the shared
/// attachments directory, subject to the allowlist.</para>
/// </remarks>
internal sealed class AttachmentUploadEndpoint(
    InboundAttachmentService attachments,
    AttachmentUploadEndpointOptions options,
    ILogger<AttachmentUploadEndpoint> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation(
                "Attachment upload endpoint disabled; uploads fall back to the message bus.");
            return;
        }

        // A slim builder rather than the agent's own host: this needs Kestrel and routing and
        // nothing else, and keeping it self-contained means the agent's DI graph is untouched.
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://+:{options.Port}");
        builder.Logging.ClearProviders();

        var app = builder.Build();

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        app.MapPost("/attachments", async (HttpRequest request, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Expected a multipart/form-data upload." });

            var form = await request.ReadFormAsync(ct);
            var file = form.Files["file"];
            if (file is null)
                return Results.BadRequest(new { error = "No 'file' part in the upload." });

            // Guard before buffering: the cap exists so a client cannot make the agent hold an
            // arbitrary amount of memory, which reading first would defeat.
            if (file.Length > InboundAttachmentService.MaxBytes)
            {
                return Results.Json(
                    new { error = $"The file is over the {InboundAttachmentService.MaxBytes / 1024 / 1024} MB limit." },
                    statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            using var buffer = new MemoryStream();
            await using (var stream = file.OpenReadStream())
                await stream.CopyToAsync(buffer, ct);

            var response = await attachments.StoreAsync(
                file.FileName,
                string.IsNullOrWhiteSpace(file.ContentType) ? null : file.ContentType,
                buffer.ToArray(),
                form["sessionId"].ToString(),
                ct);

            return response.Success
                ? Results.Ok(response)
                : Results.BadRequest(response);
        });

        logger.LogInformation(
            "Attachment upload endpoint listening on port {Port} (POST /attachments)", options.Port);

        try
        {
            await app.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // The agent must keep running without it — clients fall back to the bus, which is
            // slower for the broker but correct. A dead endpoint is not a dead agent.
            logger.LogError(ex,
                "Attachment upload endpoint failed to start on port {Port}; uploads will fall back " +
                "to the message bus.", options.Port);
        }
    }
}

/// <summary>
/// Configuration for the agent's attachment upload endpoint, bound from
/// <c>AttachmentUpload</c> (env: <c>AttachmentUpload__Port</c>).
/// </summary>
public sealed class AttachmentUploadEndpointOptions
{
    /// <summary>
    /// Whether to listen at all. Defaults to <c>true</c>: with the endpoint off, every upload
    /// falls back to the bus, which works but puts multi-megabyte bodies through the broker.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Port to listen on. Defaults to 8082 — 8081 is the introspection MCP sidecar in the same
    /// pod.
    /// </summary>
    public int Port { get; set; } = 8082;
}
