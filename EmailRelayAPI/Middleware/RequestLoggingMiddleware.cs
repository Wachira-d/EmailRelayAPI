using System.Diagnostics;
using EmailRelayAPI.Data.Entities;
using EmailRelayAPI.Options;
using EmailRelayAPI.Security;
using EmailRelayAPI.Services;
using Microsoft.Extensions.Options;

namespace EmailRelayAPI.Middleware;

/// <summary>Records every API request (who, what, status, duration) for usage reporting.</summary>
public class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IAuditService _audit;
    private readonly IAnomalyDetector _anomaly;
    private readonly IOptionsMonitor<AuditOptions> _audit_options;
    private readonly IOptionsMonitor<SecurityOptions> _security;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    public RequestLoggingMiddleware(ILogger<RequestLoggingMiddleware> logger, RequestDelegate next, IAuditService audit, IAnomalyDetector anomaly, IOptionsMonitor<AuditOptions> auditOptions, IOptionsMonitor<SecurityOptions> security)
    {
        _next = next;
        _audit = audit;
        _anomaly = anomaly;
        _audit_options = auditOptions;
        _security = security;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (!path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var sw = Stopwatch.StartNew();
        string? error = null;
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            throw;
        }
        finally
        {
            sw.Stop();
            var cc = context.GetClientContext();
            var status = error != null && !context.Response.HasStarted ? 500 : context.Response.StatusCode;

            if (status == 405)
            {
                _logger.LogWarning("405 Method Not Allowed for {Method} {Path} (override header: '{Override}'). The method reached the application but no endpoint accepts it.",
                    context.Request.Method, path, context.Request.Headers["X-HTTP-Method-Override"].ToString());
            }

            if (cc != null && status >= 400 && status < 500 && status != 429)
            {
                _anomaly.RecordClientError(cc.ClientId, status, path);
            }

            if (_audit_options.CurrentValue.EnableRequestLogging)
            {
                _audit.LogRequest(new RequestLog
                {
                    ClientId = cc?.ClientId,
                    ApiKeyId = cc?.ApiKeyId,
                    Username = cc == null ? context.GetAdminUsername() : null,
                    IpAddress = context.GetClientIp(),
                    Method = context.Request.Method,
                    Path = path,
                    Query = context.Request.QueryString.HasValue ? context.Request.QueryString.Value : null,
                    StatusCode = status,
                    DurationMs = sw.ElapsedMilliseconds,
                    RequestBytes = context.Request.ContentLength,
                    UserAgent = context.GetUserAgent(),
                    TraceId = context.GetTraceId(),
                    Error = error
                });
            }
        }
    }
}

public static class RequestLoggingMiddlewareExtensions
{
    public static IApplicationBuilder UseRequestLogging(this IApplicationBuilder app) => app.UseMiddleware<RequestLoggingMiddleware>();
}
