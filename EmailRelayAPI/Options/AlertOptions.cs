namespace EmailRelayAPI.Options;

/// <summary>Operator alerting for critical security / delivery events.</summary>
public class AlertOptions
{
    public const string SectionName = "Alerts";

    public bool Enabled { get; set; } = false;

    /// <summary>Recipients of alert emails.</summary>
    public List<string> AdminEmails { get; set; } = new();

    /// <summary>Name of the sender profile used to send alert emails.</summary>
    public string? SenderProfileName { get; set; }

    /// <summary>Optional webhook (e.g. Teams / Slack / custom) that receives alert JSON.</summary>
    public string? WebhookUrl { get; set; }

    /// <summary>Same alert type is not repeated inside this window.</summary>
    public int ThrottleMinutes { get; set; } = 15;
}
