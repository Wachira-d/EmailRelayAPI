using System.Text.Json;
using EmailRelayAPI.Data.Entities;
using EmailRelayAPI.Models;
using EmailRelayAPI.Security;
using EmailRelayAPI.Services;

namespace EmailRelayAPI.Middleware;

/// <summary>Post-authentication per-client rate limiting and client-level anomaly tracking.</summary>
public class ClientRateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IRateLimitService _rateLimit;
    private readonly IAnomalyDetector _anomaly;
    private readonly IAuditService _audit;
    private readonly SlidingCounterStore _auditThrottle = new();
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public ClientRateLimitMiddleware(RequestDelegate next, IRateLimitService rateLimit, IAnomalyDetector anomaly, IAuditService audit)
    {
        _next = next;
        _rateLimit = rateLimit;
        _anomaly = anomaly;
        _audit = audit;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var cc = context.GetClientContext();
        if (cc == null)
        {
            await _next(context);
            return;
        }

        var ip = context.GetClientIp();
        var traceId = context.GetTraceId();

        _anomaly.RecordClientRequest(cc.ClientId, ip, traceId);
        _audit.TouchSeenIp(cc.ClientId, ip);
        await _anomaly.RecordClientIpAsync(cc.ClientId, cc.ClientName, ip, context.RequestAborted);

        var limit = cc.Client.RateLimitPerMinute;
        var decision = _rateLimit.Check($"client:{cc.ClientId}", limit, TimeSpan.FromMinutes(1));

        context.Response.OnStarting(() =>
        {
            var h = context.Response.Headers;
            h["X-RateLimit-Limit"] = decision.Limit.ToString();
            h["X-RateLimit-Remaining"] = decision.Remaining.ToString();
            return Task.CompletedTask;
        });

        if (!decision.Allowed)
        {
            if (_auditThrottle.Increment($"climit:{cc.ClientId}", TimeSpan.FromMinutes(1)) <= 1)
            {
                _audit.Security("ClientRateLimitExceeded", AuditSeverity.Warning,
                    $"Client '{cc.ClientName}' exceeded {limit} requests/min", ip, cc.ClientId, cc.ApiKeyId, traceId: traceId);
            }
            context.Response.StatusCode = 429;
            context.Response.ContentType = "application/json";
            context.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString();
            await context.Response.WriteAsync(JsonSerializer.Serialize(
                ApiResponse.Fail($"Rate limit of {limit} requests per minute exceeded. Retry after {decision.RetryAfterSeconds}s"), JsonOpts));
            return;
        }

        await _next(context);
    }
}

public static class ClientRateLimitMiddlewareExtensions
{
    public static IApplicationBuilder UseClientRateLimit(this IApplicationBuilder app) => app.UseMiddleware<ClientRateLimitMiddleware>();
}
