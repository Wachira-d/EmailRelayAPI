using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EmailRelayAPI.Data.Entities;

/// <summary>A tenant / integration that is allowed to send mail through the relay.</summary>
public class ApiClient
{
    public int Id { get; set; }

    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Transport used for this client's mail (A → SMTP, B → API ...).</summary>
    public int? DefaultSenderProfileId { get; set; }
    public SenderProfile? DefaultSenderProfile { get; set; }

    /// <summary>Comma separated IPs / CIDR ranges allowed to use this client's keys. Empty = any.</summary>
    [MaxLength(2000)]
    public string? AllowedIpRanges { get; set; }

    /// <summary>Comma separated addresses or "@domain" entries the client may use as From. Empty = profile default only.</summary>
    [MaxLength(2000)]
    public string? AllowedFromAddresses { get; set; }

    /// <summary>Comma separated recipient domains the client may send to. Empty = any.</summary>
    [MaxLength(2000)]
    public string? AllowedRecipientDomains { get; set; }

    public int RateLimitPerMinute { get; set; } = 60;
    public int DailyQuota { get; set; } = 1000;
    public int MaxRecipientsPerMessage { get; set; } = 50;

    /// <summary>Require HMAC-SHA256 signed requests (X-Timestamp / X-Signature).</summary>
    public bool RequireSignedRequests { get; set; }

    /// <summary>Signing secret, protected with ASP.NET Data Protection.</summary>
    [MaxLength(1000)]
    public string? SigningSecretProtected { get; set; }

    /// <summary>Optional URL that receives delivery status callbacks.</summary>
    [MaxLength(1000)]
    public string? WebhookUrl { get; set; }

    [MaxLength(1000)]
    public string? WebhookSecretProtected { get; set; }

    public bool KeyRotationEnabled { get; set; }
    public int KeyRotationDays { get; set; } = 90;
    public int KeyRotationGraceDays { get; set; } = 7;

    [MaxLength(200)]
    public string? ContactEmail { get; set; }

    [MaxLength(500)]
    public string? Tags { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(100)]
    public string? CreatedBy { get; set; }

    public ICollection<ApiKey> ApiKeys { get; set; } = new List<ApiKey>();
}
