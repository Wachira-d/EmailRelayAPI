namespace EmailRelayAPI.Options;

/// <summary>Admin login via AuthenAPI (Active Directory) with hybrid local cache.</summary>
public class AuthenApiOptions
{
    public const string SectionName = "AuthenApi";

    /// <summary>Base URL of AuthenAPI, e.g. https://authen.company.local/api</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>API key issued by AuthenAPI.</summary>
    public string ApiKey { get; set; } = string.Empty;
    public string ApiKeyHeaderName { get; set; } = "X-API-Key";
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Cache verified credentials locally (hashed) so repeat logins do not hit AD.</summary>
    public bool CacheEnabled { get; set; } = true;
    public int CacheDurationDays { get; set; } = 90;

    /// <summary>Allow login from cache when AuthenAPI is unreachable (even if the cache entry expired).</summary>
    public bool AllowOfflineLogin { get; set; } = true;

    /// <summary>AD groups mapped to the Admin role. Empty = every authenticated user is Admin.</summary>
    public List<string> AdminGroups { get; set; } = new();

    /// <summary>AD groups mapped to the read-only Viewer role.</summary>
    public List<string> ViewerGroups { get; set; } = new();

    public int MaxFailedAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public int SessionTimeoutMinutes { get; set; } = 480;

    /// <summary>Break-glass local administrator used when AuthenAPI is not configured or unavailable.</summary>
    public LocalAdminOptions LocalAdmin { get; set; } = new();
}

public class LocalAdminOptions
{
    public bool Enabled { get; set; } = false;
    public string Username { get; set; } = "admin";

    /// <summary>PBKDF2 hash produced by: dotnet run -- hash-password "your-password"</summary>
    public string PasswordHash { get; set; } = string.Empty;
}
