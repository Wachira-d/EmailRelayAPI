namespace EmailRelayAPI.Options;

/// <summary>Database provider configuration.</summary>
public class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>"Sqlite" (default) or "SqlServer".</summary>
    public string Provider { get; set; } = "Sqlite";

    /// <summary>Connection string for the selected provider.</summary>
    public string ConnectionString { get; set; } = "Data Source=data/emailrelay.db";
}
