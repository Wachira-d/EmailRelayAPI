namespace EmailRelayAPI.Options;

/// <summary>
/// Console login through AuthenAPI (Active Directory), following the AuthenAPI integration guide:
/// AuthenAPI is asked on every login; the offline cache is used only when AuthenAPI cannot be reached.
/// </summary>
public class AuthenApiOptions
{
    public const string SectionName = "AuthenApi";

    /// <summary>AuthenAPI base URL: https only, no path, e.g. https://authen.hondacx.me</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Path of the authenticate endpoint.</summary>
    public string AuthenticatePath { get; set; } = "/api/ldap/authenticate";

    /// <summary>Path used by the console's connection check (never called before a login).</summary>
    public string HealthPath { get; set; } = "/health/ready";

    /// <summary>API key issued to this system by AuthenAPI. Keep it in server-side config or a secret store.</summary>
    public string ApiKey { get; set; } = string.Empty;
    public string ApiKeyHeaderName { get; set; } = "X-API-Key";

    /// <summary>Must be longer than AuthenAPI's worst case (guide: 45 s). No automatic retry is ever made.</summary>
    public int TimeoutSeconds { get; set; } = 45;

    /// <summary>TCP connect timeout; a connect failure counts as "unreachable" and allows the fallback.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 10;

    // ---------------- authorization ----------------

    /// <summary>AD groups (direct membership) mapped to the Admin role. Empty = every authenticated user is Admin.</summary>
    public List<string> AdminGroups { get; set; } = new();

    /// <summary>AD groups mapped to the read-only Viewer role.</summary>
    public List<string> ViewerGroups { get; set; } = new();

    // ---------------- local throttling ----------------

    /// <summary>Failed logins allowed per username and per IP inside the window (guide: 5 per 15 minutes).</summary>
    public int MaxFailedAttempts { get; set; } = 5;
    public int FailedAttemptWindowMinutes { get; set; } = 15;

    // ---------------- sessions ----------------

    /// <summary>Absolute session lifetime; the user must sign in again afterwards (guide: 8-12 hours).</summary>
    public int SessionTimeoutMinutes { get; set; } = 480;

    /// <summary>Idle timeout (sliding) inside the absolute lifetime.</summary>
    public int IdleTimeoutMinutes { get; set; } = 60;

    /// <summary>Lifetime of a degraded session (direct AD or offline login). Such sessions are read-only.</summary>
    public int DegradedSessionMinutes { get; set; } = 60;

    // ---------------- circuit breaker ----------------

    /// <summary>Consecutive "unreachable" results that open the circuit.</summary>
    public int CircuitFailureThreshold { get; set; } = 3;

    /// <summary>How long the circuit stays open before one trial request is let through.</summary>
    public int CircuitOpenSeconds { get; set; } = 30;

    // ---------------- offline credential cache ----------------

    /// <summary>
    /// How long after the last successful online login the offline cache may be used. 0 disables the offline cache
    /// (safest when the console does not need to work during a directory outage). Admin accounts are never cached.
    /// </summary>
    public int OfflineWindowHours { get; set; } = 24;

    /// <summary>Secret pepper for offline hashes. Keep it outside the database (config / secret store). Empty disables the offline cache.</summary>
    public string OfflineCachePepper { get; set; } = string.Empty;

    /// <summary>Wrong offline passwords allowed per user (counted atomically) before the offline path is refused.</summary>
    public int OfflineMaxFailuresPerUser { get; set; } = 5;

    /// <summary>Wrong offline passwords allowed per minute across all users (password-spraying guard).</summary>
    public int OfflineMaxFailuresPerMinute { get; set; } = 20;

    // ---------------- direct AD fallback (optional) ----------------

    public DirectAdOptions DirectAd { get; set; } = new();

    // ---------------- break-glass ----------------

    /// <summary>Break-glass local administrator used when AuthenAPI is not configured or unavailable.</summary>
    public LocalAdminOptions LocalAdmin { get; set; } = new();

    /// <summary>The offline cache is usable only with a window and a sufficiently long pepper.</summary>
    public bool OfflineCacheEnabled => OfflineWindowHours > 0 && (OfflineCachePepper?.Length ?? 0) >= 32;

    /// <summary>
    /// Normalizes and validates the base URL. Returns an error message, or null when the configuration is valid.
    /// An http URL is refused so that passwords never travel unencrypted, even once before a redirect.
    /// </summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl)) return null; // AuthenAPI not configured (local admin only)
        if (!Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var uri))
            return $"AuthenApi:BaseUrl '{BaseUrl}' is not an absolute URL";
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return $"AuthenApi:BaseUrl must use https (got '{uri.Scheme}'). Passwords must never be sent over http.";
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length > 0 && !path.Equals("/api", StringComparison.OrdinalIgnoreCase))
            return $"AuthenApi:BaseUrl must not contain a path (got '{uri.AbsolutePath}'); use e.g. https://authen.hondacx.me";
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return "AuthenApi:BaseUrl must not contain a query string or fragment";
        if (!AuthenticatePath.StartsWith('/')) return "AuthenApi:AuthenticatePath must start with '/'";
        if (TimeoutSeconds < 1) return "AuthenApi:TimeoutSeconds must be positive";
        return null;
    }

    /// <summary>Scheme + host + port only. A legacy "/api" suffix (older configuration) is removed.</summary>
    public string NormalizedBaseUrl
    {
        get
        {
            if (!Uri.TryCreate(BaseUrl?.Trim(), UriKind.Absolute, out var uri)) return string.Empty;
            return uri.GetLeftPart(UriPartial.Authority);
        }
    }
}

/// <summary>
/// Step 2 of the fallback: one LDAPS bind to a domain controller when AuthenAPI cannot be reached.
/// Uses the UPN stored from the user's last online login; users without a cache entry are not tried.
/// </summary>
public class DirectAdOptions
{
    public bool Enabled { get; set; } = false;

    /// <summary>Domain controller FQDN (the certificate is issued to the name, not the IP).</summary>
    public string Server { get; set; } = string.Empty;

    /// <summary>LDAPS only.</summary>
    public int Port { get; set; } = 636;

    public int TimeoutSeconds { get; set; } = 10;
}

public class LocalAdminOptions
{
    public bool Enabled { get; set; } = false;
    public string Username { get; set; } = "admin";

    /// <summary>PBKDF2 hash produced by: dotnet EmailRelayAPI.dll hash-password "your-password"</summary>
    public string PasswordHash { get; set; } = string.Empty;
}
