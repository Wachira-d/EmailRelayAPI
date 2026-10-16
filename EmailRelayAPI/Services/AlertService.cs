using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using EmailRelayAPI.Models;
using EmailRelayAPI.Options;
using EmailRelayAPI.Services.Transports;
using Microsoft.Extensions.Options;

namespace EmailRelayAPI.Services;

public interface IAlertService
{
    /// <summary>Notify operators (email and/or webhook). Throttled per alert type.</summary>
    Task RaiseAsync(string type, string title, string message, CancellationToken ct = default);
}

public class AlertService : IAlertService
{
    public const string HttpClientName = "alert-webhook";

    private readonly IOptionsMonitor<AlertOptions> _options;
    private readonly IServiceProvider _services;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AlertService> _logger;
    private readonly ConcurrentDictionary<string, DateTime> _lastSent = new();

    public AlertService(IOptionsMonitor<AlertOptions> options, IServiceProvider services, IHttpClientFactory httpFactory, ILogger<AlertService> logger)
    {
        _options = options;
        _services = services;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task RaiseAsync(string type, string title, string message, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!opts.Enabled) return;

        var now = DateTime.UtcNow;
        var last = _lastSent.GetOrAdd(type, DateTime.MinValue);
        if (now - last < TimeSpan.FromMinutes(Math.Max(1, opts.ThrottleMinutes))) return;
        _lastSent[type] = now;

        _logger.LogWarning("ALERT [{Type}] {Title}: {Message}", type, title, message);

        if (!string.IsNullOrWhiteSpace(opts.WebhookUrl))
        {
            try
            {
                var client = _httpFactory.CreateClient(HttpClientName);
                var payload = JsonSerializer.Serialize(new { source = "EmailRelayAPI", type, title, text = $"{title}\n{message}", message, timestamp = now });
                using var res = await client.PostAsync(opts.WebhookUrl, new StringContent(payload, Encoding.UTF8, "application/json"), ct);
                if (!res.IsSuccessStatusCode) _logger.LogWarning("Alert webhook returned {Status}", (int)res.StatusCode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Alert webhook failed");
            }
        }

        if (opts.AdminEmails.Count > 0 && !string.IsNullOrWhiteSpace(opts.SenderProfileName))
        {
            try
            {
                var queue = _services.GetRequiredService<IEmailQueueService>();
                var email = new OutboundEmail
                {
                    To = opts.AdminEmails.Select(e => new EmailAddressDto { Address = e }).ToList(),
                    Subject = $"[EmailRelay] {title}",
                    Text = $"{title}\n\n{message}\n\nTime (UTC): {now:u}\nType: {type}",
                    Html = $"<h3>{WebUtility.HtmlEncode(title)}</h3><p>{WebUtility.HtmlEncode(message).Replace("\n", "<br>")}</p><p><small>Time (UTC): {now:u} · Type: {WebUtility.HtmlEncode(type)}</small></p>"
                };
                await queue.AcceptSystemAsync(opts.SenderProfileName, email, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Alert email could not be queued");
            }
        }
    }
}
