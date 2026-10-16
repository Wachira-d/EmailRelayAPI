using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmailRelayAPI.Data;
using EmailRelayAPI.Data.Entities;
using EmailRelayAPI.Models;
using EmailRelayAPI.Options;
using EmailRelayAPI.Security;
using Microsoft.Extensions.Options;

namespace EmailRelayAPI.Services;

public interface IWebhookNotifier
{
    /// <summary>Posts a delivery status callback to the client's webhook (if configured).</summary>
    Task NotifyAsync(EmailMessage message, ApiClient client, CancellationToken ct = default);
}

public class WebhookNotifier : IWebhookNotifier
{
    public const string HttpClientName = "client-webhook";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ISecretProtector _secrets;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<DeliveryOptions> _options;
    private readonly ILogger<WebhookNotifier> _logger;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public WebhookNotifier(IHttpClientFactory httpFactory, ISecretProtector secrets, IServiceScopeFactory scopeFactory, IOptionsMonitor<DeliveryOptions> options, ILogger<WebhookNotifier> logger)
    {
        _httpFactory = httpFactory;
        _secrets = secrets;
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    public async Task NotifyAsync(EmailMessage message, ApiClient client, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(client.WebhookUrl)) return;
        if (!Uri.TryCreate(client.WebhookUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http")) return;

        var payload = JsonSerializer.Serialize(new
        {
            @event = "email." + message.Status.ToApiString(),
            id = message.Id,
            status = message.Status.ToApiString(),
            clientId = message.ClientId,
            idempotencyKey = message.IdempotencyKey,
            subject = message.Subject,
            attempts = message.Attempts,
            providerMessageId = message.ProviderMessageId,
            error = message.LastError,
            sentAt = message.SentAt,
            tags = IpUtils.SplitList(message.Tags),
            timestamp = DateTime.UtcNow
        }, JsonOpts);

        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var secret = _secrets.Unprotect(client.WebhookSecretProtected);
        string? signature = null;
        if (!string.IsNullOrEmpty(secret))
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            signature = "sha256=" + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(ts + "." + payload))).ToLowerInvariant();
        }

        var http = _httpFactory.CreateClient(HttpClientName);
        http.Timeout = TimeSpan.FromSeconds(Math.Max(3, _options.CurrentValue.WebhookTimeoutSeconds));

        string? lastError = null;
        var delays = new[] { 0, 2000, 5000 };
        for (var attempt = 0; attempt < delays.Length; attempt++)
        {
            if (delays[attempt] > 0) await Task.Delay(delays[attempt], ct);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, uri)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                req.Headers.TryAddWithoutValidation("X-Relay-Timestamp", ts);
                req.Headers.TryAddWithoutValidation("X-Relay-Event", "email." + message.Status.ToApiString());
                if (signature != null) req.Headers.TryAddWithoutValidation("X-Relay-Signature", signature);

                using var res = await http.SendAsync(req, ct);
                if (res.IsSuccessStatusCode)
                {
                    await RecordAsync(message.Id, "WebhookSent", $"POST {uri.Host} → {(int)res.StatusCode} (attempt {attempt + 1})", ct);
                    return;
                }
                lastError = $"HTTP {(int)res.StatusCode}";
                if ((int)res.StatusCode is >= 400 and < 500 and not 408 and not 429) break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                lastError = ex.GetType().Name + ": " + ex.Message;
            }
        }

        _logger.LogWarning("Webhook to {Url} failed for message {Id}: {Error}", uri.Host, message.Id, lastError);
        await RecordAsync(message.Id, "WebhookFailed", $"POST {uri.Host} failed: {lastError}", ct);
    }

    private async Task RecordAsync(Guid messageId, string type, string details, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.EmailEvents.Add(new EmailEvent { MessageId = messageId, Type = type, Details = details, Source = "webhook" });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not record webhook event");
        }
    }
}
