using System.ComponentModel.DataAnnotations;

namespace EmailRelayAPI.Data.Entities;

public enum AuditCategory
{
    Authentication = 0,
    ApiKey = 1,
    Security = 2,
    Admin = 3,
    Delivery = 4,
    System = 5,
    Client = 6
}

public enum AuditSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2
}

/// <summary>Security / administrative audit trail.</summary>
public class AuditLog
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public AuditCategory Category { get; set; }
    public AuditSeverity Severity { get; set; } = AuditSeverity.Info;

    [MaxLength(100)]
    public string Action { get; set; } = string.Empty;
    public bool Success { get; set; } = true;

    public int? ClientId { get; set; }
    public int? ApiKeyId { get; set; }

    [MaxLength(256)]
    public string? Username { get; set; }

    [MaxLength(64)]
    public string? IpAddress { get; set; }

    [MaxLength(500)]
    public string? UserAgent { get; set; }

    [MaxLength(500)]
    public string? RequestPath { get; set; }

    [MaxLength(10)]
    public string? HttpMethod { get; set; }
    public int? StatusCode { get; set; }
    public long? DurationMs { get; set; }

    [MaxLength(4000)]
    public string? Details { get; set; }

    [MaxLength(64)]
    public string? TraceId { get; set; }

    [MaxLength(50)]
    public string? TargetType { get; set; }

    [MaxLength(100)]
    public string? TargetId { get; set; }
}

/// <summary>One row per API request (usage log).</summary>
public class RequestLog
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public int? ClientId { get; set; }
    public int? ApiKeyId { get; set; }

    [MaxLength(256)]
    public string? Username { get; set; }

    [MaxLength(64)]
    public string IpAddress { get; set; } = string.Empty;

    [MaxLength(10)]
    public string Method { get; set; } = string.Empty;

    [MaxLength(500)]
    public string Path { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Query { get; set; }
    public int StatusCode { get; set; }
    public long DurationMs { get; set; }
    public long? RequestBytes { get; set; }

    [MaxLength(500)]
    public string? UserAgent { get; set; }

    [MaxLength(64)]
    public string? TraceId { get; set; }

    [MaxLength(1000)]
    public string? Error { get; set; }
}
