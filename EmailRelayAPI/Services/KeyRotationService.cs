using EmailRelayAPI.Data;
using EmailRelayAPI.Data.Entities;
using EmailRelayAPI.Options;
using EmailRelayAPI.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EmailRelayAPI.Services;

/// <summary>
/// Optional key rotation policy. For clients that opted in, keys older than KeyRotationDays get an expiry
/// (now + grace period) and operators are notified so a new key can be issued. Rotation is never forced
/// on clients that did not enable it.
/// </summary>
public class KeyRotationService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAuditService _audit;
    private readonly IAlertService _alerts;
    private readonly IApiKeyService _keys;
    private readonly IOptionsMonitor<ApiKeyOptions> _options;
    private readonly ILogger<KeyRotationService> _logger;

    public KeyRotationService(IServiceScopeFactory scopeFactory, IAuditService audit, IAlertService alerts, IApiKeyService keys, IOptionsMonitor<ApiKeyOptions> options, ILogger<KeyRotationService> logger)
    {
        _scopeFactory = scopeFactory;
        _audit = audit;
        _alerts = alerts;
        _keys = keys;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Key rotation check failed");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(5, _options.CurrentValue.RotationCheckIntervalMinutes)), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task<int> CheckAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;

        var keys = await db.ApiKeys.Include(k => k.Client)
            .Where(k => k.Client.KeyRotationEnabled && k.RevokedAt == null && k.RotatedAt == null)
            .ToListAsync(ct);

        var flagged = 0;
        foreach (var key in keys)
        {
            var due = key.CreatedAt.AddDays(Math.Max(1, key.Client.KeyRotationDays));
            if (due > now) continue;
            var hardExpiry = due.AddDays(Math.Max(0, key.Client.KeyRotationGraceDays));
            if (key.ExpiresAt.HasValue && key.ExpiresAt.Value <= hardExpiry) continue; // already handled or admin set an earlier expiry

            key.ExpiresAt = hardExpiry;
            flagged++;
            _audit.Log(new AuditLog
            {
                Category = AuditCategory.ApiKey,
                Severity = AuditSeverity.Warning,
                Action = "KeyRotationDue",
                ClientId = key.ClientId,
                ApiKeyId = key.Id,
                TargetType = "ApiKey",
                TargetId = key.Prefix,
                Details = $"Key '{key.Name}' ({key.Prefix}…) of client '{key.Client.Name}' is older than {key.Client.KeyRotationDays} days and will expire at {hardExpiry:u}. Issue a new key (admin UI or POST /api/v1/me/keys/rotate)."
            });
            _ = _alerts.RaiseAsync("KeyRotationDue", $"API key rotation due for '{key.Client.Name}'",
                $"Key '{key.Name}' ({key.Prefix}…) expires at {hardExpiry:u} because key rotation is enabled for this client.", CancellationToken.None);
        }

        if (flagged > 0)
        {
            await db.SaveChangesAsync(ct);
            _keys.InvalidateCache();
            _logger.LogWarning("{Count} API key(s) flagged for rotation", flagged);
        }
        return flagged;
    }
}
