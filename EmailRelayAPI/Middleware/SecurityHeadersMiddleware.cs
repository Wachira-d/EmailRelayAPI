namespace EmailRelayAPI.Middleware;

/// <summary>Adds defensive HTTP headers to every response.</summary>
public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var h = context.Response.Headers;
            var path = context.Request.Path.Value ?? string.Empty;
            var isSwagger = path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase);

            h.TryAdd("X-Frame-Options", "DENY");
            h.TryAdd("X-Content-Type-Options", "nosniff");
            h.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");
            h.TryAdd("Permissions-Policy", "geolocation=(), microphone=(), camera=()");
            h.TryAdd("Content-Security-Policy", isSwagger
                ? "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; frame-ancestors 'none'"
                : "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'; font-src 'self' data:; connect-src 'self'; frame-ancestors 'none'");

            if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
            {
                h.TryAdd("Cache-Control", "no-store, no-cache, must-revalidate");
                h.TryAdd("Pragma", "no-cache");
            }

            h.Remove("Server");
            h.Remove("X-Powered-By");
            return Task.CompletedTask;
        });

        return _next(context);
    }
}

public static class SecurityHeadersMiddlewareExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.UseMiddleware<SecurityHeadersMiddleware>();
}
