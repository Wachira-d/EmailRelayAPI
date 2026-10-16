using System.Text.Json;
using EmailRelayAPI.Data.Entities;
using EmailRelayAPI.Security;

namespace EmailRelayAPI.Models;

public static class Mappers
{
    public static EmailSummaryDto ToSummary(EmailMessage m) => new()
    {
        Id = m.Id,
        Status = m.Status.ToApiString(),
        From = string.IsNullOrEmpty(m.FromName) ? m.FromAddress : $"{m.FromName} <{m.FromAddress}>",
        To = string.Join(", ", Services.EmailQueueService.Deserialize(m.ToJson).Select(a => a.Address)),
        Subject = m.Subject,
        Attempts = m.Attempts,
        CreatedAt = m.CreatedAt,
        SentAt = m.SentAt,
        DurationMs = m.DurationMs,
        LastError = m.LastError,
        ClientId = m.ClientId,
        ClientName = m.Client?.Name ?? (m.IsSystem ? "(system)" : null),
        SenderProfile = m.SenderProfile?.Name,
        RecipientCount = m.RecipientCount,
        Priority = m.Priority,
        HasAttachments = m.Attachments.Count > 0
    };

    public static EmailStatusResponse ToStatus(EmailMessage m) => new()
    {
        Id = m.Id,
        Status = m.Status.ToApiString(),
        From = string.IsNullOrEmpty(m.FromName) ? m.FromAddress : $"{m.FromName} <{m.FromAddress}>",
        To = Services.EmailQueueService.Deserialize(m.ToJson),
        Cc = string.IsNullOrEmpty(m.CcJson) ? null : Services.EmailQueueService.Deserialize(m.CcJson),
        Bcc = string.IsNullOrEmpty(m.BccJson) ? null : Services.EmailQueueService.Deserialize(m.BccJson),
        Subject = m.Subject,
        Attempts = m.Attempts,
        CreatedAt = m.CreatedAt,
        ScheduledAt = m.ScheduledAt,
        NextAttemptAt = m.Status == EmailStatus.Queued || m.Status == EmailStatus.Scheduled ? m.NextAttemptAt : null,
        SentAt = m.SentAt,
        DurationMs = m.DurationMs,
        LastError = m.LastError,
        ProviderMessageId = m.ProviderMessageId,
        SenderProfile = m.SenderProfile?.Name,
        IdempotencyKey = m.IdempotencyKey,
        Tags = string.IsNullOrEmpty(m.Tags) ? null : IpUtils.SplitList(m.Tags),
        Events = m.Events.OrderBy(e => e.Timestamp).ThenBy(e => e.Id).Select(e => new EmailEventDto
        {
            Timestamp = e.Timestamp,
            Type = e.Type,
            Details = e.Details,
            Attempt = e.Attempt,
            DurationMs = e.DurationMs
        }).ToList(),
        Attachments = m.Attachments.Select(a => new AttachmentInfoDto
        {
            FileName = a.FileName,
            ContentType = a.ContentType,
            Size = a.Size,
            IsInline = a.IsInline
        }).ToList()
    };

    public static ApiKeyDto ToDto(ApiKey k, ApiClient? client = null)
    {
        client ??= k.Client;
        return new ApiKeyDto
        {
            Id = k.Id,
            ClientId = k.ClientId,
            ClientName = client?.Name,
            Name = k.Name,
            Prefix = k.Prefix,
            Scopes = k.Scopes,
            CreatedAt = k.CreatedAt,
            ExpiresAt = k.ExpiresAt,
            RevokedAt = k.RevokedAt,
            RevokedReason = k.RevokedReason,
            LastUsedAt = k.LastUsedAt,
            LastUsedIp = k.LastUsedIp,
            UseCount = k.UseCount,
            RotatedAt = k.RotatedAt,
            ReplacedByKeyId = k.ReplacedByKeyId,
            CreatedBy = k.CreatedBy,
            IsActive = k.IsActive,
            RotationDueAt = client != null && client.KeyRotationEnabled && k.RotatedAt == null && k.RevokedAt == null
                ? k.CreatedAt.AddDays(client.KeyRotationDays)
                : null
        };
    }

    public static ClientDto ToDto(ApiClient c, bool includeKeys = true) => new()
    {
        Id = c.Id,
        Name = c.Name,
        Description = c.Description,
        IsActive = c.IsActive,
        DefaultSenderProfileId = c.DefaultSenderProfileId,
        DefaultSenderProfileName = c.DefaultSenderProfile?.Name,
        AllowedIpRanges = c.AllowedIpRanges,
        AllowedFromAddresses = c.AllowedFromAddresses,
        AllowedRecipientDomains = c.AllowedRecipientDomains,
        RateLimitPerMinute = c.RateLimitPerMinute,
        DailyQuota = c.DailyQuota,
        MaxRecipientsPerMessage = c.MaxRecipientsPerMessage,
        RequireSignedRequests = c.RequireSignedRequests,
        HasSigningSecret = !string.IsNullOrEmpty(c.SigningSecretProtected),
        WebhookUrl = c.WebhookUrl,
        HasWebhookSecret = !string.IsNullOrEmpty(c.WebhookSecretProtected),
        KeyRotationEnabled = c.KeyRotationEnabled,
        KeyRotationDays = c.KeyRotationDays,
        KeyRotationGraceDays = c.KeyRotationGraceDays,
        ContactEmail = c.ContactEmail,
        Tags = c.Tags,
        CreatedAt = c.CreatedAt,
        UpdatedAt = c.UpdatedAt,
        CreatedBy = c.CreatedBy,
        ActiveKeyCount = c.ApiKeys.Count(k => k.IsActive),
        Keys = includeKeys ? c.ApiKeys.OrderByDescending(k => k.CreatedAt).Select(k => ToDto(k, c)).ToList() : new()
    };

    public static SenderProfileDto ToDto(SenderProfile p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Description = p.Description,
        Type = p.Type.ToString(),
        IsActive = p.IsActive,
        FromAddress = p.FromAddress,
        FromName = p.FromName,
        ReplyTo = p.ReplyTo,
        SmtpHost = p.SmtpHost,
        SmtpPort = p.SmtpPort,
        SmtpSecurity = p.SmtpSecurity.ToString(),
        SmtpUsername = p.SmtpUsername,
        HasSmtpPassword = !string.IsNullOrEmpty(p.SmtpPasswordProtected),
        SkipCertificateValidation = p.SkipCertificateValidation,
        SmtpLocalDomain = p.SmtpLocalDomain,
        ApiProvider = p.ApiProvider.ToString(),
        ApiEndpoint = p.ApiEndpoint,
        HasApiKey = !string.IsNullOrEmpty(p.ApiKeyProtected),
        ApiDomain = p.ApiDomain,
        OAuthTenantId = p.OAuthTenantId,
        OAuthClientId = p.OAuthClientId,
        HasOAuthClientSecret = !string.IsNullOrEmpty(p.OAuthClientSecretProtected),
        ExtraHeadersJson = p.ExtraHeadersJson,
        TimeoutSeconds = p.TimeoutSeconds,
        MaxConcurrency = p.MaxConcurrency,
        MaxPerMinute = p.MaxPerMinute,
        LastSuccessAt = p.LastSuccessAt,
        LastFailureAt = p.LastFailureAt,
        LastError = p.LastError,
        ConsecutiveFailures = p.ConsecutiveFailures,
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt
    };

    public static AuditLogDto ToDto(AuditLog a, string? clientName = null) => new()
    {
        Id = a.Id,
        Timestamp = a.Timestamp,
        Category = a.Category.ToString(),
        Severity = a.Severity.ToString(),
        Action = a.Action,
        Success = a.Success,
        ClientId = a.ClientId,
        ClientName = clientName,
        ApiKeyId = a.ApiKeyId,
        Username = a.Username,
        IpAddress = a.IpAddress,
        UserAgent = a.UserAgent,
        RequestPath = a.RequestPath,
        HttpMethod = a.HttpMethod,
        StatusCode = a.StatusCode,
        DurationMs = a.DurationMs,
        Details = a.Details,
        TraceId = a.TraceId,
        TargetType = a.TargetType,
        TargetId = a.TargetId
    };

    public static RequestLogDto ToDto(RequestLog r, string? clientName = null) => new()
    {
        Id = r.Id,
        Timestamp = r.Timestamp,
        ClientId = r.ClientId,
        ClientName = clientName,
        ApiKeyId = r.ApiKeyId,
        Username = r.Username,
        IpAddress = r.IpAddress,
        Method = r.Method,
        Path = r.Path,
        Query = r.Query,
        StatusCode = r.StatusCode,
        DurationMs = r.DurationMs,
        RequestBytes = r.RequestBytes,
        UserAgent = r.UserAgent,
        TraceId = r.TraceId,
        Error = r.Error
    };

    public static BlockEntryDto ToDto(BlockEntry b) => new()
    {
        Id = b.Id,
        TargetType = b.TargetType.ToString(),
        Target = b.Target,
        Reason = b.Reason,
        CreatedAt = b.CreatedAt,
        ExpiresAt = b.ExpiresAt,
        IsPermanent = b.IsPermanent,
        CreatedBy = b.CreatedBy,
        RemovedAt = b.RemovedAt,
        RemovedBy = b.RemovedBy,
        HitCount = b.HitCount,
        IsActive = b.IsEffective(DateTime.UtcNow)
    };

    public static TemplateDto ToDto(EmailTemplate t, string? clientName = null) => new()
    {
        Id = t.Id,
        ClientId = t.ClientId,
        ClientName = clientName,
        Name = t.Name,
        Description = t.Description,
        Subject = t.Subject,
        HtmlBody = t.HtmlBody,
        TextBody = t.TextBody,
        IsActive = t.IsActive,
        CreatedAt = t.CreatedAt,
        UpdatedAt = t.UpdatedAt,
        CreatedBy = t.CreatedBy
    };

    public static AdminUserDto ToDto(AdminUser u) => new()
    {
        Id = u.Id,
        Username = u.Username,
        DisplayName = u.DisplayName,
        Email = u.Email,
        Department = u.Department,
        Role = u.Role,
        Source = u.Source,
        IsEnabled = u.IsEnabled,
        LastLogin = u.LastLogin,
        LastLoginIp = u.LastLoginIp,
        LastAdSync = u.LastAdSync,
        CacheExpires = u.CacheExpires,
        LockedUntil = u.LockedUntil,
        Groups = TryParseList(u.GroupsJson)
    };

    private static List<string> TryParseList(string? json)
    {
        try
        {
            return string.IsNullOrEmpty(json) ? new() : JsonSerializer.Deserialize<List<string>>(json) ?? new();
        }
        catch
        {
            return new();
        }
    }
}
