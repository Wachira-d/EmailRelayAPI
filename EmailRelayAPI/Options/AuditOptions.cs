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
}
