using System.ComponentModel.DataAnnotations;

namespace EmailRelayAPI.Data.Entities;

public enum BlockTargetType
{
    Ip = 0,
    Client = 1,
    ApiKey = 2
}

/// <summary>Temporary or permanent block of an IP, client or key (automatic or by an admin).</summary>
public class BlockEntry
{
    public int Id { get; set; }
    public BlockTargetType TargetType { get; set; }

    /// <summary>IP / CIDR, client id or key prefix depending on TargetType.</summary>
    [MaxLength(100)]
    public string Target { get; set; } = string.Empty;

    [MaxLength(500)]
    public string Reason { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public bool IsPermanent { get; set; }

    [MaxLength(100)]
    public string CreatedBy { get; set; } = "system";

    public DateTime? RemovedAt { get; set; }

    [MaxLength(100)]
    public string? RemovedBy { get; set; }

    public long HitCount { get; set; }

    public bool IsEffective(DateTime now)
        => RemovedAt == null && (IsPermanent || ExpiresAt == null || ExpiresAt > now);
}

/// <summary>Source IPs seen per client (new-IP anomaly detection and reporting).</summary>
public class ClientSeenIp
{
    public int Id { get; set; }
    public int ClientId { get; set; }

    [MaxLength(64)]
    public string IpAddress { get; set; } = string.Empty;
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    public long RequestCount { get; set; }
}
