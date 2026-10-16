using System.ComponentModel.DataAnnotations;

namespace EmailRelayAPI.Data.Entities;

/// <summary>Application log entries (warnings, errors, exceptions) captured from ILogger for the console.</summary>
public class SystemLog
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [MaxLength(20)]
    public string Level { get; set; } = "Information";

    [MaxLength(200)]
    public string Category { get; set; } = string.Empty;

    [MaxLength(4000)]
    public string Message { get; set; } = string.Empty;

    public string? Exception { get; set; }

    [MaxLength(64)]
    public string? TraceId { get; set; }

    [MaxLength(500)]
    public string? RequestPath { get; set; }

    [MaxLength(10)]
    public string? HttpMethod { get; set; }

    public int EventId { get; set; }
}
