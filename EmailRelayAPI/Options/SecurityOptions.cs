namespace EmailRelayAPI.Options;

/// <summary>Global security configuration (follows the AuthenAPI conventions).</summary>
public class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>Header that carries the client API key.</summary>
    public string ApiKeyHeaderName { get; set; } = "X-API-Key";

    /// <summary>Also accept the key as "Authorization: Bearer &lt;key&gt;".</summary>
    public bool AllowBearerToken { get; set; } = true;

    /// <summary>Enable the global IP allow list (in addition to per-client allow lists).</summary>
    public bool EnableIpAllowList { get; set; } = false;

    /// <summary>Allowed IPs / CIDR ranges when the global allow list is enabled. Loopback is always allowed.</summary>
    public List<string> AllowedIpAddresses { get; set; } = new();

    /// <summary>Honour X-Forwarded-For / X-Forwarded-Proto from these proxies (IP or CIDR). Empty = ignore forwarded headers.</summary>
    public List<string> KnownProxies { get; set; } = new();

    /// <summary>Pre-authentication limit per source IP per minute (protects against unauthenticated floods).</summary>
    public bool EnableGlobalRateLimit { get; set; } = true;
    public int GlobalRateLimitPerMinute { get; set; } = 600;

    /// <summary>Maximum request body size in bytes (attachments included).</summary>
    public long MaxRequestBodyBytes { get; set; } = 30 * 1024 * 1024;

    /// <summary>Paths that bypass IP filtering, rate limiting and request logging.</summary>
    public List<string> ExcludedPaths { get; set; } = new() { "/health", "/swagger", "/admin", "/lib", "/favicon.ico" };

    /// <summary>Accepted clock skew for signed requests (X-Timestamp).</summary>
    public int SignatureToleranceSeconds { get; set; } = 300;

    /// <summary>Redirect HTTP to HTTPS and emit HSTS.</summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>Default scopes for new keys.</summary>
    public string DefaultKeyScopes { get; set; } = "send,read";

    /// <summary>How long an API key lookup stays cached in memory.</summary>
    public int ApiKeyCacheSeconds { get; set; } = 60;
}
