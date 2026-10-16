namespace EmailRelayAPI.Options;

/// <summary>Email delivery pipeline settings.</summary>
public class DeliveryOptions
{
    public const string SectionName = "Delivery";

    /// <summary>Parallel delivery workers.</summary>
    public int WorkerCount { get; set; } = 4;

    /// <summary>Maximum delivery attempts (including the first).</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Exponential back-off base delay.</summary>
    public int RetryBaseDelaySeconds { get; set; } = 30;

    /// <summary>Upper bound for the back-off delay.</summary>
    public int RetryMaxDelaySeconds { get; set; } = 1800;

    /// <summary>How often the database is scanned for due / recovered messages.</summary>
    public int PollIntervalSeconds { get; set; } = 15;

    /// <summary>Where attachments are stored on disk.</summary>
    public string AttachmentStoragePath { get; set; } = "data/attachments";

    /// <summary>Maximum total attachment size per message (bytes).</summary>
    public long MaxAttachmentBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Hard limit on recipients (to+cc+bcc) per message; clients can be configured lower.</summary>
    public int MaxRecipientsPerMessage { get; set; } = 100;

    /// <summary>Maximum messages in one batch call.</summary>
    public int MaxBatchSize { get; set; } = 100;

    /// <summary>Timeout for client status webhooks.</summary>
    public int WebhookTimeoutSeconds { get; set; } = 10;

    /// <summary>Messages stuck in "Sending" longer than this are re-queued (crash recovery).</summary>
    public int StuckSendingMinutes { get; set; } = 10;

    /// <summary>Sender profile is marked unhealthy after this many consecutive failures.</summary>
    public int ProfileUnhealthyThreshold { get; set; } = 5;

    /// <summary>Attachment extensions that are always rejected.</summary>
    public List<string> BlockedAttachmentExtensions { get; set; } = new()
    {
        ".exe", ".dll", ".bat", ".cmd", ".com", ".scr", ".pif", ".msi", ".js", ".jse", ".vbs", ".vbe", ".ps1", ".wsf", ".hta", ".cpl", ".jar", ".lnk", ".reg"
    };

    /// <summary>Maximum attachments per message.</summary>
    public int MaxAttachmentsPerMessage { get; set; } = 20;
}
