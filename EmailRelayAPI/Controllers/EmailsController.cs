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

namespace EmailRelayAPI.Controllers;

/// <summary>Submit and track email messages.</summary>
[ApiController]
[Route("api/v1/emails")]
[Authorize(AuthenticationSchemes = RelayClaims.ApiKeyScheme)]
[Produces("application/json")]
public class EmailsController : ControllerBase
{
    private readonly IEmailQueueService _queue;
    private readonly AppDbContext _db;
    private readonly IOptionsMonitor<DeliveryOptions> _delivery;

    public EmailsController(IEmailQueueService queue, AppDbContext db, IOptionsMonitor<DeliveryOptions> delivery)
    {
        _queue = queue;
        _db = db;
        _delivery = delivery;
    }

    /// <summary>Queue a single email for delivery.</summary>
    /// <remarks>
    /// The message is validated, stored and delivered asynchronously through the client's sender profile.
    /// Returns 202 with the message id; poll GET /api/v1/emails/{id} or configure a webhook for status updates.
    /// Use <c>idempotencyKey</c> to make retries safe.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = RelayClaims.PolicySend)]
    [ProducesResponseType(typeof(ApiResponse<EmailAcceptedResponse>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiResponse<EmailAcceptedResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Send([FromBody] SendEmailRequest request, CancellationToken ct)
    {
        var cc = HttpContext.GetClientContext()!;
        var result = await _queue.AcceptAsync(request, cc, HttpContext.GetClientIp(), HttpContext.GetTraceId(), ct);
        return ToResponse(result);
    }

    /// <summary>Queue up to Delivery:MaxBatchSize emails in one call. Each item is accepted or rejected independently.</summary>
    [HttpPost("batch")]
    [Authorize(Policy = RelayClaims.PolicySend)]
    [ProducesResponseType(typeof(ApiResponse<List<BatchItemResult>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SendBatch([FromBody] SendBatchRequest request, CancellationToken ct)
    {
        var max = _delivery.CurrentValue.MaxBatchSize;
        if (request.Messages.Count == 0) return BadRequest(ApiResponse.Fail("Batch is empty"));
        if (request.Messages.Count > max) return BadRequest(ApiResponse.Fail($"Batch too large; maximum is {max} messages"));

        var cc = HttpContext.GetClientContext()!;
        var ip = HttpContext.GetClientIp();
        var traceId = HttpContext.GetTraceId();
        var results = new List<BatchItemResult>();

        for (var i = 0; i < request.Messages.Count; i++)
        {
            var r = await _queue.AcceptAsync(request.Messages[i], cc, ip, traceId, ct);
            results.Add(new BatchItemResult
            {
                Index = i,
                Accepted = r.Success,
                Id = r.Message?.Id,
                Status = r.Message?.Status.ToApiString(),
                Error = r.Error
            });
        }

        var accepted = results.Count(r => r.Accepted);
        return Ok(ApiResponse<List<BatchItemResult>>.Ok(results, $"{accepted} of {results.Count} messages accepted"));
    }

    /// <summary>Get the delivery status and timeline of a message.</summary>
    [HttpGet("{id:guid}")]
    [Authorize(Policy = RelayClaims.PolicyRead)]
    [ProducesResponseType(typeof(ApiResponse<EmailStatusResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var cc = HttpContext.GetClientContext()!;
        var message = await _db.Messages.AsNoTracking()
            .Include(m => m.Events)
            .Include(m => m.Attachments)
            .Include(m => m.SenderProfile)
            .FirstOrDefaultAsync(m => m.Id == id && m.ClientId == cc.ClientId, ct);
        if (message == null) return NotFound(ApiResponse.Fail("Message not found"));
        return Ok(ApiResponse<EmailStatusResponse>.Ok(Mappers.ToStatus(message)));
    }

    /// <summary>List the client's messages (newest first).</summary>
    /// <param name="status">queued, scheduled, sending, sent, failed or cancelled</param>
    /// <param name="from">Created at or after (UTC)</param>
    /// <param name="to">Created before (UTC)</param>
    /// <param name="search">Matches subject or recipient</param>
    /// <param name="page">1-based page</param>
    /// <param name="pageSize">1..100</param>
    [HttpGet]
    [Authorize(Policy = RelayClaims.PolicyRead)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<EmailSummaryDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? search,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        var cc = HttpContext.GetClientContext()!;
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var q = _db.Messages.AsNoTracking().Include(m => m.Client).Include(m => m.SenderProfile).Include(m => m.Attachments)
            .Where(m => m.ClientId == cc.ClientId);

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<EmailStatus>(status, true, out var st)) return BadRequest(ApiResponse.Fail("Unknown status"));
            q = q.Where(m => m.Status == st);
        }
        if (from.HasValue) q = q.Where(m => m.CreatedAt >= from.Value.ToUniversalTime());
        if (to.HasValue) q = q.Where(m => m.CreatedAt < to.Value.ToUniversalTime());
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(m => m.Subject.Contains(s) || m.ToJson.Contains(s));
        }

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

    /// <summary>Cancel a message that has not been sent yet.</summary>
    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = RelayClaims.PolicySend)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var cc = HttpContext.GetClientContext()!;
        var exists = await _db.Messages.AnyAsync(m => m.Id == id && m.ClientId == cc.ClientId, ct);
        if (!exists) return NotFound(ApiResponse.Fail("Message not found"));
        var ok = await _queue.CancelAsync(id, cc.ClientId, $"client:{cc.ClientName}", ct);
        return ok ? Ok(ApiResponse.Ok("Message cancelled")) : Conflict(ApiResponse.Fail("Message can no longer be cancelled"));
    }

    private IActionResult ToResponse(AcceptResult result)
    {
        if (!result.Success)
        {
            return StatusCode(result.StatusCode, ApiResponse.Fail(result.Error ?? "Request rejected"));
        }
        var m = result.Message!;
        var body = ApiResponse<EmailAcceptedResponse>.Ok(new EmailAcceptedResponse
        {
            Id = m.Id,
            Status = m.Status.ToApiString(),
            CreatedAt = m.CreatedAt,
            ScheduledAt = m.ScheduledAt,
            Duplicate = result.Duplicate
        }, result.Duplicate ? "Duplicate idempotency key; returning the original message" : "Message accepted for delivery");
        return StatusCode(result.StatusCode, body);
    }
}
