using EmailRelayAPI.Data;
using EmailRelayAPI.Data.Entities;
using EmailRelayAPI.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EmailRelayAPI.Services;

/// <summary>Purges old messages, attachments and logs according to the retention policy.</summary>
public class RetentionService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEmailQueueService _messages;
    private readonly IAuditService _audit;
    private readonly IOptionsMonitor<RetentionOptions> _options;
    private readonly ILogger<RetentionService> _logger;

    public RetentionService(IServiceScopeFactory scopeFactory, IEmailQueueService messages, IAuditService audit, IOptionsMonitor<RetentionOptions> options, ILogger<RetentionService> logger)
    {
        _scopeFactory = scopeFactory;
        _messages = messages;
        _audit = audit;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = _options.CurrentValue;
            var delay = ComputeDelay(opts);
            _logger.LogInformation("Next retention purge in {Delay}", delay);
            try { await Task.Delay(delay, stoppingToken); } catch (OperationCanceledException) { break; }

            if (!opts.Enabled) continue;
            try
            {
                await PurgeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Retention purge failed");
            }
        }
    }

    public async Task<(int Messages, int Audit, int Requests, int Blocks)> PurgeAsync(CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        var now = DateTime.UtcNow;
        var messages = 0;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var messageCutoff = now.AddDays(-Math.Max(1, opts.MessageRetentionDays));
        while (true)
        {
            var batch = await db.Messages.Include(m => m.Attachments)
                .Where(m => m.CreatedAt < messageCutoff && m.Status != EmailStatus.Queued && m.Status != EmailStatus.Sending)
                .OrderBy(m => m.CreatedAt)
                .Take(200)
                .ToListAsync(ct);
            if (batch.Count == 0) break;
            foreach (var m in batch) _messages.DeleteAttachmentFiles(m);
            db.Messages.RemoveRange(batch);
            await db.SaveChangesAsync(ct);
            messages += batch.Count;
        }

        var auditCutoff = now.AddDays(-Math.Max(1, opts.AuditRetentionDays));
        var audits = await db.AuditLogs.Where(a => a.Timestamp < auditCutoff).ExecuteDeleteAsync(ct);

        var requestCutoff = now.AddDays(-Math.Max(1, opts.RequestLogRetentionDays));
        var requests = await db.RequestLogs.Where(r => r.Timestamp < requestCutoff).ExecuteDeleteAsync(ct);

        var systemCutoff = now.AddDays(-Math.Max(1, opts.SystemLogRetentionDays));
        var systemLogs = await db.SystemLogs.Where(l => l.Timestamp < systemCutoff).ExecuteDeleteAsync(ct);

        var blockCutoff = now.AddDays(-30);
        var blocks = await db.BlockEntries
            .Where(b => (b.RemovedAt != null && b.RemovedAt < blockCutoff) || (!b.IsPermanent && b.ExpiresAt != null && b.ExpiresAt < blockCutoff))
            .ExecuteDeleteAsync(ct);

        _audit.Log(new AuditLog
        {
            Category = AuditCategory.System,
            Action = "RetentionPurge",
            Details = $"Purged {messages} messages, {audits} audit entries, {requests} request logs, {systemLogs} system log entries, {blocks} old block entries"
        });
        _logger.LogInformation("Retention purge: {Messages} messages, {Audit} audit, {Requests} request logs, {Blocks} blocks", messages, audits, requests, blocks);
        return (messages, audits, requests, blocks);
    }

    private static TimeSpan ComputeDelay(RetentionOptions opts)
    {
        var interval = TimeSpan.FromHours(Math.Max(1, opts.PurgeIntervalHours));
        if (!string.IsNullOrWhiteSpace(opts.PurgeTime) && TimeOnly.TryParse(opts.PurgeTime, out var time))
        {
            var now = DateTime.Now;
            var next = now.Date.Add(time.ToTimeSpan());
            if (next <= now) next = next.AddDays(1);
            return next - now;
        }
        return interval;
    }
}
