using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmailRelayAPI.Data;
using EmailRelayAPI.Data.Entities;
using EmailRelayAPI.Models;
using EmailRelayAPI.Options;
using EmailRelayAPI.Security;
using EmailRelayAPI.Services.Transports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EmailRelayAPI.Services;

public sealed class AcceptResult
{
    public bool Success { get; init; }
    public int StatusCode { get; init; } = 202;
    public string? Error { get; init; }
    public EmailMessage? Message { get; init; }
    public bool Duplicate { get; init; }

    public static AcceptResult Fail(int status, string error) => new() { Success = false, StatusCode = status, Error = error };
}

public interface IEmailQueueService
{
    Task<AcceptResult> AcceptAsync(SendEmailRequest request, ClientContext client, string ip, string traceId, CancellationToken ct = default);
    Task<EmailMessage?> AcceptSystemAsync(string senderProfileName, OutboundEmail email, CancellationToken ct = default);
    Task<bool> CancelAsync(Guid id, int? clientId, string by, CancellationToken ct = default);
    Task<bool> RequeueAsync(Guid id, string by, CancellationToken ct = default);
    Task<OutboundEmail> ToOutboundAsync(EmailMessage message, CancellationToken ct = default);
    void DeleteAttachmentFiles(EmailMessage message);
}

/// <summary>Validates incoming send requests, persists them and hands them to the dispatcher.</summary>
public class EmailQueueService : IEmailQueueService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDeliveryQueue _queue;
    private readonly IDailyQuotaService _quota;
    private readonly IAnomalyDetector _anomaly;
    private readonly ITemplateRenderer _templates;
    private readonly IAuditService _audit;
    private readonly IOptionsMonitor<DeliveryOptions> _options;
    private readonly ILogger<EmailQueueService> _logger;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public EmailQueueService(IServiceScopeFactory scopeFactory, IDeliveryQueue queue, IDailyQuotaService quota, IAnomalyDetector anomaly, ITemplateRenderer templates, IAuditService audit, IOptionsMonitor<DeliveryOptions> options, ILogger<EmailQueueService> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _quota = quota;
        _anomaly = anomaly;
        _templates = templates;
        _audit = audit;
        _options = options;
        _logger = logger;
    }

    public async Task<AcceptResult> AcceptAsync(SendEmailRequest request, ClientContext cc, string ip, string traceId, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        var client = cc.Client;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // ---- idempotency ----
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var existing = await db.Messages.AsNoTracking()
                .FirstOrDefaultAsync(m => m.ClientId == client.Id && m.IdempotencyKey == request.IdempotencyKey, ct);
            if (existing != null)
            {
                return new AcceptResult { Success = true, StatusCode = 200, Message = existing, Duplicate = true };
            }
        }

        // ---- sender profile ----
        SenderProfile? profile;
        if (!string.IsNullOrWhiteSpace(request.SenderProfile))
        {
            profile = await db.SenderProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Name == request.SenderProfile, ct);
            if (profile == null) return AcceptResult.Fail(422, $"Sender profile '{request.SenderProfile}' does not exist");
        }
        else if (client.DefaultSenderProfileId.HasValue)
        {
            profile = await db.SenderProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == client.DefaultSenderProfileId.Value, ct);
            if (profile == null) return AcceptResult.Fail(422, "The client's default sender profile no longer exists");
        }
        else
        {
            return AcceptResult.Fail(422, "No sender profile is configured for this client. Ask an administrator to assign one.");
        }
        if (!profile.IsActive) return AcceptResult.Fail(422, $"Sender profile '{profile.Name}' is disabled");

        // ---- template ----
        var subject = request.Subject;
        var text = request.Text;
        var html = request.Html;
        if (!string.IsNullOrWhiteSpace(request.Template))
        {
            var candidates = await db.Templates.AsNoTracking()
                .Where(t => t.Name == request.Template && t.IsActive && (t.ClientId == client.Id || t.ClientId == null))
                .ToListAsync(ct);
            var template = candidates.OrderByDescending(t => t.ClientId.HasValue).FirstOrDefault();
            if (template == null) return AcceptResult.Fail(422, $"Template '{request.Template}' not found");

            var vars = request.Variables ?? new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(subject)) subject = _templates.Render(template.Subject, vars, htmlEncode: false);
            if (string.IsNullOrWhiteSpace(html) && !string.IsNullOrEmpty(template.HtmlBody)) html = _templates.Render(template.HtmlBody, vars, htmlEncode: true);
            if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrEmpty(template.TextBody)) text = _templates.Render(template.TextBody, vars, htmlEncode: false);
        }
        else if (request.Variables is { Count: > 0 })
        {
            // allow placeholders in ad-hoc bodies too
            if (!string.IsNullOrEmpty(subject)) subject = _templates.Render(subject, request.Variables, false);
            if (!string.IsNullOrEmpty(html)) html = _templates.Render(html, request.Variables, true);
            if (!string.IsNullOrEmpty(text)) text = _templates.Render(text, request.Variables, false);
        }

        if (string.IsNullOrWhiteSpace(subject)) return AcceptResult.Fail(400, "Subject is required");
        if (subject.Length > 998) return AcceptResult.Fail(400, "Subject is too long (max 998 characters)");
        if (ContainsLineBreak(subject)) return AcceptResult.Fail(400, "Subject must not contain line breaks");
        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(html)) return AcceptResult.Fail(400, "Either text or html body is required");

        // ---- recipients ----
        var to = Normalize(request.To);
        var cc2 = Normalize(request.Cc);
        var bcc = Normalize(request.Bcc);
        if (to.Count == 0) return AcceptResult.Fail(400, "At least one recipient is required in 'to'");

        foreach (var a in to.Concat(cc2).Concat(bcc))
        {
            if (!IsValidAddress(a.Address)) return AcceptResult.Fail(400, $"Invalid recipient address: '{a.Address}'");
            if (a.Name != null && ContainsLineBreak(a.Name)) return AcceptResult.Fail(400, "Recipient names must not contain line breaks");
        }

        var recipientCount = to.Count + cc2.Count + bcc.Count;
        var maxRecipients = Math.Min(client.MaxRecipientsPerMessage <= 0 ? int.MaxValue : client.MaxRecipientsPerMessage, opts.MaxRecipientsPerMessage);
        if (recipientCount > maxRecipients) return AcceptResult.Fail(400, $"Too many recipients ({recipientCount}); maximum is {maxRecipients}");

        var allowedDomains = IpUtils.SplitList(client.AllowedRecipientDomains).Select(d => d.TrimStart('@', '*').ToLowerInvariant()).ToList();
        if (allowedDomains.Count > 0)
        {
            var bad = to.Concat(cc2).Concat(bcc).Select(a => a.Address).FirstOrDefault(a => !allowedDomains.Contains(Domain(a)));
            if (bad != null) return AcceptResult.Fail(403, $"Recipient domain not allowed for this client: '{Domain(bad)}'");
        }

        // ---- from ----
        var fromAddress = profile.FromAddress;
        var fromName = profile.FromName;
        if (request.From != null && !string.IsNullOrWhiteSpace(request.From.Address))
        {
            var requested = request.From.Address.Trim();
            if (!IsValidAddress(requested)) return AcceptResult.Fail(400, $"Invalid from address: '{requested}'");
            if (!requested.Equals(profile.FromAddress, StringComparison.OrdinalIgnoreCase) && !IsFromAllowed(requested, client.AllowedFromAddresses))
                return AcceptResult.Fail(403, $"From address '{requested}' is not allowed for this client");
            fromAddress = requested;
            fromName = string.IsNullOrWhiteSpace(request.From.Name) ? fromName : request.From.Name.Trim();
        }
        if (fromName != null && ContainsLineBreak(fromName)) return AcceptResult.Fail(400, "From name must not contain line breaks");

        string? replyTo = request.ReplyTo?.Address?.Trim();
        if (string.IsNullOrWhiteSpace(replyTo)) replyTo = profile.ReplyTo;
        if (!string.IsNullOrWhiteSpace(replyTo) && !IsValidAddress(replyTo)) return AcceptResult.Fail(400, $"Invalid reply-to address: '{replyTo}'");

        // ---- headers ----
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (request.Headers != null)
        {
            if (request.Headers.Count > 20) return AcceptResult.Fail(400, "Too many custom headers (max 20)");
            foreach (var (k, v) in request.Headers)
            {
                if (string.IsNullOrWhiteSpace(k) || !k.StartsWith("X-", StringComparison.OrdinalIgnoreCase) || k.Length > 100)
                    return AcceptResult.Fail(400, $"Custom header '{k}' is not allowed; only X-* headers are accepted");
                if (v == null || v.Length > 1000 || ContainsLineBreak(k) || ContainsLineBreak(v))
                    return AcceptResult.Fail(400, $"Invalid value for header '{k}'");
                headers[k.Trim()] = v;
            }
        }

        // ---- attachments ----
        var attachments = new List<(AttachmentDto Dto, byte[] Bytes, string FileName)>();
        long totalAttachmentBytes = 0;
        if (request.Attachments != null)
        {
            if (request.Attachments.Count > opts.MaxAttachmentsPerMessage) return AcceptResult.Fail(400, $"Too many attachments (max {opts.MaxAttachmentsPerMessage})");
            foreach (var a in request.Attachments)
            {
                var fileName = SanitizeFileName(a.FileName);
                if (string.IsNullOrEmpty(fileName)) return AcceptResult.Fail(400, "Attachment file name is required");
                var ext = Path.GetExtension(fileName).ToLowerInvariant();
                if (opts.BlockedAttachmentExtensions.Any(b => b.Equals(ext, StringComparison.OrdinalIgnoreCase)))
                    return AcceptResult.Fail(400, $"Attachment type '{ext}' is not allowed");

                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(a.ContentBase64);
                }
                catch (FormatException)
                {
                    return AcceptResult.Fail(400, $"Attachment '{fileName}' is not valid base64");
                }
                totalAttachmentBytes += bytes.Length;
                if (totalAttachmentBytes > opts.MaxAttachmentBytes)
                    return AcceptResult.Fail(413, $"Attachments exceed the maximum total size of {opts.MaxAttachmentBytes / 1024 / 1024} MB");
                attachments.Add((a, bytes, fileName));
            }
        }

        // ---- quota ----
        if (!await _quota.TryConsumeAsync(client.Id, client.DailyQuota, 1, ct))
            return AcceptResult.Fail(429, $"Daily quota of {client.DailyQuota} messages exceeded");

        // ---- anomaly / duplicate storm ----
        var contentHash = ComputeContentHash(to, cc2, bcc, subject, text, html);
        if (_anomaly.RecordMessage(client.Id, contentHash, recipientCount, ip, traceId))
            return AcceptResult.Fail(409, "This identical message has been submitted repeatedly in a short time and was rejected as a duplicate storm. Use idempotencyKey for safe retries.");

        // ---- persist ----
        var now = DateTime.UtcNow;
        var scheduled = request.ScheduleAt.HasValue && request.ScheduleAt.Value.ToUniversalTime() > now.AddSeconds(5);
        var message = new EmailMessage
        {
            ClientId = client.Id,
            ApiKeyId = cc.ApiKeyId,
            SenderProfileId = profile.Id,
            IdempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey.Trim(),
            FromAddress = fromAddress,
            FromName = fromName,
            ReplyTo = replyTo,
            ToJson = JsonSerializer.Serialize(to, JsonOpts),
            CcJson = cc2.Count > 0 ? JsonSerializer.Serialize(cc2, JsonOpts) : null,
            BccJson = bcc.Count > 0 ? JsonSerializer.Serialize(bcc, JsonOpts) : null,
            Subject = subject.Trim(),
            TextBody = text,
            HtmlBody = html,
            HeadersJson = headers.Count > 0 ? JsonSerializer.Serialize(headers, JsonOpts) : null,
            Tags = request.Tags is { Count: > 0 } ? string.Join(",", request.Tags.Select(t => t.Trim()).Where(t => t.Length > 0).Take(10)) : null,
            Priority = request.Priority,
            RecipientCount = recipientCount,
            ContentHash = contentHash,
            Status = scheduled ? EmailStatus.Scheduled : EmailStatus.Queued,
            ScheduledAt = scheduled ? request.ScheduleAt!.Value.ToUniversalTime() : null,
            NextAttemptAt = scheduled ? request.ScheduleAt!.Value.ToUniversalTime() : now,
            CreatedAt = now,
            RequestIp = ip,
            TraceId = traceId,
            TotalSize = (text?.Length ?? 0) + (html?.Length ?? 0) + totalAttachmentBytes
        };
        message.Events.Add(new EmailEvent { Type = scheduled ? "Scheduled" : "Queued", Details = scheduled ? $"Scheduled for {message.ScheduledAt:u}" : $"Accepted from {ip}", Source = "api" });

        foreach (var (dto, bytes, fileName) in attachments)
        {
            var path = await StoreAttachmentAsync(message.Id, message.Attachments.Count, fileName, bytes, ct);
            message.Attachments.Add(new EmailAttachment
            {
                FileName = fileName,
                ContentType = string.IsNullOrWhiteSpace(dto.ContentType) ? GuessContentType(fileName) : dto.ContentType.Trim(),
                Size = bytes.Length,
                StoragePath = path,
                ContentId = string.IsNullOrWhiteSpace(dto.ContentId) ? null : dto.ContentId.Trim('<', '>', ' '),
                IsInline = !string.IsNullOrWhiteSpace(dto.ContentId),
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
            });
        }

        db.Messages.Add(message);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (message.IdempotencyKey != null)
        {
            // raced on the idempotency index
            DeleteAttachmentFiles(message);
            var existing = await db.Messages.AsNoTracking().FirstAsync(m => m.ClientId == client.Id && m.IdempotencyKey == message.IdempotencyKey, ct);
            return new AcceptResult { Success = true, StatusCode = 200, Message = existing, Duplicate = true };
        }

        if (!scheduled) _queue.Enqueue(message.Id);

        _logger.LogInformation("Accepted message {Id} from client {Client} ({Recipients} recipients, profile {Profile})", message.Id, client.Name, recipientCount, profile.Name);
        return new AcceptResult { Success = true, StatusCode = 202, Message = message };
    }

    public async Task<EmailMessage?> AcceptSystemAsync(string senderProfileName, OutboundEmail email, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var profile = await db.SenderProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Name == senderProfileName && p.IsActive, ct);
        if (profile == null)
        {
            _logger.LogWarning("System mail not sent: sender profile '{Profile}' not found or inactive", senderProfileName);
            return null;
        }

        var message = new EmailMessage
        {
            SenderProfileId = profile.Id,
            FromAddress = profile.FromAddress,
            FromName = profile.FromName,
            ReplyTo = profile.ReplyTo,
            ToJson = JsonSerializer.Serialize(email.To, JsonOpts),
            Subject = email.Subject,
            TextBody = email.Text,
            HtmlBody = email.Html,
            RecipientCount = email.To.Count,
            ContentHash = ComputeContentHash(email.To, email.Cc, email.Bcc, email.Subject, email.Text, email.Html),
            Priority = 1,
            IsSystem = true,
            NextAttemptAt = DateTime.UtcNow,
            Tags = "system"
        };
        message.Events.Add(new EmailEvent { Type = "Queued", Details = "System generated", Source = "system" });
        db.Messages.Add(message);
        await db.SaveChangesAsync(ct);
        _queue.Enqueue(message.Id);
        return message;
    }

    public async Task<bool> CancelAsync(Guid id, int? clientId, string by, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var message = await db.Messages.FirstOrDefaultAsync(m => m.Id == id && (clientId == null || m.ClientId == clientId), ct);
        if (message == null) return false;
        if (message.Status != EmailStatus.Queued && message.Status != EmailStatus.Scheduled) return false;

        message.Status = EmailStatus.Cancelled;
        message.CompletedAt = DateTime.UtcNow;
        message.NextAttemptAt = null;
        db.EmailEvents.Add(new EmailEvent { MessageId = id, Type = "Cancelled", Details = $"Cancelled by {by}", Source = by });
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RequeueAsync(Guid id, string by, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var message = await db.Messages.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (message == null) return false;
        if (message.Status != EmailStatus.Failed && message.Status != EmailStatus.Cancelled) return false;

        message.Status = EmailStatus.Queued;
        message.Attempts = 0;
        message.NextAttemptAt = DateTime.UtcNow;
        message.CompletedAt = null;
        message.LastError = null;
        db.EmailEvents.Add(new EmailEvent { MessageId = id, Type = "Requeued", Details = $"Re-queued by {by}", Source = by });
        await db.SaveChangesAsync(ct);
        _queue.Enqueue(id);
        return true;
    }

    public async Task<OutboundEmail> ToOutboundAsync(EmailMessage message, CancellationToken ct = default)
    {
        var email = new OutboundEmail
        {
            Id = message.Id,
            FromAddress = message.FromAddress,
            FromName = message.FromName,
            ReplyTo = message.ReplyTo,
            To = Deserialize(message.ToJson),
            Cc = Deserialize(message.CcJson),
            Bcc = Deserialize(message.BccJson),
            Subject = message.Subject,
            Text = message.TextBody,
            Html = message.HtmlBody,
            Headers = string.IsNullOrEmpty(message.HeadersJson) ? new() : JsonSerializer.Deserialize<Dictionary<string, string>>(message.HeadersJson, JsonOpts) ?? new(),
            Tags = IpUtils.SplitList(message.Tags)
        };

        foreach (var a in message.Attachments)
        {
            var full = ResolvePath(a.StoragePath);
            if (!File.Exists(full))
            {
                throw new FileNotFoundException($"Attachment file missing: {a.FileName}", full);
            }
            email.Attachments.Add(new OutboundAttachment
            {
                FileName = a.FileName,
                ContentType = a.ContentType,
                Content = await File.ReadAllBytesAsync(full, ct),
                ContentId = a.ContentId,
                IsInline = a.IsInline
            });
        }
        return email;
    }

    public void DeleteAttachmentFiles(EmailMessage message)
    {
        foreach (var a in message.Attachments)
        {
            try
            {
                var full = ResolvePath(a.StoragePath);
                if (File.Exists(full)) File.Delete(full);
                var dir = Path.GetDirectoryName(full);
                if (dir != null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete attachment {Path}", a.StoragePath);
            }
        }
    }

    // ---------------- helpers ----------------

    public static List<EmailAddressDto> Deserialize(string? json)
        => string.IsNullOrEmpty(json) ? new() : JsonSerializer.Deserialize<List<EmailAddressDto>>(json, JsonOpts) ?? new();

    private static List<EmailAddressDto> Normalize(List<EmailAddressDto>? list)
    {
        var result = new List<EmailAddressDto>();
        if (list == null) return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in list)
        {
            if (a == null || string.IsNullOrWhiteSpace(a.Address)) continue;
            var addr = a.Address.Trim();
            if (!seen.Add(addr)) continue;
            result.Add(new EmailAddressDto { Address = addr, Name = string.IsNullOrWhiteSpace(a.Name) ? null : a.Name.Trim() });
        }
        return result;
    }

    public static bool IsValidAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Length > 320) return false;
        if (ContainsLineBreak(address) || address.Contains(' ')) return false;
        if (!MailAddress.TryCreate(address, out var parsed)) return false;
        return parsed.Address.Equals(address, StringComparison.OrdinalIgnoreCase) && address.Contains('@') && Domain(address).Contains('.');
    }

    private static bool ContainsLineBreak(string s) => s.Contains('\r') || s.Contains('\n');

    private static string Domain(string address)
    {
        var at = address.LastIndexOf('@');
        return at < 0 ? string.Empty : address[(at + 1)..].ToLowerInvariant();
    }

    private static bool IsFromAllowed(string address, string? allowedCsv)
    {
        var allowed = IpUtils.SplitList(allowedCsv);
        var domain = Domain(address);
        foreach (var entry in allowed)
        {
            var e = entry.ToLowerInvariant();
            if (e.StartsWith("*@")) e = e[1..];
            if (e.StartsWith('@'))
            {
                if (domain == e[1..]) return true;
            }
            else if (e.Equals(address, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string ComputeContentHash(IEnumerable<EmailAddressDto> to, IEnumerable<EmailAddressDto> cc, IEnumerable<EmailAddressDto> bcc, string? subject, string? text, string? html)
    {
        var sb = new StringBuilder();
        foreach (var address in to.Concat(cc).Concat(bcc).Select(x => x.Address.ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal))
            sb.Append(address).Append(';');
        sb.Append('|').Append(subject).Append('|').Append(text).Append('|').Append(html);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var fileName = Path.GetFileName(name.Trim());
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(fileName.Where(c => !invalid.Contains(c) && c != ';' && c != '%').ToArray());
        return cleaned.Length > 200 ? cleaned[^200..] : cleaned;
    }

    private static string GuessContentType(string fileName)
    {
        var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
        return provider.TryGetContentType(fileName, out var ct) ? ct : "application/octet-stream";
    }

    private string ResolvePath(string storagePath)
        => Path.IsPathRooted(storagePath) ? storagePath : Path.Combine(Path.GetFullPath(_options.CurrentValue.AttachmentStoragePath), storagePath);

    private async Task<string> StoreAttachmentAsync(Guid messageId, int index, string fileName, byte[] bytes, CancellationToken ct)
    {
        var relative = Path.Combine(DateTime.UtcNow.ToString("yyyyMM"), messageId.ToString("N"), $"{index}_{fileName}");
        var full = ResolvePath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, bytes, ct);
        return relative;
    }
}
