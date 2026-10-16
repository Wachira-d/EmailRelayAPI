using System.ComponentModel.DataAnnotations;

namespace EmailRelayAPI.Data.Entities;

public enum EmailStatus
{
    Queued = 0,
    Scheduled = 1,
    Sending = 2,
    Sent = 3,
    Failed = 4,
    Cancelled = 5
}

/// <summary>A message accepted by the relay. Doubles as the delivery log.</summary>
public class EmailMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Null for system generated mail (alerts).</summary>
    public int? ClientId { get; set; }
    public ApiClient? Client { get; set; }

    public int? ApiKeyId { get; set; }

    public int SenderProfileId { get; set; }
    public SenderProfile SenderProfile { get; set; } = null!;

    [MaxLength(200)]
    public string? IdempotencyKey { get; set; }

    [MaxLength(320)]
    public string FromAddress { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? FromName { get; set; }

    [MaxLength(320)]
    public string? ReplyTo { get; set; }

    /// <summary>JSON array of {address,name}.</summary>
    public string ToJson { get; set; } = "[]";
    public string? CcJson { get; set; }
    public string? BccJson { get; set; }

    [MaxLength(998)]
    public string Subject { get; set; } = string.Empty;

    public string? TextBody { get; set; }
    public string? HtmlBody { get; set; }

    /// <summary>JSON object of custom headers.</summary>
    public string? HeadersJson { get; set; }

    [MaxLength(500)]
    public string? Tags { get; set; }

    /// <summary>0 = normal, 1 = high (dispatched first).</summary>
    public int Priority { get; set; }

    public int RecipientCount { get; set; }

    /// <summary>SHA-256 of recipients + subject + body, used for duplicate detection.</summary>
    [MaxLength(64)]
    public string ContentHash { get; set; } = string.Empty;

    public EmailStatus Status { get; set; } = EmailStatus.Queued;
    public int Attempts { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public DateTime? ScheduledAt { get; set; }

    [MaxLength(2000)]
    public string? LastError { get; set; }

    [MaxLength(500)]
    public string? ProviderMessageId { get; set; }

    [MaxLength(2000)]
    public string? ProviderResponse { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public long? DurationMs { get; set; }

    [MaxLength(64)]
    public string? RequestIp { get; set; }

    [MaxLength(64)]
    public string? TraceId { get; set; }

    public long TotalSize { get; set; }
    public bool IsSystem { get; set; }

    public ICollection<EmailAttachment> Attachments { get; set; } = new List<EmailAttachment>();
    public ICollection<EmailEvent> Events { get; set; } = new List<EmailEvent>();
}

public class EmailAttachment
{
    public int Id { get; set; }
    public Guid MessageId { get; set; }
    public EmailMessage Message { get; set; } = null!;

    [MaxLength(255)]
    public string FileName { get; set; } = string.Empty;

    [MaxLength(255)]
    public string ContentType { get; set; } = "application/octet-stream";

    public long Size { get; set; }

    [MaxLength(1000)]
    public string StoragePath { get; set; } = string.Empty;

    [MaxLength(255)]
    public string? ContentId { get; set; }
    public bool IsInline { get; set; }

    [MaxLength(64)]
    public string? Sha256 { get; set; }
}

/// <summary>Timeline entry for a message (queued, attempt, sent, failed, provider events ...).</summary>
public class EmailEvent
{
    public long Id { get; set; }
    public Guid MessageId { get; set; }
    public EmailMessage Message { get; set; } = null!;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [MaxLength(50)]
    public string Type { get; set; } = string.Empty;

    [MaxLength(4000)]
    public string? Details { get; set; }
    public int? Attempt { get; set; }
    public long? DurationMs { get; set; }

    [MaxLength(100)]
    public string? Source { get; set; }
}
