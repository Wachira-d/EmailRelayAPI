using System.Security.Claims;
using EmailRelayAPI.Data.Entities;

namespace EmailRelayAPI.Security;

public static class RelayClaims
{
    public const string ApiKeyScheme = "ApiKey";
    public const string AdminCookieScheme = "AdminCookie";
    public const string AdminOrApiKeyScheme = "AdminOrApiKey";

    public const string ClientId = "relay:client_id";
    public const string ClientName = "relay:client_name";
    public const string ApiKeyId = "relay:api_key_id";
    public const string KeyPrefix = "relay:key_prefix";
    public const string Scope = "relay:scope";
    public const string AuthType = "relay:auth_type";
    public const string DisplayName = "relay:display_name";

    public const string ItemClientContext = "RelayClientContext";

    public const string PolicySend = "Scope.Send";
    public const string PolicyRead = "Scope.Read";
    public const string PolicyAdmin = "Admin";
    public const string PolicyViewer = "Viewer";

    public const string RoleAdmin = "Admin";
    public const string RoleViewer = "Viewer";

    public const string ScopeSend = "send";
    public const string ScopeRead = "read";
    public const string ScopeAdmin = "admin";
}

/// <summary>Authenticated API client for the current request.</summary>
public sealed class ClientContext
{
    public required ApiClient Client { get; init; }
    public required ApiKey Key { get; init; }
    public int ClientId => Client.Id;
    public string ClientName => Client.Name;
    public int ApiKeyId => Key.Id;
    public string KeyPrefix => Key.Prefix;
    public string[] Scopes => Key.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public static class HttpContextExtensions
{
    public static ClientContext? GetClientContext(this HttpContext ctx)
        => ctx.Items.TryGetValue(RelayClaims.ItemClientContext, out var v) ? v as ClientContext : null;

    public static string GetClientIp(this HttpContext ctx)
        => IpUtils.Normalize(ctx.Connection.RemoteIpAddress)?.ToString() ?? "unknown";

    public static string? GetUserAgent(this HttpContext ctx)
    {
        var ua = ctx.Request.Headers.UserAgent.ToString();
        return string.IsNullOrEmpty(ua) ? null : (ua.Length > 500 ? ua[..500] : ua);
    }

    public static string GetTraceId(this HttpContext ctx)
        => System.Diagnostics.Activity.Current?.Id ?? ctx.TraceIdentifier;

    public static string? GetAdminUsername(this HttpContext ctx)
    {
        var user = ctx.User;
        if (user?.Identity?.IsAuthenticated != true) return null;
        var type = user.FindFirst(RelayClaims.AuthType)?.Value;
        return type == "admin" ? user.Identity.Name : null;
    }

    public static bool IsAdmin(this ClaimsPrincipal user)
        => user.IsInRole(RelayClaims.RoleAdmin);
}
