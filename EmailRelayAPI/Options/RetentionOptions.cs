namespace EmailRelayAPI.Options;

/// <summary>Data retention / auto purge settings.</summary>
public class RetentionOptions
{
    public const string SectionName = "Retention";

    public bool Enabled { get; set; } = true;
    public int MessageRetentionDays { get; set; } = 90;
    public int AuditRetentionDays { get; set; } = 365;
    public int RequestLogRetentionDays { get; set; } = 30;
    public int SystemLogRetentionDays { get; set; } = 30;
    public int PurgeIntervalHours { get; set; } = 24;

    /// <summary>Time of day (24h, local server time) to run the purge, e.g. "02:00". Null = interval from start.</summary>
    public string? PurgeTime { get; set; } = "02:00";
}
