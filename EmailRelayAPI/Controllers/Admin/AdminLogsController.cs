using EmailRelayAPI.Data;
using EmailRelayAPI.Data.Entities;
using EmailRelayAPI.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EmailRelayAPI.Controllers.Admin;

/// <summary>Audit trail and request log queries.</summary>
[Route("api/admin/logs")]
public class AdminLogsController : AdminControllerBase
{
    private readonly AppDbContext _db;

    public AdminLogsController(AppDbContext db) => _db = db;

    [HttpGet("audit")]
    public async Task<IActionResult> Audit([FromQuery] string? category, [FromQuery] string? severity, [FromQuery] string? action, [FromQuery] string? username,
        [FromQuery] int? clientId, [FromQuery] string? ip, [FromQuery] bool? success, [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? search,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        (page, pageSize) = Paging(page, pageSize);
        var q = AuditQuery(category, severity, action, username, clientId, ip, success, from, to, search);
        if (q == null) return BadRequest(ApiResponse.Fail("Invalid category or severity"));

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(a => a.Timestamp).ThenByDescending(a => a.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var names = await ClientNamesAsync(items.Where(a => a.ClientId.HasValue).Select(a => a.ClientId!.Value), ct);

        return Ok(ApiResponse<PagedResult<AuditLogDto>>.Ok(new PagedResult<AuditLogDto>
        {
            Items = items.Select(a => Mappers.ToDto(a, a.ClientId.HasValue ? names.GetValueOrDefault(a.ClientId.Value) : null)).ToList(),
            Page = page,
            PageSize = pageSize,
            TotalCount = total
        }));
    }

    [HttpGet("audit/export")]
    public async Task<IActionResult> AuditExport([FromQuery] string? category, [FromQuery] string? severity, [FromQuery] string? action, [FromQuery] string? username,
        [FromQuery] int? clientId, [FromQuery] string? ip, [FromQuery] bool? success, [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? search, CancellationToken ct = default)
    {
        var q = AuditQuery(category, severity, action, username, clientId, ip, success, from, to, search);
        if (q == null) return BadRequest(ApiResponse.Fail("Invalid category or severity"));
        var items = await q.OrderByDescending(a => a.Timestamp).Take(100_000).ToListAsync(ct);
        return Csv($"audit-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv",
            new[] { "Timestamp", "Category", "Severity", "Action", "Success", "Username", "ClientId", "ApiKeyId", "IpAddress", "Path", "Method", "Status", "DurationMs", "TargetType", "TargetId", "Details", "TraceId" },
            items.Select(a => new object?[] { a.Timestamp, a.Category, a.Severity, a.Action, a.Success, a.Username, a.ClientId, a.ApiKeyId, a.IpAddress, a.RequestPath, a.HttpMethod, a.StatusCode, a.DurationMs, a.TargetType, a.TargetId, a.Details, a.TraceId }));
    }

    [HttpGet("audit/actions")]
    public async Task<IActionResult> AuditActions(CancellationToken ct)
    {
        var actions = await _db.AuditLogs.Select(a => a.Action).Distinct().OrderBy(a => a).ToListAsync(ct);
        return Ok(ApiResponse<List<string>>.Ok(actions));
    }

    [HttpGet("requests")]
    public async Task<IActionResult> Requests([FromQuery] int? clientId, [FromQuery] int? status, [FromQuery] string? path, [FromQuery] string? ip, [FromQuery] string? username,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] bool? errorsOnly, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        (page, pageSize) = Paging(page, pageSize);
        var q = RequestQuery(clientId, status, path, ip, username, from, to, errorsOnly);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(r => r.Timestamp).ThenByDescending(r => r.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var names = await ClientNamesAsync(items.Where(r => r.ClientId.HasValue).Select(r => r.ClientId!.Value), ct);

        return Ok(ApiResponse<PagedResult<RequestLogDto>>.Ok(new PagedResult<RequestLogDto>
        {
            Items = items.Select(r => Mappers.ToDto(r, r.ClientId.HasValue ? names.GetValueOrDefault(r.ClientId.Value) : null)).ToList(),
            Page = page,
            PageSize = pageSize,
            TotalCount = total
        }));
    }

    [HttpGet("requests/export")]
    public async Task<IActionResult> RequestsExport([FromQuery] int? clientId, [FromQuery] int? status, [FromQuery] string? path, [FromQuery] string? ip, [FromQuery] string? username,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] bool? errorsOnly, CancellationToken ct = default)
    {
        var q = RequestQuery(clientId, status, path, ip, username, from, to, errorsOnly);
        var items = await q.OrderByDescending(r => r.Timestamp).Take(100_000).ToListAsync(ct);
        return Csv($"requests-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv",
            new[] { "Timestamp", "ClientId", "ApiKeyId", "Username", "IpAddress", "Method", "Path", "Query", "Status", "DurationMs", "RequestBytes", "UserAgent", "TraceId", "Error" },
            items.Select(r => new object?[] { r.Timestamp, r.ClientId, r.ApiKeyId, r.Username, r.IpAddress, r.Method, r.Path, r.Query, r.StatusCode, r.DurationMs, r.RequestBytes, r.UserAgent, r.TraceId, r.Error }));
    }

    private IQueryable<AuditLog>? AuditQuery(string? category, string? severity, string? action, string? username, int? clientId, string? ip, bool? success, DateTime? from, DateTime? to, string? search)
    {
        var q = _db.AuditLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(category))
        {
            if (!Enum.TryParse<AuditCategory>(category, true, out var cat)) return null;
            q = q.Where(a => a.Category == cat);
        }
        if (!string.IsNullOrWhiteSpace(severity))
        {
            if (!Enum.TryParse<AuditSeverity>(severity, true, out var sev)) return null;
            q = q.Where(a => a.Severity >= sev);
        }
        if (!string.IsNullOrWhiteSpace(action)) q = q.Where(a => a.Action == action);
        if (!string.IsNullOrWhiteSpace(username)) q = q.Where(a => a.Username != null && a.Username.Contains(username));
        if (clientId.HasValue) q = q.Where(a => a.ClientId == clientId);
        if (!string.IsNullOrWhiteSpace(ip)) q = q.Where(a => a.IpAddress == ip);
        if (success.HasValue) q = q.Where(a => a.Success == success.Value);
        if (from.HasValue) q = q.Where(a => a.Timestamp >= from.Value.ToUniversalTime());
        if (to.HasValue) q = q.Where(a => a.Timestamp < to.Value.ToUniversalTime());
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(a => (a.Details != null && a.Details.Contains(s)) || a.Action.Contains(s) || (a.TargetId != null && a.TargetId == s) || (a.TraceId != null && a.TraceId == s));
        }
        return q;
    }

    private IQueryable<RequestLog> RequestQuery(int? clientId, int? status, string? path, string? ip, string? username, DateTime? from, DateTime? to, bool? errorsOnly)
    {
        var q = _db.RequestLogs.AsNoTracking().AsQueryable();
        if (clientId.HasValue) q = q.Where(r => r.ClientId == clientId);
        if (status.HasValue) q = q.Where(r => r.StatusCode == status);
        if (errorsOnly == true) q = q.Where(r => r.StatusCode >= 400);
        if (!string.IsNullOrWhiteSpace(path)) q = q.Where(r => r.Path.Contains(path));
        if (!string.IsNullOrWhiteSpace(ip)) q = q.Where(r => r.IpAddress == ip);
        if (!string.IsNullOrWhiteSpace(username)) q = q.Where(r => r.Username != null && r.Username.Contains(username));
        if (from.HasValue) q = q.Where(r => r.Timestamp >= from.Value.ToUniversalTime());
        if (to.HasValue) q = q.Where(r => r.Timestamp < to.Value.ToUniversalTime());
        return q;
    }

    private async Task<Dictionary<int, string>> ClientNamesAsync(IEnumerable<int> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return new();
        return await _db.Clients.AsNoTracking().Where(c => list.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
    }
}
