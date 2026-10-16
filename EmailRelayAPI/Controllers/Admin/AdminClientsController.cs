using System.Security.Cryptography;
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

/// <summary>Manage API clients and their keys.</summary>
[Route("api/admin/clients")]
public class AdminClientsController : AdminControllerBase
{
    private readonly AppDbContext _db;
    private readonly IApiKeyService _keys;
    private readonly ISecretProtector _secrets;
    private readonly IAuditService _audit;
    private readonly IDailyQuotaService _quota;
    private readonly IOptionsMonitor<SecurityOptions> _security;
    private readonly IOptionsMonitor<ApiKeyOptions> _keyOptions;

    public AdminClientsController(AppDbContext db, IApiKeyService keys, ISecretProtector secrets, IAuditService audit, IDailyQuotaService quota, IOptionsMonitor<SecurityOptions> security, IOptionsMonitor<ApiKeyOptions> keyOptions)
    {
        _db = db;
        _keys = keys;
        _secrets = secrets;
        _audit = audit;
        _quota = quota;
        _security = security;
        _keyOptions = keyOptions;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var clients = await _db.Clients.AsNoTracking().Include(c => c.ApiKeys).Include(c => c.DefaultSenderProfile).OrderBy(c => c.Name).ToListAsync(ct);
        var list = new List<ClientDto>();
        foreach (var c in clients)
        {
            var dto = Mappers.ToDto(c, includeKeys: false);
            dto.UsedToday = await _quota.GetUsedTodayAsync(c.Id, ct);
            list.Add(dto);
        }
        return Ok(ApiResponse<List<ClientDto>>.Ok(list));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        var c = await _db.Clients.AsNoTracking().Include(x => x.ApiKeys).Include(x => x.DefaultSenderProfile).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c == null) return NotFound(ApiResponse.Fail("Client not found"));
        var dto = Mappers.ToDto(c);
        dto.UsedToday = await _quota.GetUsedTodayAsync(c.Id, ct);
        return Ok(ApiResponse<ClientDto>.Ok(dto));
    }

    [HttpPost]
    [Authorize(Policy = RelayClaims.PolicyAdmin)]
    public async Task<IActionResult> Create([FromBody] ClientUpsertRequest request, CancellationToken ct)
    {
        var error = await ValidateAsync(request, null, ct);
        if (error != null) return BadRequest(ApiResponse.Fail(error));

        var client = new ApiClient { CreatedBy = Actor };
        Apply(client, request);
        if (!string.IsNullOrEmpty(request.WebhookSecret)) client.WebhookSecretProtected = _secrets.Protect(request.WebhookSecret);
        _db.Clients.Add(client);
        await _db.SaveChangesAsync(ct);
        _keys.InvalidateCache();

        Audit("ClientCreated", client, $"Created client '{client.Name}'");
        return CreatedAtAction(nameof(Get), new { id = client.Id }, ApiResponse<ClientDto>.Ok(Mappers.ToDto(client), "Client created"));
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = RelayClaims.PolicyAdmin)]
    public async Task<IActionResult> Update(int id, [FromBody] ClientUpsertRequest request, CancellationToken ct)
    {
        var client = await _db.Clients.Include(c => c.ApiKeys).FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client == null) return NotFound(ApiResponse.Fail("Client not found"));

        var error = await ValidateAsync(request, id, ct);
        if (error != null) return BadRequest(ApiResponse.Fail(error));

        var wasActive = client.IsActive;
        Apply(client, request);
        if (request.WebhookSecret != null)
            client.WebhookSecretProtected = request.WebhookSecret.Length == 0 ? null : _secrets.Protect(request.WebhookSecret);
        client.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _keys.InvalidateCache();

        Audit("ClientUpdated", client, $"Updated client '{client.Name}'" + (wasActive != client.IsActive ? (client.IsActive ? " (enabled)" : " (disabled)") : string.Empty));
        await _db.Entry(client).Reference(c => c.DefaultSenderProfile).LoadAsync(ct);
        return Ok(ApiResponse<ClientDto>.Ok(Mappers.ToDto(client), "Client updated"));
    }

    [HttpDelete("{id:int}")]
    [Authorize(Policy = RelayClaims.PolicyAdmin)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client == null) return NotFound(ApiResponse.Fail("Client not found"));
        _db.Clients.Remove(client);
        await _db.SaveChangesAsync(ct);
        _keys.InvalidateCache();
        Audit("ClientDeleted", client, $"Deleted client '{client.Name}' and all of its keys");
        return Ok(ApiResponse.Ok("Client deleted"));
    }

    // ---------------- keys ----------------

    [HttpPost("{id:int}/keys")]
    [Authorize(Policy = RelayClaims.PolicyAdmin)]
    public async Task<IActionResult> CreateKey(int id, [FromBody] CreateApiKeyRequest request, CancellationToken ct)
    {
        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client == null) return NotFound(ApiResponse.Fail("Client not found"));

        var scopes = NormalizeScopes(request.Scopes);
        if (scopes == null) return BadRequest(ApiResponse.Fail("Scopes must be a comma separated subset of: send, read, admin"));
        if (request.ExpiresAt.HasValue && request.ExpiresAt.Value <= DateTime.UtcNow) return BadRequest(ApiResponse.Fail("ExpiresAt must be in the future"));

        var generated = _keys.Generate();
        var key = new ApiKey
        {
            ClientId = client.Id,
            Name = request.Name.Trim(),
            Prefix = generated.Prefix,
            KeyHash = generated.Hash,
            Scopes = scopes,
            ExpiresAt = request.ExpiresAt?.ToUniversalTime(),
            CreatedBy = Actor
        };
        _db.ApiKeys.Add(key);
        await _db.SaveChangesAsync(ct);
        _keys.InvalidateCache();

        Audit("KeyCreated", client, $"Created key '{key.Name}' ({key.Prefix}…) scopes [{key.Scopes}] for client '{client.Name}'", key.Id, key.Prefix);
        return Ok(ApiResponse<CreatedApiKeyResponse>.Ok(new CreatedApiKeyResponse { Key = Mappers.ToDto(key, client), PlainKey = generated.PlainKey },
            "API key created. Copy it now - it will not be shown again."));
    }

    [HttpPost("{id:int}/keys/{keyId:int}/revoke")]
    [Authorize(Policy = RelayClaims.PolicyAdmin)]
    public async Task<IActionResult> RevokeKey(int id, int keyId, [FromBody] RevokeKeyRequest? request, CancellationToken ct)
    {
        var key = await _db.ApiKeys.Include(k => k.Client).FirstOrDefaultAsync(k => k.Id == keyId && k.ClientId == id, ct);
        if (key == null) return NotFound(ApiResponse.Fail("Key not found"));
        if (key.RevokedAt != null) return Conflict(ApiResponse.Fail("Key is already revoked"));

        key.RevokedAt = DateTime.UtcNow;
        key.RevokedReason = string.IsNullOrWhiteSpace(request?.Reason) ? $"Revoked by {Actor}" : request!.Reason!.Trim();
        await _db.SaveChangesAsync(ct);
        _keys.InvalidateCache();

        Audit("KeyRevoked", key.Client, $"Revoked key '{key.Name}' ({key.Prefix}…): {key.RevokedReason}", key.Id, key.Prefix);
        return Ok(ApiResponse<ApiKeyDto>.Ok(Mappers.ToDto(key), "Key revoked"));
    }

    /// <summary>Issue a replacement key; the old key stays valid for the client's grace period.</summary>
    [HttpPost("{id:int}/keys/{keyId:int}/rotate")]
    [Authorize(Policy = RelayClaims.PolicyAdmin)]
    public async Task<IActionResult> RotateKey(int id, int keyId, CancellationToken ct)
    {
        var key = await _db.ApiKeys.Include(k => k.Client).FirstOrDefaultAsync(k => k.Id == keyId && k.ClientId == id, ct);
        if (key == null) return NotFound(ApiResponse.Fail("Key not found"));
        if (key.RevokedAt != null) return Conflict(ApiResponse.Fail("Cannot rotate a revoked key"));
        if (key.RotatedAt != null) return Conflict(ApiResponse.Fail("Key was already rotated"));

        var generated = _keys.Generate();
        var now = DateTime.UtcNow;
        var replacement = new ApiKey
        {
            ClientId = key.ClientId,
            Name = key.Name,
            Prefix = generated.Prefix,
            KeyHash = generated.Hash,
            Scopes = key.Scopes,
            CreatedBy = $"{Actor} (rotation of {key.Prefix})"
        };
        _db.ApiKeys.Add(replacement);
        await _db.SaveChangesAsync(ct);

        var grace = TimeSpan.FromDays(Math.Max(0, key.Client.KeyRotationGraceDays));
        if (grace < TimeSpan.FromHours(1)) grace = TimeSpan.FromHours(1);
        var oldExpiry = now.Add(grace);
        key.RotatedAt = now;
        key.ReplacedByKeyId = replacement.Id;
        if (key.ExpiresAt == null || key.ExpiresAt > oldExpiry) key.ExpiresAt = oldExpiry;
        await _db.SaveChangesAsync(ct);
        _keys.InvalidateCache();

        Audit("KeyRotated", key.Client, $"Rotated key '{key.Name}' {key.Prefix}… → {replacement.Prefix}…; old key expires {key.ExpiresAt:u}", replacement.Id, replacement.Prefix);
        return Ok(ApiResponse<CreatedApiKeyResponse>.Ok(new CreatedApiKeyResponse { Key = Mappers.ToDto(replacement, key.Client), PlainKey = generated.PlainKey },
            $"Key rotated. The previous key keeps working until {key.ExpiresAt:u}."));
    }

    // ---------------- signing secret ----------------

    [HttpPost("{id:int}/signing-secret")]
    [Authorize(Policy = RelayClaims.PolicyAdmin)]
    public async Task<IActionResult> GenerateSigningSecret(int id, CancellationToken ct)
    {
        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client == null) return NotFound(ApiResponse.Fail("Client not found"));

        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        client.SigningSecretProtected = _secrets.Protect(secret);
        client.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _keys.InvalidateCache();

        Audit("SigningSecretGenerated", client, $"Generated new request signing secret for '{client.Name}'");
        return Ok(ApiResponse<SigningSecretResponse>.Ok(new SigningSecretResponse { SigningSecret = secret }, "Signing secret generated"));
    }

    [HttpDelete("{id:int}/signing-secret")]
    [Authorize(Policy = RelayClaims.PolicyAdmin)]
    public async Task<IActionResult> RemoveSigningSecret(int id, CancellationToken ct)
    {
        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client == null) return NotFound(ApiResponse.Fail("Client not found"));
        client.SigningSecretProtected = null;
        client.RequireSignedRequests = false;
        client.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _keys.InvalidateCache();
        Audit("SigningSecretRemoved", client, $"Removed request signing secret for '{client.Name}'; signed requests disabled");
        return Ok(ApiResponse.Ok("Signing secret removed"));
    }

    // ---------------- helpers ----------------

    private async Task<string?> ValidateAsync(ClientUpsertRequest r, int? id, CancellationToken ct)
    {
        var name = r.Name.Trim();
        if (await _db.Clients.AnyAsync(c => c.Name == name && c.Id != id, ct)) return $"A client named '{name}' already exists";
        if (r.DefaultSenderProfileId.HasValue && !await _db.SenderProfiles.AnyAsync(p => p.Id == r.DefaultSenderProfileId.Value, ct)) return "Sender profile not found";

        foreach (var range in IpUtils.SplitList(r.AllowedIpRanges))
        {
            if (!IpUtils.TryParseCidr(range, out _, out _)) return $"Invalid IP or CIDR: '{range}'";
        }
        foreach (var entry in IpUtils.SplitList(r.AllowedFromAddresses))
        {
            var e = entry.StartsWith("*@") ? entry[1..] : entry;
            if (e.StartsWith('@'))
            {
                if (e.Length < 3 || !e.Contains('.')) return $"Invalid from domain: '{entry}'";
            }
            else if (!EmailQueueService.IsValidAddress(e)) return $"Invalid from address: '{entry}'";
        }
        foreach (var d in IpUtils.SplitList(r.AllowedRecipientDomains))
        {
            if (d.TrimStart('@', '*').Length < 3) return $"Invalid recipient domain: '{d}'";
        }
        if (!string.IsNullOrWhiteSpace(r.ContactEmail) && !EmailQueueService.IsValidAddress(r.ContactEmail.Trim())) return "Invalid contact email";
        if (r.RequireSignedRequests && id.HasValue)
        {
            var hasSecret = await _db.Clients.Where(c => c.Id == id.Value).Select(c => c.SigningSecretProtected != null).FirstOrDefaultAsync(ct);
            if (!hasSecret) return "Generate a signing secret before requiring signed requests";
        }
        else if (r.RequireSignedRequests)
        {
            return "Create the client first, generate a signing secret, then enable signed requests";
        }
        return null;
    }

    private void Apply(ApiClient c, ClientUpsertRequest r)
    {
        c.Name = r.Name.Trim();
        c.Description = r.Description?.Trim();
        c.IsActive = r.IsActive;
        c.DefaultSenderProfileId = r.DefaultSenderProfileId;
        c.AllowedIpRanges = Join(r.AllowedIpRanges);
        c.AllowedFromAddresses = Join(r.AllowedFromAddresses);
        c.AllowedRecipientDomains = Join(r.AllowedRecipientDomains);
        c.RateLimitPerMinute = r.RateLimitPerMinute;
        c.DailyQuota = r.DailyQuota;
        c.MaxRecipientsPerMessage = r.MaxRecipientsPerMessage;
        c.RequireSignedRequests = r.RequireSignedRequests;
        c.WebhookUrl = string.IsNullOrWhiteSpace(r.WebhookUrl) ? null : r.WebhookUrl.Trim();
        c.KeyRotationEnabled = r.KeyRotationEnabled;
        c.KeyRotationDays = r.KeyRotationDays;
        c.KeyRotationGraceDays = r.KeyRotationGraceDays;
        c.ContactEmail = r.ContactEmail?.Trim();
        c.Tags = r.Tags?.Trim();
    }

    private static string? Join(string? csv)
    {
        var list = IpUtils.SplitList(csv);
        return list.Count == 0 ? null : string.Join(", ", list);
    }

    private string? NormalizeScopes(string? scopes)
    {
        var list = IpUtils.SplitList(string.IsNullOrWhiteSpace(scopes) ? _security.CurrentValue.DefaultKeyScopes : scopes)
            .Select(s => s.ToLowerInvariant()).Distinct().ToList();
        if (list.Count == 0) return null;
        var allowed = new[] { RelayClaims.ScopeSend, RelayClaims.ScopeRead, RelayClaims.ScopeAdmin };
        return list.All(allowed.Contains) ? string.Join(",", list) : null;
    }

    private void Audit(string action, ApiClient client, string details, int? keyId = null, string? targetId = null)
        => _audit.Log(new AuditLog
        {
            Category = keyId.HasValue ? AuditCategory.ApiKey : AuditCategory.Client,
            Action = action,
            Username = Actor,
            IpAddress = ClientIp,
            ClientId = client.Id,
            ApiKeyId = keyId,
            TargetType = keyId.HasValue ? "ApiKey" : "Client",
            TargetId = targetId ?? client.Id.ToString(),
            Details = details,
            TraceId = HttpContext.GetTraceId()
        });
}
