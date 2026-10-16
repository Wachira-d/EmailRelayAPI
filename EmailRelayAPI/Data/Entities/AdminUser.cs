using System.ComponentModel.DataAnnotations;

namespace EmailRelayAPI.Data.Entities;

/// <summary>
/// Local cache of administrators verified through AuthenAPI (hybrid authentication, see
/// AuthenAPI docs/CLIENT_INTEGRATION_GUIDE.md). Only a PBKDF2 hash of the password is stored.
/// </summary>
public class AdminUser
{
    public int Id { get; set; }

    [MaxLength(256)]
    public string Username { get; set; } = string.Empty;

    [MaxLength(256)]
    public string PasswordHash { get; set; } = string.Empty;

    [MaxLength(128)]
    public string Salt { get; set; } = string.Empty;
    public int Iterations { get; set; } = 100_000;

    [MaxLength(200)]
    public string? DisplayName { get; set; }

    [MaxLength(320)]
    public string? Email { get; set; }

    [MaxLength(200)]
    public string? Department { get; set; }

    [MaxLength(200)]
    public string? Title { get; set; }

    /// <summary>JSON array of AD group names.</summary>
    public string GroupsJson { get; set; } = "[]";

    /// <summary>Admin or Viewer.</summary>
    [MaxLength(20)]
    public string Role { get; set; } = "Viewer";

    public bool IsEnabled { get; set; } = true;
    public DateTime? LastLogin { get; set; }

    [MaxLength(64)]
    public string? LastLoginIp { get; set; }
    public DateTime LastAdSync { get; set; } = DateTime.UtcNow;
    public DateTime CacheExpires { get; set; }
    public int FailedAttempts { get; set; }
    public DateTime? LockedUntil { get; set; }

    /// <summary>AuthenApi or Local.</summary>
    [MaxLength(20)]
    public string Source { get; set; } = "AuthenApi";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
