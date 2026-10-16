using System.Text.Json;
using EmailRelayAPI.Data;
using EmailRelayAPI.Data.Entities;
using EmailRelayAPI.Models;
using EmailRelayAPI.Security;
using EmailRelayAPI.Services;
using EmailRelayAPI.Services.Transports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EmailRelayAPI.Controllers.Admin;

/// <summary>Manage sender profiles (SMTP servers and mail APIs).</summary>
[Route("api/admin/profiles")]
public class AdminProfilesController : AdminControllerBase
{
    private readonly AppDbContext _db;
    private readonly ISecretProtector _secrets;
    private readonly IEmailTransportResolver _transports;
    private readonly IApiKeyService _keys;
    private readonly IAuditService _audit;

    public AdminProfilesController(AppDbContext db, ISecretProtector secrets, IEmailTransportResolver transports, IApiKeyService keys, IAuditService audit)
    {
        _db = db;
        _secrets = secrets;
        _transports = transports;
        _keys = keys;
        _audit = audit;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        var profiles = await _db.SenderProfiles.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
        var clientCounts = await _db.Clients.Where(c => c.DefaultSenderProfileId != null)
            .GroupBy(c => c.DefaultSenderProfileId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var stats = await _db.Messages.Where(m => m.SentAt >= today || m.CompletedAt >= today)
            .GroupBy(m => m.SenderProfileId)
            .Select(g => new { g.Key, Sent = g.Sum(m => (m.Status == EmailStatus.Sent && m.SentAt >= today) ? 1 : 0), Failed = g.Sum(m => (m.Status == EmailStatus.Failed && m.CompletedAt >= today) ? 1 : 0) })
            .ToListAsync(ct);

        var list = profiles.Select(p =>
        {
            var dto = Mappers.ToDto(p);
            dto.ClientCount = clientCounts.FirstOrDefault(c => c.Key == p.Id)?.Count ?? 0;
            var s = stats.FirstOrDefault(x => x.Key == p.Id);
            dto.SentToday = s?.Sent ?? 0;
            dto.FailedToday = s?.Failed ?? 0;
            return dto;
        }).ToList();
        return Ok(ApiResponse<List<SenderProfileDto>>.Ok(list));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        var p = await _db.SenderProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p == null) return NotFound(ApiResponse.Fail("Sender profile not found"));
        var dto = Mappers.ToDto(p);
        dto.ClientCount = await _db.Clients.CountAsync(c => c.DefaultSenderProfileId == id, ct);
        return Ok(ApiResponse<SenderProfileDto>.Ok(dto));
    }

    [HttpPost]
    [Authorize(Policy = RelayClaims.PolicyAdmin)]
    public async Task<IActionResult> Create([FromBody] SenderProfileUpsertRequest request, CancellationToken ct)
    {
        var error = await ValidateAsync(request, null, ct);
        if (error != null) return BadRequest(ApiResponse.Fail(error));

        var p = new SenderProfile { CreatedBy = Actor };
        Apply(p, request, isNew: true);
        _db.SenderProfiles.Add(p);
        await _db.SaveChangesAsync(ct);
        Audit("ProfileCreated", p, $"Created sender profile '{p.Name}' ({p.Type}{(p.Type == TransportType.HttpApi ? "/" + p.ApiProvider : "")})");
        return CreatedAtAction(nameof(Get), new { id = p.Id }, ApiResponse<SenderProfileDto>.Ok(Mappers.ToDto(p), "Sender profile created"));
    }

    [HttpPut("{id:int}")]
    [HttpPost("{id:int}/update")]
    [Authorize(Policy = RelayClaims.PolicyAdmin)]
    public async Task<IActionResult> Update(int id, [FromBody] SenderProfileUpsertRequest request, CancellationToken ct)
    {
        var p = await _db.SenderProfiles.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p == null) return NotFound(ApiResponse.Fail("Sender profile not found"));
        var error = await ValidateAsync(request, id, ct);
        if (error != null) return BadRequest(ApiResponse.Fail(error));

        Apply(p, request, isNew: false);
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _keys.InvalidateCache();
        Audit("ProfileUpdated", p, $"Updated sender profile '{p.Name}'");
        return Ok(ApiResponse<SenderProfileDto>.Ok(Mappers.ToDto(p), "Sender profile updated"));
    }

    [HttpDelete("{id:int}")]
    [HttpPost("{id:int}/delete")]
    [Authorize(Policy = RelayClaims.PolicyAdmin)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var p = await _db.SenderProfiles.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p == null) return NotFound(ApiResponse.Fail("Sender profile not found"));
        if (await _db.Messages.AnyAsync(m => m.SenderProfileId == id, ct))
            return Conflict(ApiResponse.Fail("Profile has message history and cannot be deleted. Disable it instead."));

        var clients = await _db.Clients.Where(c => c.DefaultSenderProfileId == id).ToListAsync(ct);
        foreach (var c in clients) c.DefaultSenderProfileId = null;
        _db.SenderProfiles.Remove(p);
        await _db.SaveChangesAsync(ct);
        _keys.InvalidateCache();
        Audit("ProfileDeleted", p, $"Deleted sender profile '{p.Name}' (unassigned from {clients.Count} client(s))");
        return Ok(ApiResponse.Ok("Sender profile deleted"));
    }

    /// <summary>Connectivity / credential test without sending mail.</summary>
    [HttpPost("{id:int}/test")]
    [Authorize(Policy = RelayClaims.PolicyAdmin)]
    public async Task<IActionResult> Test(int id, CancellationToken ct)
    {
        var p = await _db.SenderProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p == null) return NotFound(ApiResponse.Fail("Sender profile not found"));

        var result = await _transports.Resolve(p).TestAsync(p, ct);
        Audit("ProfileTested", p, $"Connection test for '{p.Name}': {(result.Success ? "OK" : "FAILED")} {result.Error ?? result.Response}");
        return Ok(result.Success
            ? ApiResponse<TransportResult>.Ok(result, "Connection test successful")
            : ApiResponse<TransportResult>.Fail(result.Error ?? "Connection test failed", result));
    }

    /// <summary>Send a real test message synchronously through this profile.</summary>
    [HttpPost("{id:int}/test-send")]
    [Authorize(Policy = RelayClaims.PolicyAdmin)]
    public async Task<IActionResult> TestSend(int id, [FromBody] TestSendRequest request, CancellationToken ct)
    {
        var p = await _db.SenderProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p == null) return NotFound(ApiResponse.Fail("Sender profile not found"));
        if (!EmailQueueService.IsValidAddress(request.To.Trim())) return BadRequest(ApiResponse.Fail("Invalid recipient address"));

        var email = new OutboundEmail
        {
            Id = Guid.NewGuid(),
            FromAddress = p.FromAddress,
            FromName = p.FromName,
            ReplyTo = p.ReplyTo,
            To = new List<EmailAddressDto> { new() { Address = request.To.Trim() } },
            Subject = $"[EmailRelay] Test message from profile '{p.Name}'",
            Text = $"This is a test message sent by {Actor} at {DateTime.UtcNow:u} through sender profile '{p.Name}' ({p.Type}).",
            Html = $"<p>This is a <strong>test message</strong> sent by {System.Net.WebUtility.HtmlEncode(Actor)} at {DateTime.UtcNow:u} through sender profile <code>{System.Net.WebUtility.HtmlEncode(p.Name)}</code> ({p.Type}).</p>"
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await _transports.Resolve(p).SendAsync(p, email, ct);
        sw.Stop();

        var tracked = await _db.SenderProfiles.FirstAsync(x => x.Id == id, ct);
        if (result.Success) { tracked.LastSuccessAt = DateTime.UtcNow; tracked.ConsecutiveFailures = 0; tracked.LastError = null; }
        else { tracked.LastFailureAt = DateTime.UtcNow; tracked.LastError = result.Error; }
        await _db.SaveChangesAsync(ct);

        Audit("ProfileTestSend", p, $"Test send via '{p.Name}' to {request.To}: {(result.Success ? "OK" : "FAILED")} in {sw.ElapsedMilliseconds}ms. {result.Error ?? result.Response}");
        return Ok(result.Success
            ? ApiResponse<TransportResult>.Ok(result, $"Test message sent in {sw.ElapsedMilliseconds} ms")
            : ApiResponse<TransportResult>.Fail(result.Error ?? "Send failed", result));
    }

    // ---------------- helpers ----------------

    private async Task<string?> ValidateAsync(SenderProfileUpsertRequest r, int? id, CancellationToken ct)
    {
        var name = r.Name.Trim();
        if (await _db.SenderProfiles.AnyAsync(p => p.Name == name && p.Id != id, ct)) return $"A sender profile named '{name}' already exists";
        if (!EmailQueueService.IsValidAddress(r.FromAddress.Trim())) return "From address is invalid";
        if (!string.IsNullOrWhiteSpace(r.ReplyTo) && !EmailQueueService.IsValidAddress(r.ReplyTo.Trim())) return "Reply-To address is invalid";

        if (r.Type == TransportType.Smtp)
        {
            if (string.IsNullOrWhiteSpace(r.SmtpHost)) return "SMTP host is required";
        }
        else
        {
            switch (r.ApiProvider)
            {
                case ApiProvider.Generic:
                    if (string.IsNullOrWhiteSpace(r.ApiEndpoint) || !Uri.TryCreate(r.ApiEndpoint, UriKind.Absolute, out _)) return "A valid absolute API endpoint URL is required for the Generic provider";
                    break;
                case ApiProvider.Mailgun:
                    if (string.IsNullOrWhiteSpace(r.ApiDomain)) return "Mailgun sending domain (ApiDomain) is required";
                    break;
                case ApiProvider.MicrosoftGraph:
                    if (string.IsNullOrWhiteSpace(r.OAuthTenantId) || string.IsNullOrWhiteSpace(r.OAuthClientId)) return "Microsoft Graph requires OAuthTenantId and OAuthClientId";
                    break;
            }
            if (!string.IsNullOrWhiteSpace(r.ApiEndpoint) && !Uri.TryCreate(r.ApiEndpoint, UriKind.Absolute, out _)) return "API endpoint must be an absolute URL";
        }

        if (!string.IsNullOrWhiteSpace(r.ExtraHeadersJson))
        {
            try
            {
                var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(r.ExtraHeadersJson);
                if (headers == null) return "ExtraHeadersJson must be a JSON object of string values";
            }
            catch (JsonException)
            {
                return "ExtraHeadersJson must be a JSON object of string values";
            }
        }
        return null;
    }

    private void Apply(SenderProfile p, SenderProfileUpsertRequest r, bool isNew)
    {
        p.Name = r.Name.Trim();
        p.Description = r.Description?.Trim();
        p.Type = r.Type;
        p.IsActive = r.IsActive;
        p.FromAddress = r.FromAddress.Trim();
        p.FromName = string.IsNullOrWhiteSpace(r.FromName) ? null : r.FromName.Trim();
        p.ReplyTo = string.IsNullOrWhiteSpace(r.ReplyTo) ? null : r.ReplyTo.Trim();

        p.SmtpHost = string.IsNullOrWhiteSpace(r.SmtpHost) ? null : r.SmtpHost.Trim();
        p.SmtpPort = r.SmtpPort;
        p.SmtpSecurity = r.SmtpSecurity;
        p.SmtpUsername = string.IsNullOrWhiteSpace(r.SmtpUsername) ? null : r.SmtpUsername.Trim();
        if (r.SmtpPassword != null || isNew) p.SmtpPasswordProtected = string.IsNullOrEmpty(r.SmtpPassword) ? null : _secrets.Protect(r.SmtpPassword);
        p.SkipCertificateValidation = r.SkipCertificateValidation;
        p.SmtpLocalDomain = string.IsNullOrWhiteSpace(r.SmtpLocalDomain) ? null : r.SmtpLocalDomain.Trim();

        p.ApiProvider = r.ApiProvider;
        p.ApiEndpoint = string.IsNullOrWhiteSpace(r.ApiEndpoint) ? null : r.ApiEndpoint.Trim();
        if (r.ApiKey != null || isNew) p.ApiKeyProtected = string.IsNullOrEmpty(r.ApiKey) ? null : _secrets.Protect(r.ApiKey);
        p.ApiDomain = string.IsNullOrWhiteSpace(r.ApiDomain) ? null : r.ApiDomain.Trim();
        p.OAuthTenantId = string.IsNullOrWhiteSpace(r.OAuthTenantId) ? null : r.OAuthTenantId.Trim();
        p.OAuthClientId = string.IsNullOrWhiteSpace(r.OAuthClientId) ? null : r.OAuthClientId.Trim();
        if (r.OAuthClientSecret != null || isNew) p.OAuthClientSecretProtected = string.IsNullOrEmpty(r.OAuthClientSecret) ? null : _secrets.Protect(r.OAuthClientSecret);
        p.ExtraHeadersJson = string.IsNullOrWhiteSpace(r.ExtraHeadersJson) ? null : r.ExtraHeadersJson.Trim();

        p.TimeoutSeconds = r.TimeoutSeconds;
        p.MaxConcurrency = r.MaxConcurrency;
        p.MaxPerMinute = r.MaxPerMinute;
    }

    private void Audit(string action, SenderProfile p, string details)
        => _audit.Log(new AuditLog
        {
            Category = AuditCategory.Admin,
            Action = action,
            Username = Actor,
            IpAddress = ClientIp,
            TargetType = "SenderProfile",
            TargetId = p.Id.ToString(),
            Details = details,
            TraceId = HttpContext.GetTraceId()
        });
}
