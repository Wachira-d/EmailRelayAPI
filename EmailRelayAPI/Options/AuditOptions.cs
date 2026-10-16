namespace EmailRelayAPI.Options;

/// <summary>Audit logging settings.</summary>
public class AuditOptions
{
    public const string SectionName = "Audit";

    /// <summary>Also write audit events as JSON lines to daily files.</summary>
    public bool EnableFileLogging { get; set; } = true;
    public string LogDirectory { get; set; } = "logs";

    /// <summary>Record every API request to the RequestLogs table.</summary>
    public bool EnableRequestLogging { get; set; } = true;

    /// <summary>Minimum ILogger level stored in the SystemLogs table (Trace, Debug, Information, Warning, Error, Critical).</summary>
    public string SystemLogMinimumLevel { get; set; } = "Warning";

    /// <summary>Also store Information-level messages from the relay's own code (deliveries, logins, startup).</summary>
    public bool SystemLogIncludeAppInfo { get; set; } = true;
}
