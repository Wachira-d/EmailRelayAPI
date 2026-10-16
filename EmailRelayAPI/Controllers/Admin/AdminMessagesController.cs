using EmailRelayAPI.Data;
using EmailRelayAPI.Data.Entities;
using EmailRelayAPI.Models;
using EmailRelayAPI.Options;
using EmailRelayAPI.Security;
using EmailRelayAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EmailRelayAPI.Controllers.Admin;

/// <summary>Browse, inspect and re-drive messages.</summary>
[Route("api/admin/messages")]
public class AdminMessagesController : AdminControllerBase
{
    private readonly AppDbContext _db;
    private readonly IEmailQueueService _queue;
    private readonly IAuditService _audit;
    private readonly IOptionsMonitor<DeliveryOptions> _delivery;

    public AdminMessagesController(AppDbContext db, IEmailQueueService queue, IAuditService audit, IOptionsMonitor<DeliveryOptions> delivery)
    {
        _db = db;
        _queue = queue;
        _audit = audit;
        _delivery = delivery;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] int? clientId, [FromQuery] int? profileId, [FromQuery] string? search,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        (page, pageSize) = Paging(page, pageSize);
        var q = BuildQuery(status, clientId, profileId, search, from, to);
        if (q == null) return BadRequest(ApiResponse.Fail("Unknown status"));

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(m => m.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Ok(ApiResponse<PagedResult<EmailSummaryDto>>.Ok(new PagedResult<EmailSummaryDto>
        {
            Items = items.Select(Mappers.ToSummary).ToList(),
            Page = page,
            PageSize = pageSize,
            TotalCount = total
        }));
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] string? status, [FromQuery] int? clientId, [FromQuery] int? profileId, [FromQuery] string? search,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct = default)
    {
        var q = BuildQuery(status, clientId, profileId, search, from, to);
        if (q == null) return BadRequest(ApiResponse.Fail("Unknown status"));
        var items = await q.OrderByDescending(m => m.CreatedAt).Take(50_000).ToListAsync(ct);
        return Csv($"messages-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv",
            new[] { "Id", "CreatedAt", "Status", "Client", "Profile", "From", "To", "Subject", "Recipients", "Attempts", "SentAt", "DurationMs", "ProviderMessageId", "LastError", "RequestIp" },
            items.Select(m => new object?[]
            {
                m.Id, m.CreatedAt, m.Status.ToApiString(), m.Client?.Name ?? (m.IsSystem ? "(system)" : ""), m.SenderProfile?.Name, m.FromAddress,
                string.Join("; ", EmailQueueService.Deserialize(m.ToJson).Select(a => a.Address)), m.Subject, m.RecipientCount, m.Attempts, m.SentAt, m.DurationMs, m.ProviderMessageId, m.LastError, m.RequestIp
            }));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var m = await _db.Messages.AsNoTracking()
            .Include(x => x.Client).Include(x => x.SenderProfile).Include(x => x.Events).Include(x => x.Attachments)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m == null) return NotFound(ApiResponse.Fail("Message not found"));

        var status = Mappers.ToStatus(m);
        var detail = new
        {
            status.Id, status.Status, status.From, status.To, status.Cc, status.Bcc, status.Subject, status.Attempts, status.CreatedAt, status.ScheduledAt,
            status.NextAttemptAt, status.SentAt, status.DurationMs, status.LastError, status.ProviderMessageId, status.SenderProfile, status.IdempotencyKey, status.Tags,
            status.Events,
            Attachments = m.Attachments.Select(a => new { a.Id, a.FileName, a.ContentType, a.Size, a.IsInline, a.ContentId, a.Sha256 }),
            ClientId = m.ClientId,
            ClientName = m.Client?.Name ?? (m.IsSystem ? "(system)" : null),
            m.ApiKeyId,
            m.Priority,
            m.RecipientCount,
            m.TextBody,
            m.HtmlBody,
            Headers = m.HeadersJson,
            m.ProviderResponse,
            m.RequestIp,
            m.TraceId,
            m.TotalSize,
            m.IsSystem,
            m.CompletedAt,
            m.ContentHash
        };
        return Ok(ApiResponse<object>.Ok(detail));
    }

    [HttpGet("{id:guid}/attachments/{attachmentId:int}")]
    public async Task<IActionResult> DownloadAttachment(Guid id, int attachmentId, CancellationToken ct)
    {
        var a = await _db.Attachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == attachmentId && x.MessageId == id, ct);
        if (a == null) return NotFound(ApiResponse.Fail("Attachment not found"));
        var root = Path.GetFullPath(_delivery.CurrentValue.AttachmentStoragePath);
        var full = Path.GetFullPath(Path.Combine(root, a.StoragePath));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return BadRequest(ApiResponse.Fail("Invalid attachment path"));
        if (!System.IO.File.Exists(full)) return NotFound(ApiResponse.Fail("Attachment file is no longer available"));

        _audit.Log(new AuditLog { Category = AuditCategory.Admin, Action = "AttachmentDownloaded", Username = Actor, IpAddress = ClientIp, TargetType = "Message", TargetId = id.ToString(), Details = $"Downloaded attachment '{a.FileName}' ({a.Size} bytes)" });
        return PhysicalFile(full, "application/octet-stream", a.FileName);
    }

    [HttpPost("{id:guid}/requeue")]
    [Authorize(Policy = RelayClaims.PolicyAdmin)]
    public async Task<IActionResult> Requeue(Guid id, CancellationToken ct)
    {
        var ok = await _queue.RequeueAsync(id, $"admin:{Actor}", ct);
        if (!ok) return Conflict(ApiResponse.Fail("Only failed or cancelled messages can be re-queued"));
        _audit.Log(new AuditLog { Category = AuditCategory.Delivery, Action = "MessageRequeued", Username = Actor, IpAddress = ClientIp, TargetType = "Message", TargetId = id.ToString(), Details = "Message re-queued by administrator" });
        return Ok(ApiResponse.Ok("Message re-queued"));
    }

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = RelayClaims.PolicyAdmin)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var ok = await _queue.CancelAsync(id, null, $"admin:{Actor}", ct);
        if (!ok) return Conflict(ApiResponse.Fail("Only queued or scheduled messages can be cancelled"));
        _audit.Log(new AuditLog { Category = AuditCategory.Delivery, Action = "MessageCancelled", Username = Actor, IpAddress = ClientIp, TargetType = "Message", TargetId = id.ToString(), Details = "Message cancelled by administrator" });
        return Ok(ApiResponse.Ok("Message cancelled"));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = RelayClaims.PolicyAdmin)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var m = await _db.Messages.Include(x => x.Attachments).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m == null) return NotFound(ApiResponse.Fail("Message not found"));
        if (m.Status == EmailStatus.Sending) return Conflict(ApiResponse.Fail("Message is being sent"));
        _queue.DeleteAttachmentFiles(m);
        _db.Messages.Remove(m);
        await _db.SaveChangesAsync(ct);
        _audit.Log(new AuditLog { Category = AuditCategory.Admin, Action = "MessageDeleted", Username = Actor, IpAddress = ClientIp, TargetType = "Message", TargetId = id.ToString(), Details = $"Deleted message '{m.Subject}' ({m.Status})" });
        return Ok(ApiResponse.Ok("Message deleted"));
    }

    private IQueryable<EmailMessage>? BuildQuery(string? status, int? clientId, int? profileId, string? search, DateTime? from, DateTime? to)
    {
        var q = _db.Messages.AsNoTracking().Include(m => m.Client).Include(m => m.SenderProfile).Include(m => m.Attachments).AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<EmailStatus>(status, true, out var st)) return null;
            q = q.Where(m => m.Status == st);
        }
        if (clientId.HasValue) q = q.Where(m => m.ClientId == clientId);
        if (profileId.HasValue) q = q.Where(m => m.SenderProfileId == profileId);
        if (from.HasValue) q = q.Where(m => m.CreatedAt >= from.Value.ToUniversalTime());
        if (to.HasValue) q = q.Where(m => m.CreatedAt < to.Value.ToUniversalTime());
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            if (Guid.TryParse(s, out var gid)) q = q.Where(m => m.Id == gid);
            else q = q.Where(m => m.Subject.Contains(s) || m.ToJson.Contains(s) || m.FromAddress.Contains(s) || (m.LastError != null && m.LastError.Contains(s)) || (m.IdempotencyKey != null && m.IdempotencyKey == s));
        }
        return q;
    }
}
