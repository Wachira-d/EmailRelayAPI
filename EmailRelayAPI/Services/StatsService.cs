using EmailRelayAPI.Data;
using EmailRelayAPI.Data.Entities;
using EmailRelayAPI.Models;
using EmailRelayAPI.Options;
using EmailRelayAPI.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EmailRelayAPI.Services;

public interface IStatsService
{
    Task<DashboardDto> GetDashboardAsync(CancellationToken ct = default);
    Task<UsageReportDto> GetUsageAsync(DateTime from, DateTime to, int? clientId, CancellationToken ct = default);
}

/// <summary>Aggregations for the dashboard and usage reports.</summary>
public class StatsService : IStatsService
{
    private readonly AppDbContext _db;
    private readonly IDeliveryQueue _queue;
    private readonly IBlockListService _blocks;
    private readonly IOptionsMonitor<DeliveryOptions> _delivery;

    public StatsService(AppDbContext db, IDeliveryQueue queue, IBlockListService blocks, IOptionsMonitor<DeliveryOptions> delivery)
    {
        _db = db;
        _queue = queue;
        _blocks = blocks;
        _delivery = delivery;
    }

    public async Task<DashboardDto> GetDashboardAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var today = now.Date;
        var dayAgo = now.AddHours(-24);
        var weekAgo = today.AddDays(-6);
        var dto = new DashboardDto { StartedAt = AppInfo.StartedAt, Version = AppInfo.Version };

        var statusToday = await _db.Messages.Where(m => m.CreatedAt >= today)
            .GroupBy(m => m.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        dto.AcceptedToday = statusToday.Sum(s => s.Count);

        dto.SentToday = await _db.Messages.CountAsync(m => m.Status == EmailStatus.Sent && m.SentAt >= today, ct);
        dto.FailedToday = await _db.Messages.CountAsync(m => m.Status == EmailStatus.Failed && m.CompletedAt >= today, ct);
        dto.QueuedNow = await _db.Messages.CountAsync(m => m.Status == EmailStatus.Queued, ct);
        dto.ScheduledNow = await _db.Messages.CountAsync(m => m.Status == EmailStatus.Scheduled, ct);
        dto.SendingNow = await _db.Messages.CountAsync(m => m.Status == EmailStatus.Sending, ct);
        dto.Sent7Days = await _db.Messages.CountAsync(m => m.Status == EmailStatus.Sent && m.SentAt >= weekAgo, ct);
        dto.Failed7Days = await _db.Messages.CountAsync(m => m.Status == EmailStatus.Failed && m.CompletedAt >= weekAgo, ct);
        dto.AvgDurationMsToday = await _db.Messages.Where(m => m.Status == EmailStatus.Sent && m.SentAt >= today && m.DurationMs != null)
            .Select(m => (double?)m.DurationMs).AverageAsync(ct);
        var finishedToday = dto.SentToday + dto.FailedToday;
        dto.SuccessRateToday = finishedToday == 0 ? 100 : Math.Round(dto.SentToday * 100.0 / finishedToday, 1);

        dto.ActiveClients = await _db.Clients.CountAsync(c => c.IsActive, ct);
        dto.ActiveKeys = await _db.ApiKeys.CountAsync(k => k.RevokedAt == null && (k.ExpiresAt == null || k.ExpiresAt > now), ct);
        dto.ActiveProfiles = await _db.SenderProfiles.CountAsync(p => p.IsActive, ct);
        var unhealthy = Math.Max(1, _delivery.CurrentValue.ProfileUnhealthyThreshold);
        dto.UnhealthyProfiles = await _db.SenderProfiles.CountAsync(p => p.IsActive && p.ConsecutiveFailures >= unhealthy, ct);
        dto.ActiveBlocks = _blocks.Snapshot().Count(b => b.IsEffective(now));
        dto.SecurityEvents24h = await _db.AuditLogs.CountAsync(a => a.Category == AuditCategory.Security && a.Timestamp >= dayAgo, ct);
        dto.CriticalEvents24h = await _db.AuditLogs.CountAsync(a => a.Severity == AuditSeverity.Critical && a.Timestamp >= dayAgo, ct);
        dto.AuthFailures24h = await _db.AuditLogs.CountAsync(a => a.Category == AuditCategory.Authentication && !a.Success && a.Timestamp >= dayAgo, ct);
        dto.QueueInFlight = _queue.InFlightCount;
        dto.QueuePending = _queue.PendingCount;

        var rotationKeys = await _db.ApiKeys.Include(k => k.Client)
            .Where(k => k.Client.KeyRotationEnabled && k.RevokedAt == null && k.RotatedAt == null)
            .Select(k => new { k.CreatedAt, k.Client.KeyRotationDays })
            .ToListAsync(ct);
        dto.KeysDueForRotation = rotationKeys.Count(k => k.CreatedAt.AddDays(k.KeyRotationDays) <= now);

        // hourly series (last 24h)
        var recent = await _db.Messages.Where(m => m.CreatedAt >= dayAgo || m.SentAt >= dayAgo || m.CompletedAt >= dayAgo)
            .Select(m => new { m.CreatedAt, m.Status, m.SentAt, m.CompletedAt })
            .ToListAsync(ct);
        var hourStart = new DateTime(dayAgo.Year, dayAgo.Month, dayAgo.Day, dayAgo.Hour, 0, 0, DateTimeKind.Utc);
        for (var h = hourStart; h <= now; h = h.AddHours(1))
        {
            var end = h.AddHours(1);
            dto.Hourly.Add(new TimeBucketDto
            {
                Bucket = h,
                Accepted = recent.Count(m => m.CreatedAt >= h && m.CreatedAt < end),
                Sent = recent.Count(m => m.Status == EmailStatus.Sent && m.SentAt >= h && m.SentAt < end),
                Failed = recent.Count(m => m.Status == EmailStatus.Failed && m.CompletedAt >= h && m.CompletedAt < end)
            });
        }

        // daily series (last 30 days)
        dto.Daily = await DailySeriesAsync(today.AddDays(-29), today.AddDays(1), null, ct);

        // top clients today
        dto.TopClients = await ClientUsageAsync(today, today.AddDays(1), null, ct);
        dto.TopClients = dto.TopClients.OrderByDescending(c => c.Accepted).Take(10).ToList();

        // profiles
        var profileStats = await _db.Messages.Where(m => m.CreatedAt >= today || m.SentAt >= today || m.CompletedAt >= today)
            .GroupBy(m => m.SenderProfileId)
            .Select(g => new
            {
                g.Key,
                Sent = g.Sum(m => (m.Status == EmailStatus.Sent && m.SentAt >= today) ? 1 : 0),
                Failed = g.Sum(m => (m.Status == EmailStatus.Failed && m.CompletedAt >= today) ? 1 : 0)
            }).ToListAsync(ct);
        var profiles = await _db.SenderProfiles.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
        dto.Profiles = profiles.Select(p =>
        {
            var s = profileStats.FirstOrDefault(x => x.Key == p.Id);
            return new SenderProfileHealthDto
            {
                Id = p.Id,
                Name = p.Name,
                Type = p.Type.ToString(),
                IsActive = p.IsActive,
                Healthy = p.ConsecutiveFailures < unhealthy,
                ConsecutiveFailures = p.ConsecutiveFailures,
                LastSuccessAt = p.LastSuccessAt,
                LastFailureAt = p.LastFailureAt,
                LastError = p.LastError,
                SentToday = s?.Sent ?? 0,
                FailedToday = s?.Failed ?? 0
            };
        }).ToList();

        // recent security events
        var security = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.Category == AuditCategory.Security || a.Severity != AuditSeverity.Info || (a.Category == AuditCategory.Authentication && !a.Success))
            .OrderByDescending(a => a.Timestamp).Take(12).ToListAsync(ct);
        var clientNames = await ClientNamesAsync(security.Where(a => a.ClientId.HasValue).Select(a => a.ClientId!.Value), ct);
        dto.RecentSecurity = security.Select(a => Mappers.ToDto(a, a.ClientId.HasValue ? clientNames.GetValueOrDefault(a.ClientId.Value) : null)).ToList();

        // recent failures
        var failures = await _db.Messages.AsNoTracking().Include(m => m.Client).Include(m => m.SenderProfile).Include(m => m.Attachments)
            .Where(m => m.Status == EmailStatus.Failed)
            .OrderByDescending(m => m.CompletedAt).Take(8).ToListAsync(ct);
        dto.RecentFailures = failures.Select(Mappers.ToSummary).ToList();

        return dto;
    }

    public async Task<UsageReportDto> GetUsageAsync(DateTime from, DateTime to, int? clientId, CancellationToken ct = default)
    {
        from = DateTime.SpecifyKind(from, DateTimeKind.Utc);
        to = DateTime.SpecifyKind(to, DateTimeKind.Utc);
        var report = new UsageReportDto { From = from, To = to };

        report.ByClient = await ClientUsageAsync(from, to, clientId, ct);
        report.ByDay = await DailySeriesAsync(from.Date, to, clientId, ct);

        var q = _db.Messages.Where(m => m.CreatedAt >= from && m.CreatedAt < to);
        if (clientId.HasValue) q = q.Where(m => m.ClientId == clientId);

        report.ByStatus = (await q.GroupBy(m => m.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct))
            .Select(s => new StatusCountDto { Status = s.Key.ToApiString(), Count = s.Count }).ToList();

        var byProfile = await q.GroupBy(m => m.SenderProfileId).Select(g => new
        {
            g.Key,
            Sent = g.Sum(m => (m.Status == EmailStatus.Sent) ? 1 : 0),
            Failed = g.Sum(m => (m.Status == EmailStatus.Failed) ? 1 : 0),
            Avg = g.Average(m => m.Status == EmailStatus.Sent ? (double?)m.DurationMs : null)
        }).ToListAsync(ct);
        var profiles = await _db.SenderProfiles.AsNoTracking().ToDictionaryAsync(p => p.Id, ct);
        report.ByProfile = byProfile.Select(p => new ProfileUsageDto
        {
            ProfileId = p.Key,
            ProfileName = profiles.TryGetValue(p.Key, out var sp) ? sp.Name : $"#{p.Key}",
            Type = profiles.TryGetValue(p.Key, out var sp2) ? sp2.Type.ToString() : "?",
            Sent = p.Sent,
            Failed = p.Failed,
            AvgDurationMs = p.Avg
        }).OrderByDescending(p => p.Sent).ToList();

        report.TopErrors = (await q.Where(m => m.Status == EmailStatus.Failed && m.LastError != null)
            .GroupBy(m => m.LastError!).Select(g => new { g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count).Take(10).ToListAsync(ct))
            .Select(e => new ErrorCountDto { Error = e.Key.Length > 200 ? e.Key[..200] : e.Key, Count = e.Count }).ToList();

        return report;
    }

    private async Task<List<TimeBucketDto>> DailySeriesAsync(DateTime from, DateTime to, int? clientId, CancellationToken ct)
    {
        var list = new List<TimeBucketDto>();
        for (var d = from.Date; d < to; d = d.AddDays(1))
        {
            var end = d.AddDays(1);
            var q = _db.Messages.AsQueryable();
            if (clientId.HasValue) q = q.Where(m => m.ClientId == clientId);
            list.Add(new TimeBucketDto
            {
                Bucket = d,
                Accepted = await q.CountAsync(m => m.CreatedAt >= d && m.CreatedAt < end, ct),
                Sent = await q.CountAsync(m => m.Status == EmailStatus.Sent && m.SentAt >= d && m.SentAt < end, ct),
                Failed = await q.CountAsync(m => m.Status == EmailStatus.Failed && m.CompletedAt >= d && m.CompletedAt < end, ct)
            });
        }
        return list;
    }

    private async Task<List<ClientUsageDto>> ClientUsageAsync(DateTime from, DateTime to, int? clientId, CancellationToken ct)
    {
        var q = _db.Messages.Where(m => m.CreatedAt >= from && m.CreatedAt < to);
        if (clientId.HasValue) q = q.Where(m => m.ClientId == clientId);

        var usage = await q.GroupBy(m => m.ClientId).Select(g => new
        {
            ClientId = g.Key,
            Accepted = g.Count(),
            Sent = g.Sum(m => (m.Status == EmailStatus.Sent) ? 1 : 0),
            Failed = g.Sum(m => (m.Status == EmailStatus.Failed) ? 1 : 0),
            Queued = g.Sum(m => (m.Status == EmailStatus.Queued || m.Status == EmailStatus.Scheduled || m.Status == EmailStatus.Sending) ? 1 : 0),
            Recipients = g.Sum(m => m.RecipientCount),
            Avg = g.Average(m => m.Status == EmailStatus.Sent ? (double?)m.DurationMs : null)
        }).ToListAsync(ct);

        var rq = _db.RequestLogs.Where(r => r.Timestamp >= from && r.Timestamp < to && r.ClientId != null);
        if (clientId.HasValue) rq = rq.Where(r => r.ClientId == clientId);
        var requests = await rq.GroupBy(r => r.ClientId).Select(g => new { ClientId = g.Key, Count = g.Count() }).ToListAsync(ct);

        var clients = await _db.Clients.AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        var ids = usage.Select(u => u.ClientId).Concat(requests.Select(r => r.ClientId)).Distinct().ToList();

        return ids.Select(id =>
        {
            var u = usage.FirstOrDefault(x => x.ClientId == id);
            var r = requests.FirstOrDefault(x => x.ClientId == id);
            var c = id.HasValue && clients.TryGetValue(id.Value, out var cl) ? cl : null;
            return new ClientUsageDto
            {
                ClientId = id,
                ClientName = c?.Name ?? (id.HasValue ? $"#{id}" : "(system)"),
                Accepted = u?.Accepted ?? 0,
                Sent = u?.Sent ?? 0,
                Failed = u?.Failed ?? 0,
                Queued = u?.Queued ?? 0,
                Recipients = u?.Recipients ?? 0,
                AvgDurationMs = u?.Avg,
                Requests = r?.Count ?? 0,
                DailyQuota = c?.DailyQuota ?? 0
            };
        }).OrderByDescending(x => x.Accepted).ToList();
    }

    private async Task<Dictionary<int, string>> ClientNamesAsync(IEnumerable<int> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return new();
        return await _db.Clients.AsNoTracking().Where(c => list.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
    }
}
