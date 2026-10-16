using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EmailRelayAPI.Data.Entities;

/// <summary>An API key. Only the SHA-256 hash is stored; the plain key is shown once at creation.</summary>
public class ApiKey
{
    public int Id { get; set; }

    public int ClientId { get; set; }
    public ApiClient Client { get; set; } = null!;

    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>First characters of the key, used for lookup and display.</summary>
    [MaxLength(16)]
    public string Prefix { get; set; } = string.Empty;

    [MaxLength(128)]
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>Comma separated scopes: send, read, admin.</summary>
    [MaxLength(200)]
    public string Scopes { get; set; } = "send,read";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    [MaxLength(500)]
    public string? RevokedReason { get; set; }

    public DateTime? LastUsedAt { get; set; }

    [MaxLength(64)]
    public string? LastUsedIp { get; set; }

    public long UseCount { get; set; }

    /// <summary>Set when this key was superseded by a rotation.</summary>
    public DateTime? RotatedAt { get; set; }
    public int? ReplacedByKeyId { get; set; }

    [MaxLength(100)]
    public string? CreatedBy { get; set; }

    [NotMapped]
    public bool IsActive => RevokedAt == null && (ExpiresAt == null || ExpiresAt > DateTime.UtcNow);

    public bool HasScope(string scope)
        => Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Any(s => s.Equals(scope, StringComparison.OrdinalIgnoreCase));
}
