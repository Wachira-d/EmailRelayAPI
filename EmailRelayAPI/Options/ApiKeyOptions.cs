namespace EmailRelayAPI.Options;

/// <summary>Defaults for API key lifecycle. Rotation is opt-in per client.</summary>
public class ApiKeyOptions
{
    public const string SectionName = "ApiKeys";

    /// <summary>Prefix of generated keys (helps secret scanners and humans recognise them).</summary>
    public string KeyPrefix { get; set; } = "erk_";

    public bool DefaultRotationEnabled { get; set; } = false;
    public int DefaultRotationDays { get; set; } = 90;
    public int DefaultGraceDays { get; set; } = 7;

    /// <summary>How often the rotation service checks for keys due for rotation.</summary>
    public int RotationCheckIntervalMinutes { get; set; } = 60;
}
