using EmailRelayAPI.Data;
using EmailRelayAPI.Data.Entities;
using EmailRelayAPI.Models;
using EmailRelayAPI.Security;
using EmailRelayAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EmailRelayAPI.Controllers.Admin;

/// <summary>Block list, caller verification data and console users.</summary>
[Route("api/admin/security")]
public class AdminSecurityController : AdminControllerBase
{
    private readonly AppDbContext _db;
    private readonly IBlockListService _blocks;
    private readonly IAdminAuthService _adminUsers;
    private readonly IAuditService _audit;
    private readonly IApiKeyService _keys;

    public AdminSecurityController(AppDbContext db, IBlockListService blocks, IAdminAuthService adminUsers, IAuditService audit, IApiKeyService keys)
    {
        _db = db;
        _blocks = blocks;
        _adminUsers = adminUsers;
        _audit = audit;
        _keys = keys;
    }

    [HttpGet("blocks")]
    public async Task<IActionResult> Blocks([FromQuery] bool includeHistory = false, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var q = _db.BlockEntries.AsNoTracking().AsQueryable();
        if (!includeHistory) q = q.Where(b => b.RemovedAt == null && (b.IsPermanent || b.ExpiresAt == null || b.ExpiresAt > now));
        var items = await q.OrderByDescending(b => b.CreatedAt).Take(500).ToListAsync(ct);
        return Ok(ApiResponse<List<BlockEntryDto>>.Ok(items.Select(Mappers.ToDto).ToList()));
    }

    [HttpPost("blocks")]
    [Authorize(Policy = RelayClaims.PolicyAdmin)]
    public async Task<IActionResult> Block([FromBody] CreateBlockRequest request, CancellationToken ct)
    {
        var target = request.Target.Trim();
        switch (request.TargetType)
        {
            case BlockTargetType.Ip:
                if (!IpUtils.TryParseCidr(target, out _, out _)) return BadRequest(ApiResponse.Fail("Target must be an IP address or CIDR range"));
                if (IpUtils.TryParseCidr(target, out var net, out _) && IpUtils.IsLoopback(net)) return BadRequest(ApiResponse.Fail("Refusing to block loopback"));
                break;
            case BlockTargetType.Client:
                if (!int.TryParse(target, out var cid) || !await _db.Clients.AnyAsync(c => c.Id == cid, ct)) return BadRequest(ApiResponse.Fail("Target must be an existing client id"));
                break;
            case BlockTargetType.ApiKey:
                if (!await _db.ApiKeys.AnyAsync(k => k.Prefix == target, ct)) return BadRequest(ApiResponse.Fail("Target must be an existing key prefix"));
                break;
        }

        var entry = await _blocks.BlockAsync(request.TargetType, target, request.Reason.Trim(), TimeSpan.FromMinutes(request.DurationMinutes), Actor, request.Permanent, ct);
        _audit.Security("ManualBlock", AuditSeverity.Warning, $"{Actor} blocked {request.TargetType} {target} ({(request.Permanent ? "permanent" : request.DurationMinutes + " min")}): {request.Reason}", ClientIp, username: Actor, success: true);
        return Ok(ApiResponse<BlockEntryDto>.Ok(Mappers.ToDto(entry), "Block added"));
    }

    [HttpDelete("blocks/{id:int}")]
    [Authorize(Policy = RelayClaims.PolicyAdmin)]
    public async Task<IActionResult> Unblock(int id, CancellationToken ct)
    {
        var entry = await _db.BlockEntries.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (entry == null) return NotFound(ApiResponse.Fail("Block entry not found"));
        var ok = await _blocks.UnblockAsync(id, Actor, ct);
        if (!ok) return Conflict(ApiResponse.Fail("Block entry is already removed"));
        _audit.Security("Unblock", AuditSeverity.Info, $"{Actor} removed block on {entry.TargetType} {entry.Target} (reason was: {entry.Reason})", ClientIp, username: Actor, success: true);
        return Ok(ApiResponse.Ok("Block removed"));
    }

    [HttpGet("seen-ips")]
    public async Task<IActionResult> SeenIps([FromQuery] int? clientId, [FromQuery] int limit = 500, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 5000);
        var q = _db.ClientSeenIps.AsNoTracking().AsQueryable();
        if (clientId.HasValue) q = q.Where(s => s.ClientId == clientId);
        var items = await q.OrderByDescending(s => s.LastSeenAt).Take(limit).ToListAsync(ct);
        var clients = await _db.Clients.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        return Ok(ApiResponse<List<ClientSeenIpDto>>.Ok(items.Select(s => new ClientSeenIpDto
        {
            ClientId = s.ClientId,
            ClientName = clients.GetValueOrDefault(s.ClientId),
            IpAddress = s.IpAddress,
            FirstSeenAt = s.FirstSeenAt,
            LastSeenAt = s.LastSeenAt,
            RequestCount = s.RequestCount
        }).ToList()));
    }

    /// <summary>Keys that are due for rotation, expiring soon or unused for a long time.</summary>
    [HttpGet("key-health")]
    public async Task<IActionResult> KeyHealth(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var keys = await _db.ApiKeys.AsNoTracking().Include(k => k.Client).Where(k => k.RevokedAt == null).ToListAsync(ct);
        var result = keys.Select(k => Mappers.ToDto(k)).Select(d => new
        {
            Key = d,
            RotationDue = d.RotationDueAt.HasValue && d.RotationDueAt.Value <= now,
            ExpiringSoon = d.ExpiresAt.HasValue && d.ExpiresAt.Value > now && d.ExpiresAt.Value <= now.AddDays(7),
            Expired = d.ExpiresAt.HasValue && d.ExpiresAt.Value <= now,
            Stale = (d.LastUsedAt ?? d.CreatedAt) < now.AddDays(-90)
        }).Where(x => x.RotationDue || x.ExpiringSoon || x.Expired || x.Stale).ToList();
        return Ok(ApiResponse<object>.Ok(result));
    }

    // ---------------- console users ----------------

    [HttpGet("users")]
    [Authorize(Policy = RelayClaims.PolicyAdmin)]
    public async Task<IActionResult> Users(CancellationToken ct)
    {
        var users = await _adminUsers.ListUsersAsync(ct);
        return Ok(ApiResponse<List<AdminUserDto>>.Ok(users.Select(Mappers.ToDto).ToList()));
    }

    [HttpPost("users/{username}/enable")]
    [Authorize(Policy = RelayClaims.PolicyAdmin)]
    public async Task<IActionResult> EnableUser(string username, CancellationToken ct)
        => await _adminUsers.SetEnabledAsync(username, true, Actor, ct) ? Ok(ApiResponse.Ok("User enabled")) : NotFound(ApiResponse.Fail("User not found"));

    [HttpPost("users/{username}/disable")]
    [Authorize(Policy = RelayClaims.PolicyAdmin)]
    public async Task<IActionResult> DisableUser(string username, CancellationToken ct)
    {
        if (string.Equals(AdminAuthService.NormalizeUsername(username), AdminAuthService.NormalizeUsername(Actor), StringComparison.OrdinalIgnoreCase))
            return BadRequest(ApiResponse.Fail("You cannot disable your own account"));
        return await _adminUsers.SetEnabledAsync(username, false, Actor, ct) ? Ok(ApiResponse.Ok("User disabled")) : NotFound(ApiResponse.Fail("User not found"));
    }

    [HttpPost("users/{username}/clear-cache")]
    [Authorize(Policy = RelayClaims.PolicyAdmin)]
    public async Task<IActionResult> ClearUserCache(string username, CancellationToken ct)
        => await _adminUsers.ClearCacheAsync(username, Actor, ct) ? Ok(ApiResponse.Ok("Credential cache cleared; next login will be verified by AuthenAPI")) : NotFound(ApiResponse.Fail("User not found"));

    [HttpPost("cache/invalidate")]
    [Authorize(Policy = RelayClaims.PolicyAdmin)]
    public async Task<IActionResult> InvalidateCaches(CancellationToken ct)
    {
        _keys.InvalidateCache();
        await _blocks.RefreshAsync(ct);
        _audit.Log(new AuditLog { Category = AuditCategory.Admin, Action = "CachesInvalidated", Username = Actor, IpAddress = ClientIp, Details = "API key cache and block list refreshed" });
        return Ok(ApiResponse.Ok("Caches invalidated"));
    }
}
