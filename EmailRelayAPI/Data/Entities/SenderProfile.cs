using System.ComponentModel.DataAnnotations;

namespace EmailRelayAPI.Data.Entities;

public enum TransportType
{
    Smtp = 0,
    HttpApi = 1
}

public enum ApiProvider
{
    /// <summary>POST the canonical relay JSON to a custom endpoint.</summary>
    Generic = 0,
    SendGrid = 1,
    Mailgun = 2,
    MicrosoftGraph = 3
}

public enum SmtpSecurity
{
    Auto = 0,
    None = 1,
    StartTls = 2,
    SslOnConnect = 3,
    StartTlsWhenAvailable = 4
}

/// <summary>How mail leaves the relay: an SMTP server or an HTTP mail API.</summary>
public class SenderProfile
{
    public int Id { get; set; }

    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    public TransportType Type { get; set; } = TransportType.Smtp;
    public bool IsActive { get; set; } = true;

    [MaxLength(320)]
    public string FromAddress { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? FromName { get; set; }

    [MaxLength(320)]
    public string? ReplyTo { get; set; }

    // ---- SMTP ----
    [MaxLength(255)]
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public SmtpSecurity SmtpSecurity { get; set; } = SmtpSecurity.Auto;

    [MaxLength(255)]
    public string? SmtpUsername { get; set; }

    [MaxLength(2000)]
    public string? SmtpPasswordProtected { get; set; }
    public bool SkipCertificateValidation { get; set; }

    [MaxLength(255)]
    public string? SmtpLocalDomain { get; set; }

    // ---- HTTP API ----
    public ApiProvider ApiProvider { get; set; } = ApiProvider.Generic;

    [MaxLength(1000)]
    public string? ApiEndpoint { get; set; }

    [MaxLength(2000)]
    public string? ApiKeyProtected { get; set; }

    /// <summary>Mailgun sending domain.</summary>
    [MaxLength(255)]
    public string? ApiDomain { get; set; }

    [MaxLength(100)]
    public string? OAuthTenantId { get; set; }

    [MaxLength(100)]
    public string? OAuthClientId { get; set; }

    [MaxLength(2000)]
    public string? OAuthClientSecretProtected { get; set; }

    /// <summary>JSON object of extra HTTP headers for the Generic provider.</summary>
    [MaxLength(2000)]
    public string? ExtraHeadersJson { get; set; }

    // ---- common ----
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxConcurrency { get; set; } = 2;

    /// <summary>Outbound throttle for this profile; 0 = unlimited.</summary>
    public int MaxPerMinute { get; set; } = 0;

    // ---- health ----
    public DateTime? LastSuccessAt { get; set; }
    public DateTime? LastFailureAt { get; set; }

    [MaxLength(2000)]
    public string? LastError { get; set; }
    public int ConsecutiveFailures { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(100)]
    public string? CreatedBy { get; set; }
}
