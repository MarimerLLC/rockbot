using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.Messaging;

namespace RockBot.Messaging.RabbitMQ;

/// <summary>
/// HTTP client for the RabbitMQ Management HTTP API.
/// Implements <see cref="IDlqSampler"/> for use by the dream service.
/// When <see cref="RabbitMqOptions.ManagementApiBaseUrl"/> is null or empty,
/// all methods return empty results rather than throwing.
/// </summary>
public sealed class RabbitMqManagementClient : IDlqSampler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient? _http;
    private readonly string _vhostEncoded;
    private readonly ILogger<RabbitMqManagementClient> _logger;

    public RabbitMqManagementClient(
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqManagementClient> logger)
    {
        _logger = logger;
        var opts = options.Value;
        _vhostEncoded = Uri.EscapeDataString(opts.VirtualHost);

        if (string.IsNullOrWhiteSpace(opts.ManagementApiBaseUrl))
        {
            _logger.LogDebug("RabbitMqManagementClient: ManagementApiBaseUrl not configured; DLQ inspection disabled");
            return;
        }

        var baseUrl = opts.ManagementApiBaseUrl.TrimEnd('/') + "/";
        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };

        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{opts.UserName}:{opts.Password}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    public bool IsEnabled => _http is not null;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DlqQueueInfo>> GetDlqQueuesAsync(CancellationToken ct = default)
    {
        if (_http is null) return [];

        try
        {
            var response = await _http.GetAsync(
                $"api/queues/{_vhostEncoded}?columns=name,messages", ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "RabbitMqManagementClient: GET queues returned {Status}", response.StatusCode);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var result = new List<DlqQueueInfo>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var name = element.GetProperty("name").GetString();
                if (string.IsNullOrEmpty(name) || !name.EndsWith(".dlq", StringComparison.Ordinal))
                    continue;

                var messages = element.TryGetProperty("messages", out var msgProp)
                    ? msgProp.GetInt64()
                    : 0L;

                result.Add(new DlqQueueInfo(name, messages));
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "RabbitMqManagementClient: failed to list DLQ queues");
            return [];
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DlqMessage>> SampleMessagesAsync(
        string queueName,
        int maxCount,
        CancellationToken ct = default)
    {
        if (_http is null) return [];

        try
        {
            var body = JsonSerializer.Serialize(new
            {
                count = maxCount,
                ackmode = "ack_requeue_true",
                encoding = "auto",
                truncate = 500
            });

            var queueEncoded = Uri.EscapeDataString(queueName);
            var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"api/queues/{_vhostEncoded}/{queueEncoded}/get")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            var response = await _http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "RabbitMqManagementClient: POST queues/{Queue}/get returned {Status}",
                    queueName, response.StatusCode);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var result = new List<DlqMessage>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                result.Add(ParseMessage(element));
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "RabbitMqManagementClient: failed to sample messages from {Queue}", queueName);
            return [];
        }
    }

    /// <inheritdoc/>
    public async Task PurgeQueueAsync(string queueName, CancellationToken ct = default)
    {
        if (_http is null) return;

        try
        {
            var queueEncoded = Uri.EscapeDataString(queueName);
            var response = await _http.DeleteAsync(
                $"api/queues/{_vhostEncoded}/{queueEncoded}/contents", ct);

            if (response.IsSuccessStatusCode)
                _logger.LogInformation("RabbitMqManagementClient: purged DLQ {Queue}", queueName);
            else
                _logger.LogWarning(
                    "RabbitMqManagementClient: purge of {Queue} returned {Status}",
                    queueName, response.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "RabbitMqManagementClient: failed to purge {Queue}", queueName);
        }
    }

    private static DlqMessage ParseMessage(JsonElement el)
    {
        var routingKey = el.TryGetProperty("routing_key", out var rk) ? rk.GetString() : null;

        string? messageId = null;
        string? messageType = null;
        string? source = null;
        string? destination = null;
        string? deathReason = null;
        int deathCount = 0;
        DateTimeOffset? deadLetteredAt = null;

        if (el.TryGetProperty("properties", out var props))
        {
            messageId = props.TryGetProperty("message_id", out var mid) ? mid.GetString() : null;
            messageType = props.TryGetProperty("type", out var t) ? t.GetString() : null;

            if (props.TryGetProperty("headers", out var headers))
            {
                source = ExtractHeaderString(headers, "rb-source");
                destination = ExtractHeaderString(headers, "rb-destination");

                if (headers.TryGetProperty("x-death", out var xDeath) &&
                    xDeath.ValueKind == JsonValueKind.Array &&
                    xDeath.GetArrayLength() > 0)
                {
                    var death = xDeath[0];
                    deathReason = death.TryGetProperty("reason", out var reason)
                        ? reason.GetString()
                        : null;

                    if (death.TryGetProperty("count", out var cnt))
                        deathCount = cnt.ValueKind == JsonValueKind.Number ? cnt.GetInt32() : 0;

                    if (death.TryGetProperty("time", out var timeEl))
                        deadLetteredAt = ParseAmqpTimestamp(timeEl);
                }
            }
        }

        const int PreviewCap = 200;
        var bodyPreview = string.Empty;
        if (el.TryGetProperty("payload", out var payload))
        {
            var raw = payload.GetString() ?? string.Empty;
            bodyPreview = raw.Length > PreviewCap ? raw[..PreviewCap] + "…" : raw;
        }

        return new DlqMessage(
            MessageId: messageId,
            MessageType: messageType,
            Source: source,
            Destination: destination,
            RoutingKey: routingKey,
            DeathReason: deathReason,
            DeathCount: deathCount,
            DeadLetteredAt: deadLetteredAt,
            BodyPreview: bodyPreview);
    }

    /// <summary>
    /// Extracts a string header value, handling both plain string and AMQP-typed
    /// {"$value": "..."} wrapper formats.
    /// </summary>
    private static string? ExtractHeaderString(JsonElement headers, string key)
    {
        if (!headers.TryGetProperty(key, out var val)) return null;

        if (val.ValueKind == JsonValueKind.String)
            return val.GetString();

        // AMQP-typed wrapper: {"$value": "..."}
        if (val.ValueKind == JsonValueKind.Object && val.TryGetProperty("$value", out var inner))
            return inner.GetString();

        return null;
    }

    /// <summary>
    /// Parses an AMQP timestamp from the Management API, which may be a Unix epoch number
    /// or an AMQP-typed {"$value": epoch} object.
    /// </summary>
    private static DateTimeOffset? ParseAmqpTimestamp(JsonElement timeEl)
    {
        if (timeEl.ValueKind == JsonValueKind.Number)
            return DateTimeOffset.FromUnixTimeSeconds(timeEl.GetInt64());

        if (timeEl.ValueKind == JsonValueKind.Object && timeEl.TryGetProperty("$value", out var val))
        {
            if (val.ValueKind == JsonValueKind.Number)
                return DateTimeOffset.FromUnixTimeSeconds(val.GetInt64());
        }

        return null;
    }
}
