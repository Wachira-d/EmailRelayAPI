using EmailRelayAPI.Data;
using EmailRelayAPI.Data.Entities;
using EmailRelayAPI.Models;
using EmailRelayAPI.Security;
using EmailRelayAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EmailRelayAPI.Controllers.Admin;

/// <summary>Reusable message templates.</summary>
[Route("api/admin/templates")]
public class AdminTemplatesController : AdminControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITemplateRenderer _renderer;
    private readonly IAuditService _audit;

    public AdminTemplatesController(AppDbContext db, ITemplateRenderer renderer, IAuditService audit)
    {
        _db = db;
        _renderer = renderer;
        _audit = audit;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int? clientId, CancellationToken ct)
    {
        var q = _db.Templates.AsNoTracking().AsQueryable();
        if (clientId.HasValue) q = q.Where(t => t.ClientId == clientId || t.ClientId == null);
        var items = await q.OrderBy(t => t.ClientId).ThenBy(t => t.Name).ToListAsync(ct);
        var clients = await _db.Clients.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        return Ok(ApiResponse<List<TemplateDto>>.Ok(items.Select(t => Mappers.ToDto(t, t.ClientId.HasValue ? clients.GetValueOrDefault(t.ClientId.Value) : null)).ToList()));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        var t = await _db.Templates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t == null) return NotFound(ApiResponse.Fail("Template not found"));
        return Ok(ApiResponse<TemplateDto>.Ok(Mappers.ToDto(t)));
    }

    [HttpPost]
    [Authorize(Policy = RelayClaims.PolicyAdmin)]
    public async Task<IActionResult> Create([FromBody] TemplateUpsertRequest request, CancellationToken ct)
    {
        var error = await ValidateAsync(request, null, ct);
        if (error != null) return BadRequest(ApiResponse.Fail(error));
        var t = new EmailTemplate { CreatedBy = Actor };
        Apply(t, request);
        _db.Templates.Add(t);
        await _db.SaveChangesAsync(ct);
        Audit("TemplateCreated", t);
        return CreatedAtAction(nameof(Get), new { id = t.Id }, ApiResponse<TemplateDto>.Ok(Mappers.ToDto(t), "Template created"));
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = RelayClaims.PolicyAdmin)]
    public async Task<IActionResult> Update(int id, [FromBody] TemplateUpsertRequest request, CancellationToken ct)
    {
        var t = await _db.Templates.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t == null) return NotFound(ApiResponse.Fail("Template not found"));
        var error = await ValidateAsync(request, id, ct);
        if (error != null) return BadRequest(ApiResponse.Fail(error));
        Apply(t, request);
        t.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        Audit("TemplateUpdated", t);
        return Ok(ApiResponse<TemplateDto>.Ok(Mappers.ToDto(t), "Template updated"));
    }

    [HttpDelete("{id:int}")]
    [Authorize(Policy = RelayClaims.PolicyAdmin)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var t = await _db.Templates.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t == null) return NotFound(ApiResponse.Fail("Template not found"));
        _db.Templates.Remove(t);
        await _db.SaveChangesAsync(ct);
        Audit("TemplateDeleted", t);
        return Ok(ApiResponse.Ok("Template deleted"));
    }

    [HttpPost("{id:int}/preview")]
    public async Task<IActionResult> Preview(int id, [FromBody] Dictionary<string, string>? variables, CancellationToken ct)
    {
        var t = await _db.Templates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t == null) return NotFound(ApiResponse.Fail("Template not found"));
        return Ok(ApiResponse<object>.Ok(new
        {
            subject = _renderer.Render(t.Subject, variables, false),
            html = t.HtmlBody == null ? null : _renderer.Render(t.HtmlBody, variables, true),
            text = t.TextBody == null ? null : _renderer.Render(t.TextBody, variables, false)
        }));
    }

    private async Task<string?> ValidateAsync(TemplateUpsertRequest r, int? id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.HtmlBody) && string.IsNullOrWhiteSpace(r.TextBody)) return "Provide an HTML body, a text body or both";
        if (r.ClientId.HasValue && !await _db.Clients.AnyAsync(c => c.Id == r.ClientId.Value, ct)) return "Client not found";
        var name = r.Name.Trim();
        if (await _db.Templates.AnyAsync(t => t.Name == name && t.ClientId == r.ClientId && t.Id != id, ct)) return $"A template named '{name}' already exists for this scope";
        return null;
    }

    private static void Apply(EmailTemplate t, TemplateUpsertRequest r)
    {
        t.ClientId = r.ClientId;
        t.Name = r.Name.Trim();
        t.Description = r.Description?.Trim();
        t.Subject = r.Subject.Trim();
        t.HtmlBody = string.IsNullOrWhiteSpace(r.HtmlBody) ? null : r.HtmlBody;
        t.TextBody = string.IsNullOrWhiteSpace(r.TextBody) ? null : r.TextBody;
        t.IsActive = r.IsActive;
    }

    private void Audit(string action, EmailTemplate t)
        => _audit.Log(new AuditLog { Category = AuditCategory.Admin, Action = action, Username = Actor, IpAddress = ClientIp, ClientId = t.ClientId, TargetType = "Template", TargetId = t.Id.ToString(), Details = $"{action}: '{t.Name}' ({(t.ClientId.HasValue ? "client " + t.ClientId : "global")})" });
}
