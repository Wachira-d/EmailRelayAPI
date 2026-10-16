namespace EmailRelayAPI.Options;

/// <summary>Abuse / anomaly detection thresholds.</summary>
public class AnomalyOptions
{
    public const string SectionName = "Anomaly";

    public bool Enabled { get; set; } = true;

    /// <summary>Automatically block the offender when a Critical anomaly is detected.</summary>
    public bool AutoBlock { get; set; } = true;

    /// <summary>Duration of an automatic block.</summary>
    public int BlockDurationMinutes { get; set; } = 30;

    /// <summary>Failed authentications (401/403) from one IP inside the window before blocking.</summary>
    public int AuthFailureThreshold { get; set; } = 10;
    public int AuthFailureWindowMinutes { get; set; } = 10;

    /// <summary>Requests from one IP or client inside BurstWindowSeconds that count as a burst ("ยิงซ้ำ").</summary>
    public int BurstThreshold { get; set; } = 100;
    public int BurstWindowSeconds { get; set; } = 10;

    /// <summary>Identical messages (same client, recipients, subject and body) inside the window.</summary>
    public int DuplicateThreshold { get; set; } = 5;
    public int DuplicateWindowMinutes { get; set; } = 10;

    /// <summary>Reject (409) duplicate messages once the threshold is hit instead of only flagging them.</summary>
    public bool RejectDuplicates { get; set; } = true;

    /// <summary>Total recipients a client may address inside the window before flagging.</summary>
    public int RecipientSpikeThreshold { get; set; } = 2000;
    public int RecipientSpikeWindowMinutes { get; set; } = 10;

    /// <summary>4xx responses from one client inside the window before flagging (misbehaving integration).</summary>
    public int ErrorRateThreshold { get; set; } = 100;
    public int ErrorRateWindowMinutes { get; set; } = 5;

    /// <summary>Log an informational security event when a client calls from an IP never seen before.</summary>
    public bool NotifyNewSourceIp { get; set; } = true;
}
