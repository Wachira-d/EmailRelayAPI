using System.Text.Json;
using EmailRelayAPI.Data.Entities;
using EmailRelayAPI.Models;
using EmailRelayAPI.Options;
using EmailRelayAPI.Security;
using EmailRelayAPI.Services;
using Microsoft.Extensions.Options;

namespace EmailRelayAPI.Middleware;

/// <summary>
/// Pre-authentication protection: dynamic block list, optional global IP allow list and a per-IP flood limit.
/// Runs before authentication so that abusive sources are rejected cheaply.
/// </summary>
public class IpFilterMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<SecurityOptions> _options;
    private readonly IBlockListService _blocks;
    private readonly IRateLimitService _rateLimit;
    private readonly IAnomalyDetector _anomaly;
    private readonly IAuditService _audit;
    private readonly SlidingCounterStore _auditThrottle = new();
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public IpFilterMiddleware(RequestDelegate next, IOptionsMonitor<SecurityOptions> options, IBlockListService blocks, IRateLimitService rateLimit, IAnomalyDetector anomaly, IAuditService audit)
    {
        _next = next;
        _options = options;
        _blocks = blocks;
        _rateLimit = rateLimit;
        _anomaly = anomaly;
        _audit = audit;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var opts = _options.CurrentValue;
        var path = context.Request.Path.Value ?? string.Empty;
        if (IsExcluded(path, opts))
        {
            await _next(context);
            return;
        }

        var remote = IpUtils.Normalize(context.Connection.RemoteIpAddress);
        var ip = remote?.ToString() ?? "unknown";

        if (remote == null)
        {
            await Reject(context, 403, "Unable to determine client IP address");
            return;
        }

        var block = _blocks.Check(remote);
        if (block != null)
        {
            if (_auditThrottle.Increment($"blocked:{ip}", TimeSpan.FromMinutes(1)) <= 1)
            {
                _audit.Security("BlockedRequest", AuditSeverity.Info,
                    $"Request from blocked IP {ip} rejected ({block.Reason}). Further hits within a minute are not logged.", ip, traceId: context.GetTraceId());
            }
            await Reject(context, 403, "Access denied: source address is blocked");
            return;
        }

        if (opts.EnableIpAllowList && !IpUtils.IsLoopback(remote) && !IpUtils.MatchesAny(remote, opts.AllowedIpAddresses))
        {
            if (_auditThrottle.Increment($"notallowed:{ip}", TimeSpan.FromMinutes(1)) <= 1)
            {
                _audit.Security("IpNotAllowed", AuditSeverity.Warning, $"Request from {ip} rejected by global IP allow list", ip, traceId: context.GetTraceId());
            }
            _anomaly.RecordAuthFailure(ip, "IP not in allow list", context.GetTraceId());
            await Reject(context, 403, "Access denied: IP address not allowed");
            return;
        }

        _anomaly.RecordRequest(ip, context.GetTraceId());

        if (opts.EnableGlobalRateLimit)
        {
            var decision = _rateLimit.Check($"ip:{ip}", opts.GlobalRateLimitPerMinute, TimeSpan.FromMinutes(1));
            if (!decision.Allowed)
            {
                if (_auditThrottle.Increment($"iplimit:{ip}", TimeSpan.FromMinutes(1)) <= 1)
                {
                    _audit.Security("GlobalRateLimitExceeded", AuditSeverity.Warning,
                        $"IP {ip} exceeded the global limit of {opts.GlobalRateLimitPerMinute} requests/min", ip, traceId: context.GetTraceId());
                }
                context.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString();
                await Reject(context, 429, $"Too many requests from this address. Retry after {decision.RetryAfterSeconds}s");
                return;
            }
        }

        await _next(context);
    }

    public static bool IsExcluded(string path, SecurityOptions opts)
        => opts.ExcludedPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) || path == "/" || path.Length == 0;

    private static async Task Reject(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(ApiResponse.Fail(message), JsonOpts));
    }
}

public static class IpFilterMiddlewareExtensions
{
    public static IApplicationBuilder UseIpFilter(this IApplicationBuilder app) => app.UseMiddleware<IpFilterMiddleware>();
}
