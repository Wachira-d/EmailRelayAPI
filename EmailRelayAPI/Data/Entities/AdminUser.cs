using System.ComponentModel.DataAnnotations;

namespace EmailRelayAPI.Data.Entities;

/// <summary>
/// A console user known from AuthenAPI. Keyed by the user key (lower-case UPN, else sAMAccountName),
/// never by what the user typed. Holds the profile from the last online login and, for non-admin roles,
/// an offline credential (HMAC-pepper + PBKDF2) that may only be used while AuthenAPI is unreachable.
/// </summary>
public class AdminUser
{
    public int Id { get; set; }

    /// <summary>User key: lower-case UPN, or lower-case sAMAccountName when the UPN is empty.</summary>
    [MaxLength(256)]
    public string Username { get; set; } = string.Empty;

    [MaxLength(256)]
    public string? UserPrincipalName { get; set; }

    [MaxLength(256)]
    public string? SamAccountName { get; set; }

    /// <summary>Offline credential "v2$iterations$salt$hash"; empty when the user has no offline credential.</summary>
    [MaxLength(256)]
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Legacy column (v1 cache). Unused.</summary>
    [MaxLength(128)]
    public string Salt { get; set; } = string.Empty;

    /// <summary>Legacy column (v1 cache). Unused.</summary>
    public int Iterations { get; set; }

    [MaxLength(200)]
    public string? DisplayName { get; set; }

    [MaxLength(320)]
    public string? Email { get; set; }

    [MaxLength(200)]
    public string? Department { get; set; }

    [MaxLength(200)]
    public string? Title { get; set; }

    /// <summary>JSON array of AD group names from the last online login.</summary>
    public string GroupsJson { get; set; } = "[]";

    /// <summary>Admin or Viewer, from the last online login.</summary>
    [MaxLength(20)]
    public string Role { get; set; } = "Viewer";

    /// <summary>Console-level switch; a disabled user cannot sign in even if AuthenAPI accepts them.</summary>
    public bool IsEnabled { get; set; } = true;

    public DateTime? LastLogin { get; set; }

    [MaxLength(64)]
    public string? LastLoginIp { get; set; }

    [MaxLength(20)]
    public string? LastLoginMode { get; set; }

    /// <summary>Last successful online verification by AuthenAPI (UTC). The offline window counts from here.</summary>
    public DateTime LastAdSync { get; set; } = DateTime.UtcNow;

    /// <summary>Legacy column (v1 cache). Unused.</summary>
    public DateTime CacheExpires { get; set; }

    /// <summary>Legacy column (v1 cache). Unused.</summary>
    public int FailedAttempts { get; set; }

    /// <summary>Wrong offline passwords since the last success; reserved atomically before each offline check.</summary>
    public int OfflineFailures { get; set; }

    /// <summary>Legacy column (v1 cache). Unused.</summary>
    public DateTime? LockedUntil { get; set; }

    /// <summary>AuthenApi or Local.</summary>
    [MaxLength(20)]
    public string Source { get; set; } = "AuthenApi";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<AdminUserAlias> Aliases { get; set; } = new List<AdminUserAlias>();

    public bool HasOfflineCredential => PasswordHash.StartsWith("v2$", StringComparison.Ordinal);
}

/// <summary>A login name the user has typed (trimmed, lower-case), mapped to their user key.</summary>
public class AdminUserAlias
{
    [MaxLength(256)]
    public string LoginName { get; set; } = string.Empty;

    public int AdminUserId { get; set; }
    public AdminUser AdminUser { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
}
