using System.ComponentModel.DataAnnotations;
using EmailRelayAPI.Data.Entities;

namespace EmailRelayAPI.Models;

// ---------------- auth ----------------

public class AdminLoginRequest
{
    [Required, MaxLength(256)]
    public string Username { get; set; } = string.Empty;

    [Required, MaxLength(256)]
    public string Password { get; set; } = string.Empty;
}

public class AdminSessionInfo
{
    public string Username { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public DateTime? LastLogin { get; set; }
    public string? Email { get; set; }
    public string? Department { get; set; }
}

public class AdminUserDto
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public string? Department { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public DateTime? LastLogin { get; set; }
    public string? LastLoginIp { get; set; }
    public DateTime LastAdSync { get; set; }
    public DateTime CacheExpires { get; set; }
    public DateTime? LockedUntil { get; set; }
    public List<string> Groups { get; set; } = new();
}

// ---------------- clients & keys ----------------

public class ClientDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public int? DefaultSenderProfileId { get; set; }
    public string? DefaultSenderProfileName { get; set; }
    public string? AllowedIpRanges { get; set; }
    public string? AllowedFromAddresses { get; set; }
    public string? AllowedRecipientDomains { get; set; }
    public int RateLimitPerMinute { get; set; }
    public int DailyQuota { get; set; }
    public int MaxRecipientsPerMessage { get; set; }
    public bool RequireSignedRequests { get; set; }
    public bool HasSigningSecret { get; set; }
    public string? WebhookUrl { get; set; }
    public bool HasWebhookSecret { get; set; }
    public bool KeyRotationEnabled { get; set; }
    public int KeyRotationDays { get; set; }
    public int KeyRotationGraceDays { get; set; }
    public string? ContactEmail { get; set; }
    public string? Tags { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public int ActiveKeyCount { get; set; }
    public int UsedToday { get; set; }
    public List<ApiKeyDto> Keys { get; set; } = new();
}

public class ClientUpsertRequest
{
    [Required, MaxLength(100), RegularExpression(@"^[A-Za-z0-9 _.\-]+$", ErrorMessage = "Name may contain letters, digits, spaces, '_', '.' and '-'")]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public int? DefaultSenderProfileId { get; set; }

    [MaxLength(2000)]
    public string? AllowedIpRanges { get; set; }

    [MaxLength(2000)]
    public string? AllowedFromAddresses { get; set; }

    [MaxLength(2000)]
    public string? AllowedRecipientDomains { get; set; }

    [Range(0, 100000)]
    public int RateLimitPerMinute { get; set; } = 60;

    [Range(0, 10000000)]
    public int DailyQuota { get; set; } = 1000;

    [Range(1, 1000)]
    public int MaxRecipientsPerMessage { get; set; } = 50;

    public bool RequireSignedRequests { get; set; }

    [MaxLength(1000), Url]
    public string? WebhookUrl { get; set; }

    /// <summary>Set to a new value to replace the webhook secret; null keeps the existing one.</summary>
    [MaxLength(200)]
    public string? WebhookSecret { get; set; }

    public bool KeyRotationEnabled { get; set; }

    [Range(1, 3650)]
    public int KeyRotationDays { get; set; } = 90;

    [Range(0, 365)]
    public int KeyRotationGraceDays { get; set; } = 7;

    [MaxLength(200)]
    public string? ContactEmail { get; set; }

    [MaxLength(500)]
    public string? Tags { get; set; }
}

public class ApiKeyDto
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public string? ClientName { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;
    public string Scopes { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public string? LastUsedIp { get; set; }
    public long UseCount { get; set; }
    public DateTime? RotatedAt { get; set; }
    public int? ReplacedByKeyId { get; set; }
    public string? CreatedBy { get; set; }
    public bool IsActive { get; set; }
    public DateTime? RotationDueAt { get; set; }
}

public class CreateApiKeyRequest
{
    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Comma separated: send, read, admin.</summary>
    [MaxLength(200)]
    public string? Scopes { get; set; }

    public DateTime? ExpiresAt { get; set; }
}

public class CreatedApiKeyResponse
{
    public ApiKeyDto Key { get; set; } = new();

    /// <summary>The plain API key. Shown only once.</summary>
    public string PlainKey { get; set; } = string.Empty;
}

public class RevokeKeyRequest
{
    [MaxLength(500)]
    public string? Reason { get; set; }
}

public class SigningSecretResponse
{
    public string SigningSecret { get; set; } = string.Empty;
    public string Note { get; set; } = "Store this secret securely. It is shown only once.";
}

// ---------------- sender profiles ----------------

public class SenderProfileDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Type { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string FromAddress { get; set; } = string.Empty;
    public string? FromName { get; set; }
    public string? ReplyTo { get; set; }
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; }
    public string SmtpSecurity { get; set; } = string.Empty;
    public string? SmtpUsername { get; set; }
    public bool HasSmtpPassword { get; set; }
    public bool SkipCertificateValidation { get; set; }
    public string? SmtpLocalDomain { get; set; }
    public string ApiProvider { get; set; } = string.Empty;
    public string? ApiEndpoint { get; set; }
    public bool HasApiKey { get; set; }
    public string? ApiDomain { get; set; }
    public string? OAuthTenantId { get; set; }
    public string? OAuthClientId { get; set; }
    public bool HasOAuthClientSecret { get; set; }
    public string? ExtraHeadersJson { get; set; }
    public int TimeoutSeconds { get; set; }
    public int MaxConcurrency { get; set; }
    public int MaxPerMinute { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public DateTime? LastFailureAt { get; set; }
    public string? LastError { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int ClientCount { get; set; }
    public int SentToday { get; set; }
    public int FailedToday { get; set; }
}

public class SenderProfileUpsertRequest
{
    [Required, MaxLength(100), RegularExpression(@"^[A-Za-z0-9 _.\-]+$", ErrorMessage = "Name may contain letters, digits, spaces, '_', '.' and '-'")]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    public TransportType Type { get; set; } = TransportType.Smtp;
    public bool IsActive { get; set; } = true;

    [Required, MaxLength(320)]
    public string FromAddress { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? FromName { get; set; }

    [MaxLength(320)]
    public string? ReplyTo { get; set; }

    [MaxLength(255)]
    public string? SmtpHost { get; set; }

    [Range(1, 65535)]
    public int SmtpPort { get; set; } = 587;
    public SmtpSecurity SmtpSecurity { get; set; } = SmtpSecurity.Auto;

    [MaxLength(255)]
    public string? SmtpUsername { get; set; }

    /// <summary>New password; null keeps the stored one, empty string clears it.</summary>
    [MaxLength(500)]
    public string? SmtpPassword { get; set; }
    public bool SkipCertificateValidation { get; set; }

    [MaxLength(255)]
    public string? SmtpLocalDomain { get; set; }

    public ApiProvider ApiProvider { get; set; } = ApiProvider.Generic;

    [MaxLength(1000)]
    public string? ApiEndpoint { get; set; }

    /// <summary>New API key; null keeps the stored one, empty string clears it.</summary>
    [MaxLength(1000)]
    public string? ApiKey { get; set; }

    [MaxLength(255)]
    public string? ApiDomain { get; set; }

    [MaxLength(100)]
    public string? OAuthTenantId { get; set; }

    [MaxLength(100)]
    public string? OAuthClientId { get; set; }

    [MaxLength(1000)]
    public string? OAuthClientSecret { get; set; }

    [MaxLength(2000)]
    public string? ExtraHeadersJson { get; set; }

    [Range(5, 600)]
    public int TimeoutSeconds { get; set; } = 30;

    [Range(1, 50)]
    public int MaxConcurrency { get; set; } = 2;

    [Range(0, 100000)]
    public int MaxPerMinute { get; set; } = 0;
}

public class TestSendRequest
{
    [Required, MaxLength(320)]
    public string To { get; set; } = string.Empty;
}

// ---------------- logs & security ----------------

public class AuditLogDto
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public bool Success { get; set; }
    public int? ClientId { get; set; }
    public string? ClientName { get; set; }
    public int? ApiKeyId { get; set; }
    public string? Username { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? RequestPath { get; set; }
    public string? HttpMethod { get; set; }
    public int? StatusCode { get; set; }
    public long? DurationMs { get; set; }
    public string? Details { get; set; }
    public string? TraceId { get; set; }
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
}

public class RequestLogDto
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public int? ClientId { get; set; }
    public string? ClientName { get; set; }
    public int? ApiKeyId { get; set; }
    public string? Username { get; set; }
    public string IpAddress { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string? Query { get; set; }
    public int StatusCode { get; set; }
    public long DurationMs { get; set; }
    public long? RequestBytes { get; set; }
    public string? UserAgent { get; set; }
    public string? TraceId { get; set; }
    public string? Error { get; set; }
}

public class BlockEntryDto
{
    public int Id { get; set; }
    public string TargetType { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public bool IsPermanent { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? RemovedAt { get; set; }
    public string? RemovedBy { get; set; }
    public long HitCount { get; set; }
    public bool IsActive { get; set; }
}

public class CreateBlockRequest
{
    [Required]
    public BlockTargetType TargetType { get; set; }

    [Required, MaxLength(100)]
    public string Target { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string Reason { get; set; } = string.Empty;

    /// <summary>Minutes; ignored when Permanent.</summary>
    [Range(1, 525600)]
    public int DurationMinutes { get; set; } = 60;
    public bool Permanent { get; set; }
}

public class ClientSeenIpDto
{
    public int ClientId { get; set; }
    public string? ClientName { get; set; }
    public string IpAddress { get; set; } = string.Empty;
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public long RequestCount { get; set; }
}

// ---------------- templates ----------------

public class TemplateDto
{
    public int Id { get; set; }
    public int? ClientId { get; set; }
    public string? ClientName { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string? HtmlBody { get; set; }
    public string? TextBody { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? CreatedBy { get; set; }
}

public class TemplateUpsertRequest
{
    public int? ClientId { get; set; }

    [Required, MaxLength(100), RegularExpression(@"^[A-Za-z0-9_.\-]+$", ErrorMessage = "Name may contain letters, digits, '_', '.' and '-'")]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    [Required, MaxLength(998)]
    public string Subject { get; set; } = string.Empty;
    public string? HtmlBody { get; set; }
    public string? TextBody { get; set; }
    public bool IsActive { get; set; } = true;
}

// ---------------- dashboard & reports ----------------

public class DashboardDto
{
    public int SentToday { get; set; }
    public int FailedToday { get; set; }
    public int QueuedNow { get; set; }
    public int ScheduledNow { get; set; }
    public int SendingNow { get; set; }
    public int AcceptedToday { get; set; }
    public int Sent7Days { get; set; }
    public int Failed7Days { get; set; }
    public double? AvgDurationMsToday { get; set; }
    public double SuccessRateToday { get; set; }
    public int ActiveClients { get; set; }
    public int ActiveKeys { get; set; }
    public int ActiveProfiles { get; set; }
    public int UnhealthyProfiles { get; set; }
    public int ActiveBlocks { get; set; }
    public int SecurityEvents24h { get; set; }
    public int CriticalEvents24h { get; set; }
    public int AuthFailures24h { get; set; }
    public int KeysDueForRotation { get; set; }
    public int QueueInFlight { get; set; }
    public int QueuePending { get; set; }
    public List<TimeBucketDto> Hourly { get; set; } = new();
    public List<TimeBucketDto> Daily { get; set; } = new();
    public List<ClientUsageDto> TopClients { get; set; } = new();
    public List<SenderProfileHealthDto> Profiles { get; set; } = new();
    public List<AuditLogDto> RecentSecurity { get; set; } = new();
    public List<EmailSummaryDto> RecentFailures { get; set; } = new();
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public DateTime StartedAt { get; set; }
    public string Version { get; set; } = string.Empty;
}

public class TimeBucketDto
{
    public DateTime Bucket { get; set; }
    public int Sent { get; set; }
    public int Failed { get; set; }
    public int Accepted { get; set; }
}

public class ClientUsageDto
{
    public int? ClientId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public int Accepted { get; set; }
    public int Sent { get; set; }
    public int Failed { get; set; }
    public int Queued { get; set; }
    public int Recipients { get; set; }
    public double? AvgDurationMs { get; set; }
    public int Requests { get; set; }
    public int DailyQuota { get; set; }
}

public class SenderProfileHealthDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool Healthy { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public DateTime? LastFailureAt { get; set; }
    public string? LastError { get; set; }
    public int SentToday { get; set; }
    public int FailedToday { get; set; }
}

public class UsageReportDto
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public List<ClientUsageDto> ByClient { get; set; } = new();
    public List<TimeBucketDto> ByDay { get; set; } = new();
    public List<StatusCountDto> ByStatus { get; set; } = new();
    public List<ProfileUsageDto> ByProfile { get; set; } = new();
    public List<ErrorCountDto> TopErrors { get; set; } = new();
}

public class StatusCountDto
{
    public string Status { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class ProfileUsageDto
{
    public int ProfileId { get; set; }
    public string ProfileName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public int Sent { get; set; }
    public int Failed { get; set; }
    public double? AvgDurationMs { get; set; }
}

public class ErrorCountDto
{
    public string Error { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class SystemInfoDto
{
    public string Version { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public string DatabaseProvider { get; set; } = string.Empty;
    public bool AuthenApiConfigured { get; set; }
    public string? AuthenApiBaseUrl { get; set; }
    public bool LocalAdminEnabled { get; set; }
    public bool AlertsEnabled { get; set; }
    public bool AnomalyDetectionEnabled { get; set; }
    public bool IpAllowListEnabled { get; set; }
    public bool GlobalRateLimitEnabled { get; set; }
    public int WorkerCount { get; set; }
    public int MaxAttempts { get; set; }
    public int MessageRetentionDays { get; set; }
    public int AuditRetentionDays { get; set; }
    public int RequestLogRetentionDays { get; set; }
    public string AttachmentStoragePath { get; set; } = string.Empty;
    public long AttachmentStorageBytes { get; set; }
    public string ApiKeyHeaderName { get; set; } = string.Empty;
    public int QueueInFlight { get; set; }
    public int QueuePending { get; set; }
}
