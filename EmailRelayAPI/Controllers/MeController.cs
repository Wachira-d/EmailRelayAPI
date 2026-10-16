using EmailRelayAPI.Data;
using EmailRelayAPI.Data.Entities;
using EmailRelayAPI.Models;
using EmailRelayAPI.Security;
using EmailRelayAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EmailRelayAPI.Controllers;

/// <summary>Information about the calling client and self-service key rotation.</summary>
[ApiController]
[Route("api/v1/me")]
[Authorize(AuthenticationSchemes = RelayClaims.ApiKeyScheme)]
[Produces("application/json")]
public class MeController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IDailyQuotaService _quota;
    private readonly IApiKeyService _keys;
    private readonly IAuditService _audit;

    public MeController(AppDbContext db, IDailyQuotaService quota, IApiKeyService keys, IAuditService audit)
    {
        _db = db;
        _quota = quota;
        _keys = keys;
        _audit = audit;
    }

    /// <summary>Current client, limits, quota usage and key information.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<ClientSelfInfo>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var cc = HttpContext.GetClientContext()!;
        var client = await _db.Clients.AsNoTracking().Include(c => c.DefaultSenderProfile).FirstAsync(c => c.Id == cc.ClientId, ct);
        var key = await _db.ApiKeys.AsNoTracking().FirstAsync(k => k.Id == cc.ApiKeyId, ct);

        var info = new ClientSelfInfo
        {
            ClientId = client.Id,
            Name = client.Name,
            SenderProfile = client.DefaultSenderProfile?.Name,
            DefaultFrom = client.DefaultSenderProfile?.FromAddress,
            RateLimitPerMinute = client.RateLimitPerMinute,
            DailyQuota = client.DailyQuota,
            UsedToday = await _quota.GetUsedTodayAsync(client.Id, ct),
            MaxRecipientsPerMessage = client.MaxRecipientsPerMessage,
            RequireSignedRequests = client.RequireSignedRequests,
            Scopes = cc.Scopes.ToList(),
            KeyName = key.Name,
            KeyPrefix = key.Prefix,
            KeyExpiresAt = key.ExpiresAt,
            KeyRotationEnabled = client.KeyRotationEnabled,
            KeyRotationDueAt = client.KeyRotationEnabled ? key.CreatedAt.AddDays(client.KeyRotationDays) : null
        };
        return Ok(ApiResponse<ClientSelfInfo>.Ok(info));
    }

    /// <summary>
    /// Rotate the calling API key: a new key with the same scopes is issued and the current key keeps working
    /// during the client's grace period (default 7 days). The new key is returned once.
    /// </summary>
    [HttpPost("keys/rotate")]
    [ProducesResponseType(typeof(ApiResponse<KeyRotationResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Rotate(CancellationToken ct)
    {
        var cc = HttpContext.GetClientContext()!;
        var client = await _db.Clients.FirstAsync(c => c.Id == cc.ClientId, ct);
        var current = await _db.ApiKeys.FirstAsync(k => k.Id == cc.ApiKeyId, ct);
        if (current.RotatedAt != null)
            return Conflict(ApiResponse.Fail("This key has already been rotated; use the replacement key"));

        var generated = _keys.Generate();
        var grace = TimeSpan.FromDays(Math.Max(0, client.KeyRotationGraceDays));
        if (grace < TimeSpan.FromHours(1)) grace = TimeSpan.FromHours(1);
        var now = DateTime.UtcNow;

        var replacement = new ApiKey
        {
            ClientId = client.Id,
            Name = current.Name,
            Prefix = generated.Prefix,
            KeyHash = generated.Hash,
            Scopes = current.Scopes,
            CreatedAt = now,
            ExpiresAt = null,
            CreatedBy = $"rotation:{current.Prefix}"
        };
        _db.ApiKeys.Add(replacement);
        await _db.SaveChangesAsync(ct);

        current.RotatedAt = now;
        current.ReplacedByKeyId = replacement.Id;
        var oldExpiry = now.Add(grace);
        if (current.ExpiresAt == null || current.ExpiresAt > oldExpiry) current.ExpiresAt = oldExpiry;
        await _db.SaveChangesAsync(ct);
        _keys.InvalidateCache();

        _audit.Log(new AuditLog
        {
            Category = AuditCategory.ApiKey,
            Action = "KeyRotatedSelfService",
            ClientId = client.Id,
            ApiKeyId = current.Id,
            IpAddress = HttpContext.GetClientIp(),
            UserAgent = HttpContext.GetUserAgent(),
            TargetType = "ApiKey",
            TargetId = replacement.Prefix,
            Details = $"Client '{client.Name}' rotated key {current.Prefix}… → {replacement.Prefix}…; old key expires {current.ExpiresAt:u}",
            TraceId = HttpContext.GetTraceId()
        });

        return Ok(ApiResponse<KeyRotationResponse>.Ok(new KeyRotationResponse
        {
            NewKey = generated.PlainKey,
            NewKeyPrefix = generated.Prefix,
            OldKeyExpiresAt = current.ExpiresAt!.Value,
            Note = "Store the new key now; it will not be shown again. The old key keeps working until it expires."
        }, "Key rotated"));
    }
}
