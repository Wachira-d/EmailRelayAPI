using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using EmailRelayAPI.Data.Entities;

namespace EmailRelayAPI.Models;

/// <summary>An email address with optional display name. Accepts either {"address":"a@b.c","name":"X"} or a plain string.</summary>
[JsonConverter(typeof(EmailAddressJsonConverter))]
public class EmailAddressDto
{
    [Required, MaxLength(320)]
    public string Address { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Name { get; set; }

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Address : $"{Name} <{Address}>";
}

/// <summary>Allows "to": ["a@b.c", {"address":"d@e.f","name":"D"}].</summary>
public class EmailAddressJsonConverter : JsonConverter<EmailAddressDto>
{
    public override EmailAddressDto? Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType == System.Text.Json.JsonTokenType.String)
        {
            var raw = reader.GetString() ?? string.Empty;
            // "Name <address>" form
            var lt = raw.LastIndexOf('<');
            var gt = raw.LastIndexOf('>');
            if (lt >= 0 && gt > lt)
            {
                return new EmailAddressDto
                {
                    Name = raw[..lt].Trim().Trim('"'),
                    Address = raw[(lt + 1)..gt].Trim()
                };
            }
            return new EmailAddressDto { Address = raw.Trim() };
        }

        if (reader.TokenType == System.Text.Json.JsonTokenType.Null)
        {
            return null;
        }

        string? address = null, name = null;
        if (reader.TokenType != System.Text.Json.JsonTokenType.StartObject)
        {
            throw new System.Text.Json.JsonException("Email address must be a string or an object");
        }

        while (reader.Read())
        {
            if (reader.TokenType == System.Text.Json.JsonTokenType.EndObject) break;
            if (reader.TokenType != System.Text.Json.JsonTokenType.PropertyName) continue;
            var prop = reader.GetString();
            reader.Read();
            var val = reader.TokenType == System.Text.Json.JsonTokenType.String ? reader.GetString() : null;
            if (string.Equals(prop, "address", StringComparison.OrdinalIgnoreCase) || string.Equals(prop, "email", StringComparison.OrdinalIgnoreCase))
                address = val;
            else if (string.Equals(prop, "name", StringComparison.OrdinalIgnoreCase))
                name = val;
        }

        return new EmailAddressDto { Address = (address ?? string.Empty).Trim(), Name = name };
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, EmailAddressDto value, System.Text.Json.JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("address", value.Address);
        if (!string.IsNullOrEmpty(value.Name)) writer.WriteString("name", value.Name);
        writer.WriteEndObject();
    }
}

public class AttachmentDto
{
    [Required, MaxLength(255)]
    public string FileName { get; set; } = string.Empty;

    [MaxLength(255)]
    public string? ContentType { get; set; }

    /// <summary>Base64 encoded content.</summary>
    [Required]
    public string ContentBase64 { get; set; } = string.Empty;

    /// <summary>Set for inline images referenced as cid:... in the HTML body.</summary>
    [MaxLength(255)]
    public string? ContentId { get; set; }
}

/// <summary>Request body for POST /api/v1/emails.</summary>
public class SendEmailRequest
{
    /// <summary>Optional From override; must be allowed for the client. Defaults to the sender profile From.</summary>
    public EmailAddressDto? From { get; set; }
    public EmailAddressDto? ReplyTo { get; set; }

    public List<EmailAddressDto> To { get; set; } = new();
    public List<EmailAddressDto>? Cc { get; set; }
    public List<EmailAddressDto>? Bcc { get; set; }

    [MaxLength(998)]
    public string? Subject { get; set; }

    /// <summary>Plain text body.</summary>
    public string? Text { get; set; }

    /// <summary>HTML body.</summary>
    public string? Html { get; set; }

    public List<AttachmentDto>? Attachments { get; set; }

    /// <summary>Custom headers (X-* only).</summary>
    public Dictionary<string, string>? Headers { get; set; }

    public List<string>? Tags { get; set; }

    /// <summary>Same key within 24h returns the original message instead of sending twice.</summary>
    [MaxLength(200)]
    public string? IdempotencyKey { get; set; }

    /// <summary>0 = normal, 1 = high.</summary>
    [Range(0, 1)]
    public int Priority { get; set; }

    /// <summary>Deliver at or after this time (UTC).</summary>
    public DateTime? ScheduleAt { get; set; }

    /// <summary>Use a stored template (by name). Subject/body are rendered from it when omitted.</summary>
    [MaxLength(100)]
    public string? Template { get; set; }

    /// <summary>Values for template placeholders.</summary>
    public Dictionary<string, string>? Variables { get; set; }

    /// <summary>Override the client's default sender profile by name (must exist and be active).</summary>
    [MaxLength(100)]
    public string? SenderProfile { get; set; }
}

public class SendBatchRequest
{
    [Required]
    public List<SendEmailRequest> Messages { get; set; } = new();
}

public class EmailAcceptedResponse
{
    public Guid Id { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? ScheduledAt { get; set; }
    public bool Duplicate { get; set; }
}

public class BatchItemResult
{
    public int Index { get; set; }
    public bool Accepted { get; set; }
    public Guid? Id { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
}

public class EmailStatusResponse
{
    public Guid Id { get; set; }
    public string Status { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public List<EmailAddressDto> To { get; set; } = new();
    public List<EmailAddressDto>? Cc { get; set; }
    public List<EmailAddressDto>? Bcc { get; set; }
    public string Subject { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ScheduledAt { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public DateTime? SentAt { get; set; }
    public long? DurationMs { get; set; }
    public string? LastError { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? SenderProfile { get; set; }
    public string? IdempotencyKey { get; set; }
    public List<string>? Tags { get; set; }
    public List<EmailEventDto> Events { get; set; } = new();
    public List<AttachmentInfoDto> Attachments { get; set; } = new();
}

public class EmailEventDto
{
    public DateTime Timestamp { get; set; }
    public string Type { get; set; } = string.Empty;
    public string? Details { get; set; }
    public int? Attempt { get; set; }
    public long? DurationMs { get; set; }
}

public class AttachmentInfoDto
{
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long Size { get; set; }
    public bool IsInline { get; set; }
}

public class EmailSummaryDto
{
    public Guid Id { get; set; }
    public string Status { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public long? DurationMs { get; set; }
    public string? LastError { get; set; }
    public int? ClientId { get; set; }
    public string? ClientName { get; set; }
    public string? SenderProfile { get; set; }
    public int RecipientCount { get; set; }
    public int Priority { get; set; }
    public bool HasAttachments { get; set; }
}

public class ClientSelfInfo
{
    public int ClientId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? SenderProfile { get; set; }
    public string? DefaultFrom { get; set; }
    public int RateLimitPerMinute { get; set; }
    public int DailyQuota { get; set; }
    public int UsedToday { get; set; }
    public int MaxRecipientsPerMessage { get; set; }
    public bool RequireSignedRequests { get; set; }
    public List<string> Scopes { get; set; } = new();
    public string KeyName { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = string.Empty;
    public DateTime? KeyExpiresAt { get; set; }
    public bool KeyRotationEnabled { get; set; }
    public DateTime? KeyRotationDueAt { get; set; }
}

public class KeyRotationResponse
{
    public string NewKey { get; set; } = string.Empty;
    public string NewKeyPrefix { get; set; } = string.Empty;
    public DateTime OldKeyExpiresAt { get; set; }
    public string Note { get; set; } = string.Empty;
}

public static class EmailStatusExtensions
{
    public static string ToApiString(this EmailStatus status) => status.ToString().ToLowerInvariant();
}
